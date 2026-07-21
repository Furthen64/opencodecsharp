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

        string? activeAssistantMessageId = null;
        try
        {
            var agent = await agents.SelectAsync(session.Agent);
            var model = await modelResolver.ResolveAsync(session);

            int step = 1;
            var maxSteps = agent.Info?.Steps ?? 100;

            while (step <= maxSteps && !cts.Token.IsCancellationRequested)
            {
                var context = await BuildContextAsync(sessionId);
                var assistantMessageId = Schema.MessageId.Create();
                activeAssistantMessageId = assistantMessageId;
                var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var modelRef = ToModelRef(model);
                var assistant = new Schema.SessionMessageAssistant(
                    assistantMessageId, null, created, "assistant", agent.Id, modelRef,
                    Array.Empty<Schema.SessionMessageAssistantContent>(), null, null, null, null, null, null);
                await sessionStore.AddMessageAsync(sessionId, assistant);
                await events.PublishAsync(RunnerEventDefinitions.StepStarted, new
                {
                    SessionId = sessionId,
                    AssistantMessageID = assistantMessageId,
                    Agent = agent.Id,
                    ModelId = modelRef.Id,
                    ProviderId = modelRef.ProviderId,
                    Variant = modelRef.Variant,
                });

                var request = new LLMRequest(
                    Model: model,
                    System: BuildSystemParts(agent),
                    Messages: context,
                    Tools: (await tools.MaterializeAsync()).Definitions
                        .Select(definition => new LLMToolDefinition(
                            definition.Name,
                            definition.Description,
                            ToolInvocation.InputSchema(definition.Name)))
                        .ToArray(),
                    ToolChoice: null
                );

                var needsContinuation = false;
                var stream = llm.StreamAsync(request);
                var text = new System.Text.StringBuilder();

                await foreach (var @event in stream.WithCancellation(cts.Token))
                {
                    if (@event.Error != null)
                    {
                        throw new InvalidOperationException(@event.Error);
                    }

                    if (!string.IsNullOrEmpty(@event.Text))
                    {
                        text.Append(@event.Text);
                        await PublishTextDeltaAsync(sessionId, assistantMessageId, @event.Text);
                    }

                    if (@event.ToolCall != null)
                    {
                        await PublishToolCalledAsync(sessionId, @event.ToolCall);

                        try
                        {
                            var tool = tools.GetTool(@event.ToolCall.Name);
                            if (tool != null)
                            {
                                var ctx = new ToolContext(sessionId, agent.Id, string.Empty, @event.ToolCall.Id);
                                var result = await tool.ExecuteAsync(
                                    ToolInvocation.Deserialize(@event.ToolCall.Name, @event.ToolCall.Input), ctx);
                                await PublishToolSuccessAsync(sessionId, @event.ToolCall, result);
                            }
                            else
                            {
                                await PublishToolFailedAsync(sessionId, @event.ToolCall, "Tool is not registered.");
                            }
                        }
                        catch (Exception ex)
                        {
                            await PublishToolFailedAsync(sessionId, @event.ToolCall, ex.Message);
                        }
                    }
                }

                var content = text.Length == 0
                    ? Array.Empty<Schema.SessionMessageAssistantContent>()
                    : [new Schema.SessionMessageText("text", $"prt_{Guid.NewGuid():N}", text.ToString())];
                await sessionStore.ReplaceMessageAsync(sessionId, assistantMessageId, assistant with
                {
                    Content = content,
                    Completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Finish = needsContinuation ? "tool-calls" : "stop",
                });
                await events.PublishAsync(RunnerEventDefinitions.StepEnded, new
                {
                    SessionId = sessionId,
                    AssistantMessageID = assistantMessageId,
                    Finish = needsContinuation ? "tool-calls" : "stop",
                });
                activeAssistantMessageId = null;

                step++;
                if (!needsContinuation) break;
            }
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            await PublishProviderErrorAsync(sessionId, activeAssistantMessageId, ex.Message);
        }
        finally
        {
            runningSessions.TryRemove(sessionId, out _);
            cts.Dispose();
        }
    }

    async Task<LLMMessage[]> BuildContextAsync(string sessionId)
    {
        var messages = await sessionStore.MessagesAsync(sessionId);
        return messages.SelectMany(message => message switch
        {
            Schema.SessionMessageUser user => [new LLMMessage("user", user.Text)],
            Schema.SessionMessageAssistant assistant =>
                assistant.Content.OfType<Schema.SessionMessageText>()
                    .Select(text => new LLMMessage("assistant", text.Text)),
            _ => Array.Empty<LLMMessage>(),
        }).ToArray();
    }

    static Schema.ModelRef ToModelRef(string value)
    {
        var parts = value.Split('/', 2);
        return parts.Length == 2
            ? new Schema.ModelRef(parts[1], parts[0], null)
            : new Schema.ModelRef(value, value, null);
    }

    static string[] BuildSystemParts(AgentSelection agent)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(agent.Info?.System))
            parts.Add(agent.Info.System);
        return parts.ToArray();
    }

    async Task PublishProviderErrorAsync(string sessionId, string? assistantMessageId, string error)
    {
        if (assistantMessageId is not null)
        {
            var assistant = (await sessionStore.MessagesAsync(sessionId))
                .OfType<Schema.SessionMessageAssistant>()
                .LastOrDefault(message => message.Id == assistantMessageId);
            if (assistant is not null)
            {
                await sessionStore.ReplaceMessageAsync(sessionId, assistantMessageId, assistant with
                {
                    Completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Finish = "error",
                    Error = new Schema.SessionUnknownError("provider", error),
                });
            }
        }
        await events.PublishAsync(RunnerEventDefinitions.StepFailed, new
        {
            SessionId = sessionId,
            AssistantMessageID = assistantMessageId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Error = new { Type = "provider", Message = error }
        });
    }

    async Task PublishTextDeltaAsync(string sessionId, string assistantMessageId, string text)
    {
        await events.PublishAsync(RunnerEventDefinitions.TextDelta, new
        {
            SessionId = sessionId,
            AssistantMessageID = assistantMessageId,
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

static class ToolInvocation
{
    public static Dictionary<string, object> InputSchema(string name) => name switch
    {
        "read" => Schema(new Dictionary<string, object>
        {
            ["path"] = String(), ["offset"] = Integer(), ["limit"] = Integer(),
        }, "path"),
        "write" => Schema(new Dictionary<string, object>
        {
            ["path"] = String(), ["content"] = String(),
        }, "path", "content"),
        "edit" => Schema(new Dictionary<string, object>
        {
            ["path"] = String(), ["oldString"] = String(), ["newString"] = String(), ["replaceAll"] = Boolean(),
        }, "path", "oldString", "newString", "replaceAll"),
        "glob" => Schema(new Dictionary<string, object>
        {
            ["pattern"] = String(), ["path"] = String(), ["limit"] = Integer(),
        }, "pattern"),
        "grep" => Schema(new Dictionary<string, object>
        {
            ["pattern"] = String(), ["path"] = String(), ["include"] = String(), ["limit"] = Integer(),
        }, "pattern"),
        "bash" => Schema(new Dictionary<string, object>
        {
            ["command"] = String(), ["workdir"] = String(), ["timeout"] = Integer(),
        }, "command"),
        _ => Schema(new Dictionary<string, object>()),
    };

    public static object Deserialize(string name, Dictionary<string, object> input)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(input);
        return name switch
        {
            "read" => Deserialize<ReadToolInput>(json),
            "write" => Deserialize<WriteToolInput>(json),
            "edit" => Deserialize<EditToolInput>(json),
            "glob" => Deserialize<GlobToolInput>(json),
            "grep" => Deserialize<GrepToolInput>(json),
            "bash" => Deserialize<BashToolInput>(json),
            _ => input,
        };
    }

    static T Deserialize<T>(string json) => System.Text.Json.JsonSerializer.Deserialize<T>(json)
        ?? throw new ToolFailure("Invalid tool input.");

    static Dictionary<string, object> Schema(Dictionary<string, object> properties, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = required,
        ["additionalProperties"] = false,
    };

    static Dictionary<string, object> String() => new() { ["type"] = "string" };
    static Dictionary<string, object> Integer() => new() { ["type"] = "integer" };
    static Dictionary<string, object> Boolean() => new() { ["type"] = "boolean" };
}

static class RunnerEventDefinitions
{
    public static EventDefinition StepStarted => new("session.next.step.started", true, "SessionId", 1);
    public static EventDefinition StepEnded => new("session.next.step.ended", true, "SessionId", 1);
    public static EventDefinition StepFailed => new("session.next.step.failed", true, "SessionId", 1);
    public static EventDefinition TextDelta => new("session.next.text.delta", true, "SessionId", 1);
    public static EventDefinition ToolCalled => new("session.next.tool.called", true, "SessionId", 1);
    public static EventDefinition ToolSuccess => new("session.next.tool.success", true, "SessionId", 1);
    public static EventDefinition ToolFailed => new("session.next.tool.failed", true, "SessionId", 1);
}
