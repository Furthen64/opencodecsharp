using System.Collections.Concurrent;

namespace OpenCode.Core;

public record LLMRequest(
    string Model,
    string[] System,
    LLMMessage[] Messages,
    LLMToolDefinition[]? Tools,
    string? ToolChoice
);

public record LLMMessage(
    string Role,
    object Content
);

public record LLMToolDefinition(
    string Name,
    string Description,
    Dictionary<string, object> InputSchema
);

public record LLMToolCall(
    string Id,
    string Name,
    Dictionary<string, object> Input
);

public record LLMStreamEvent(
    string Type,
    string? Text,
    LLMToolCall? ToolCall,
    string? Reasoning,
    LLMUsage? Usage,
    string? Error
);

public record LLMUsage(
    int InputTokens,
    int OutputTokens,
    int? ReasoningTokens
);

public interface IModelResolver
{
    Task<string> ResolveAsync(Schema.SessionInfo session);
}

public interface ILLMClient
{
    IAsyncEnumerable<LLMStreamEvent> StreamAsync(LLMRequest request);
}

public interface ISessionRunner
{
    Task RunAsync(string sessionId, bool force);
    Task InterruptAsync(string sessionId);
}

public class SessionRunner : ISessionRunner
{
    readonly IEventService events;
    readonly ILLMClient llm;
    readonly IAgentService agents;
    readonly IToolRegistry tools;
    readonly IModelResolver modelResolver;
    readonly SessionStore sessionStore;
    readonly ConcurrentDictionary<string, CancellationTokenSource> runningSessions = new();

    public SessionRunner(
        IEventService events,
        ILLMClient llm,
        IAgentService agents,
        IToolRegistry tools,
        IModelResolver modelResolver,
        SessionStore sessionStore)
    {
        this.events = events;
        this.llm = llm;
        this.agents = agents;
        this.tools = tools;
        this.modelResolver = modelResolver;
        this.sessionStore = sessionStore;
    }

    public async Task RunAsync(string sessionId, bool force)
    {
        var session = await sessionStore.GetAsync(sessionId);
        if (session == null)
            throw new SessionNotFoundError(sessionId);

        var cts = new CancellationTokenSource();
        if (!runningSessions.TryAdd(sessionId, cts))
            return;

        try
        {
            var agent = await agents.SelectAsync(session.Agent);
            var model = await modelResolver.ResolveAsync(session);

            int step = 1;
            var maxSteps = agent.Info?.Steps ?? 100;

            while (step <= maxSteps && !cts.Token.IsCancellationRequested)
            {
                var context = await BuildContextAsync(sessionId);

                var request = new LLMRequest(
                    Model: model,
                    System: BuildSystemParts(agent),
                    Messages: context,
                    Tools: null,
                    ToolChoice: null
                );

                var needsContinuation = false;
                var stream = llm.StreamAsync(request);

                await foreach (var @event in stream.WithCancellation(cts.Token))
                {
                    if (@event.Error != null)
                    {
                        await PublishProviderErrorAsync(sessionId, @event.Error);
                        return;
                    }

                    if (@event.Text != null)
                    {
                        await PublishTextDeltaAsync(sessionId, @event.Text);
                    }

                    if (@event.ToolCall != null)
                    {
                        needsContinuation = true;
                        await PublishToolCalledAsync(sessionId, @event.ToolCall);

                        try
                        {
                            var tool = tools.GetTool(@event.ToolCall.Name);
                            if (tool != null)
                            {
                                var ctx = new ToolContext(sessionId, agent.Id, string.Empty, @event.ToolCall.Id);
                                var result = await tool.ExecuteAsync(@event.ToolCall.Input, ctx);
                                await PublishToolSuccessAsync(sessionId, @event.ToolCall, result);
                            }
                        }
                        catch (Exception ex)
                        {
                            await PublishToolFailedAsync(sessionId, @event.ToolCall, ex.Message);
                        }
                    }
                }

                step++;
                if (!needsContinuation) break;
            }
        }
        finally
        {
            runningSessions.TryRemove(sessionId, out _);
            cts.Dispose();
        }
    }

    async Task<LLMMessage[]> BuildContextAsync(string sessionId)
    {
        return Array.Empty<LLMMessage>();
    }

    static string[] BuildSystemParts(AgentSelection agent)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(agent.Info?.System))
            parts.Add(agent.Info.System);
        return parts.ToArray();
    }

    async Task PublishProviderErrorAsync(string sessionId, string error)
    {
        await events.PublishAsync(RunnerEventDefinitions.StepFailed, new
        {
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Error = new { Type = "provider", Message = error }
        });
    }

    async Task PublishTextDeltaAsync(string sessionId, string text)
    {
        await events.PublishAsync(RunnerEventDefinitions.TextDelta, new
        {
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Delta = text
        });
    }

    async Task PublishToolCalledAsync(string sessionId, LLMToolCall toolCall)
    {
        await events.PublishAsync(RunnerEventDefinitions.ToolCalled, new
        {
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CallId = toolCall.Id,
            Tool = toolCall.Name,
            Input = toolCall.Input
        });
    }

    async Task PublishToolSuccessAsync(string sessionId, LLMToolCall toolCall, object result)
    {
        await events.PublishAsync(RunnerEventDefinitions.ToolSuccess, new
        {
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CallId = toolCall.Id,
            Result = result
        });
    }

    async Task PublishToolFailedAsync(string sessionId, LLMToolCall toolCall, string error)
    {
        await events.PublishAsync(RunnerEventDefinitions.ToolFailed, new
        {
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CallId = toolCall.Id,
            Error = new { Type = "execution", Message = error }
        });
    }

    public Task InterruptAsync(string sessionId)
    {
        if (runningSessions.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
        }
        return Task.CompletedTask;
    }
}

static class RunnerEventDefinitions
{
    public static EventDefinition StepFailed => new("session.step.failed", true, "SessionId", 1);
    public static EventDefinition TextDelta => new("session.text.delta", true, "SessionId", 1);
    public static EventDefinition ToolCalled => new("session.tool.called", true, "SessionId", 1);
    public static EventDefinition ToolSuccess => new("session.tool.success", true, "SessionId", 1);
    public static EventDefinition ToolFailed => new("session.tool.failed", true, "SessionId", 1);
}