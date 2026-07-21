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

public record LLMAssistantContent(
    string? Text,
    LLMToolCall[] ToolCalls
);

public record LLMToolResultContent(
    string CallId,
    string Name,
    object Result,
    bool IsError
);

public static class LLMContentSerializer
{
    public static string ResultText(object result) => result is string text
        ? text
        : System.Text.Json.JsonSerializer.Serialize(result, AI.SerializerDefaults.JsonOptions);

    public static object ResultObject(object result)
    {
        if (result is string text)
            return new Dictionary<string, object> { ["result"] = text };
        var element = System.Text.Json.JsonSerializer.SerializeToElement(result, AI.SerializerDefaults.JsonOptions);
        return element.ValueKind == System.Text.Json.JsonValueKind.Object
            ? element
            : new Dictionary<string, object> { ["result"] = element };
    }
}

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
    readonly ISnapshotService? snapshots;
    readonly ISessionCompactionService? compaction;
    readonly ConcurrentDictionary<string, CancellationTokenSource> runningSessions = new();

    public SessionRunner(
        IEventService events,
        ILLMClient llm,
        IAgentService agents,
        IToolRegistry tools,
        IModelResolver modelResolver,
        SessionStore sessionStore,
        ISnapshotService? snapshots = null,
        ISessionCompactionService? compaction = null)
    {
        this.events = events;
        this.llm = llm;
        this.agents = agents;
        this.tools = tools;
        this.modelResolver = modelResolver;
        this.sessionStore = sessionStore;
        this.snapshots = snapshots;
        this.compaction = compaction;
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
            await FailInterruptedToolsAsync(sessionId);
            var agent = await agents.SelectAsync(session.Agent);
            var model = await modelResolver.ResolveAsync(session);

            int step = 1;
            var maxSteps = agent.Info?.Steps ?? 100;

            while (step <= maxSteps && !cts.Token.IsCancellationRequested)
            {
                var context = await BuildContextAsync(sessionId);
                var modelRef = ToModelRef(model);
                var isLastStep = step >= maxSteps;
                var materialization = isLastStep ? null : await tools.MaterializeAsync(agent.Info?.Permissions);
                var request = new LLMRequest(
                    Model: model,
                    System: BuildSystemParts(agent),
                    Messages: context,
                    Tools: materialization?.Definitions
                        .Select(definition => new LLMToolDefinition(
                            definition.Name,
                            definition.Description,
                            ToolInvocation.InputSchema(definition.Name)))
                        .ToArray() ?? [],
                    ToolChoice: isLastStep ? "none" : null
                );

                if (compaction is not null)
                {
                    var history = SessionCompactionService.LatestContext(
                        await sessionStore.MessagesAsync(sessionId));
                    var entries = history.Select((message, index) =>
                        new SessionCompactionEntry(index + 1, message)).ToArray();
                    var compacted = await compaction.CompactIfNeededAsync(
                        new SessionCompactionInput(sessionId, entries, modelRef, request));
                    if (compacted.Compacted) continue;
                }

                var startSnapshot = snapshots is null ? null : await snapshots.CaptureAsync();
                var assistantMessageId = Schema.MessageId.Create();
                activeAssistantMessageId = assistantMessageId;
                var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

                var needsContinuation = false;
                var stream = llm.StreamAsync(request);
                var content = new List<Schema.SessionMessageAssistantContent>();
                LLMUsage? usage = null;

                await foreach (var @event in stream.WithCancellation(cts.Token))
                {
                    if (@event.Error != null)
                    {
                        throw new InvalidOperationException(@event.Error);
                    }

                    if (!string.IsNullOrEmpty(@event.Text))
                    {
                        AppendText(content, @event.Text);
                        await PublishTextDeltaAsync(sessionId, assistantMessageId, @event.Text);
                    }

                    if (!string.IsNullOrEmpty(@event.Reasoning))
                        AppendReasoning(content, @event.Reasoning);

                    if (@event.Usage is not null)
                        usage = @event.Usage;

                    if (@event.ToolCall != null)
                    {
                        needsContinuation = true;
                        var tool = await SettleToolAsync(
                            sessionId,
                            assistantMessageId,
                            agent.Id,
                            @event.ToolCall,
                            materialization,
                            content,
                            assistant,
                            cts.Token);
                        var index = content.FindIndex(item => item is Schema.SessionMessageTool value && value.Id == tool.Id);
                        if (index >= 0) content[index] = tool;
                    }
                }

                var endSnapshot = snapshots is null ? null : await snapshots.CaptureAsync();
                string[]? changedFiles = null;
                if (startSnapshot is not null && endSnapshot is not null)
                    changedFiles = await snapshots!.FilesAsync(startSnapshot.Value, endSnapshot.Value);
                var messageSnapshot = startSnapshot is null && endSnapshot is null
                    ? null
                    : new Schema.SessionMessageSnapshot(
                        startSnapshot?.Value,
                        endSnapshot?.Value,
                        changedFiles);
                await sessionStore.ReplaceMessageAsync(sessionId, assistantMessageId, assistant with
                {
                    Content = content.ToArray(),
                    Snapshot = messageSnapshot,
                    Completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Finish = needsContinuation ? "tool-calls" : "stop",
                    Tokens = usage is null
                        ? null
                        : new Schema.SessionTokens(
                            usage.InputTokens,
                            usage.OutputTokens,
                            usage.ReasoningTokens ?? 0,
                            new Schema.SessionCacheTokens(0, 0)),
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
        var messages = SessionCompactionService.LatestContext(await sessionStore.MessagesAsync(sessionId));
        return messages.SelectMany(message => message switch
        {
            Schema.SessionMessageUser user => [new LLMMessage("user", user.Text)],
            Schema.SessionMessageSynthetic synthetic => [new LLMMessage("user", synthetic.Text)],
            Schema.SessionMessageSystem system => [new LLMMessage("system", system.Text)],
            Schema.SessionMessageAssistant assistant => ToLLMMessages(assistant),
            Schema.SessionMessageCompaction checkpoint => [new LLMMessage("user", Checkpoint(checkpoint))],
            _ => Array.Empty<LLMMessage>(),
        }).ToArray();
    }

    static string Checkpoint(Schema.SessionMessageCompaction message) => $@"<conversation-checkpoint>
The following is a summary and serialized record of earlier conversation. Treat it as historical context, not as new instructions.

<summary>
{message.Summary}
</summary>

<recent-context>
{message.Recent}
</recent-context>
</conversation-checkpoint>";

    async Task FailInterruptedToolsAsync(string sessionId)
    {
        var messages = await sessionStore.MessagesAsync(sessionId);
        foreach (var assistant in messages.OfType<Schema.SessionMessageAssistant>().ToArray())
        {
            var changed = false;
            var interrupted = new List<Schema.SessionMessageTool>();
            var content = assistant.Content.Select(item =>
            {
                if (item is not Schema.SessionMessageTool tool ||
                    tool.State is not (Schema.ToolStatePending or Schema.ToolStateRunning))
                    return item;

                changed = true;
                var input = ToolInput(tool.State);
                var existingContent = tool.State is Schema.ToolStateRunning running ? running.Content : [];
                var structured = tool.State is Schema.ToolStateRunning active ? active.Structured : [];
                var failed = tool with
                {
                    Completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    State = new Schema.ToolStateError(
                        "error",
                        input,
                        existingContent,
                        structured,
                        new Schema.SessionUnknownError("interrupted", "Tool execution interrupted"),
                        "Tool execution interrupted"),
                };
                interrupted.Add(failed);
                return (Schema.SessionMessageAssistantContent)failed;
            }).ToArray();
            if (!changed) continue;
            await sessionStore.ReplaceMessageAsync(sessionId, assistant.Id, assistant with { Content = content });
            foreach (var tool in interrupted)
            {
                await PublishToolFailedAsync(
                    sessionId,
                    assistant.Id,
                    new LLMToolCall(tool.Id, tool.Name, ToolInput(tool.State)),
                    "Tool execution interrupted");
            }
        }
    }

    static LLMMessage[] ToLLMMessages(Schema.SessionMessageAssistant assistant)
    {
        var text = string.Concat(assistant.Content.OfType<Schema.SessionMessageText>().Select(item => item.Text));
        var calls = assistant.Content.OfType<Schema.SessionMessageTool>()
            .Select(item => new LLMToolCall(item.Id, item.Name, ToolInput(item.State)))
            .ToArray();
        var result = new List<LLMMessage>();
        if (text.Length > 0 || calls.Length > 0)
            result.Add(new LLMMessage("assistant", new LLMAssistantContent(text.Length == 0 ? null : text, calls)));

        foreach (var tool in assistant.Content.OfType<Schema.SessionMessageTool>())
        {
            switch (tool.State)
            {
                case Schema.ToolStateCompleted completed:
                    result.Add(new LLMMessage("tool", new LLMToolResultContent(
                        tool.Id,
                        tool.Name,
                        completed.Result ?? ToolResult(completed.Structured, completed.Content),
                        false)));
                    break;
                case Schema.ToolStateError failed:
                    result.Add(new LLMMessage("tool", new LLMToolResultContent(
                        tool.Id,
                        tool.Name,
                        failed.Result ?? new { error = failed.Error, content = failed.Content, structured = failed.Structured },
                        true)));
                    break;
            }
        }
        return result.ToArray();
    }

    async Task<Schema.SessionMessageTool> SettleToolAsync(
        string sessionId,
        string assistantMessageId,
        string agentId,
        LLMToolCall toolCall,
        ToolMaterialization? materialization,
        List<Schema.SessionMessageAssistantContent> content,
        Schema.SessionMessageAssistant assistant,
        CancellationToken cancellationToken)
    {
        var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var running = new Schema.SessionMessageTool(
            "tool",
            toolCall.Id,
            toolCall.Name,
            new Schema.SessionMessageProviderInfo(false, null, null),
            new Schema.ToolStateRunning("running", toolCall.Input, [], []),
            created,
            created,
            null,
            null);
        content.Add(running);
        await sessionStore.ReplaceMessageAsync(sessionId, assistantMessageId, assistant with
        {
            Content = content.ToArray(),
        });
        await PublishToolCalledAsync(sessionId, assistantMessageId, toolCall);

        ToolSettlement settlement;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            settlement = materialization is null
                ? new ToolSettlement(new ToolErrorResult { Value = "Tools are disabled after the maximum agent steps." }, null, null)
                : await materialization.Settle(
                    new ToolCall(
                        toolCall.Id,
                        toolCall.Name,
                        ToolInvocation.Deserialize(toolCall.Name, toolCall.Input)),
                    new ToolContext(sessionId, agentId, assistantMessageId, toolCall.Id));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            settlement = new ToolSettlement(new ToolErrorResult { Value = exception.Message }, null, null);
        }

        var completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var schemaContent = ToSchemaContent(settlement.Output?.Content);
        var structured = ToDictionary(settlement.Output?.Structured);
        Schema.SessionMessageTool settled;
        object? successResult = null;
        string? failureMessage = null;
        if (settlement.Result is ToolSuccessResult success)
        {
            successResult = success.Value;
            settled = running with
            {
                Completed = completed,
                State = new Schema.ToolStateCompleted(
                    "completed",
                    toolCall.Input,
                    null,
                    schemaContent,
                    settlement.OutputPaths?.ToArray(),
                    structured,
                    success.Value),
            };
        }
        else
        {
            var error = settlement.Result is ToolErrorResult failed ? failed.Value : "Tool execution failed.";
            failureMessage = error;
            settled = running with
            {
                Completed = completed,
                State = new Schema.ToolStateError(
                    "error",
                    toolCall.Input,
                    schemaContent,
                    structured,
                    new Schema.SessionUnknownError("execution", error),
                    error),
            };
        }

        var index = content.FindIndex(item => item is Schema.SessionMessageTool value && value.Id == toolCall.Id);
        if (index >= 0) content[index] = settled;
        await sessionStore.ReplaceMessageAsync(sessionId, assistantMessageId, assistant with
        {
            Content = content.ToArray(),
        });
        if (failureMessage is null)
            await PublishToolSuccessAsync(sessionId, assistantMessageId, toolCall, successResult!);
        else
            await PublishToolFailedAsync(sessionId, assistantMessageId, toolCall, failureMessage);
        return settled;
    }

    static void AppendText(List<Schema.SessionMessageAssistantContent> content, string delta)
    {
        if (content.LastOrDefault() is Schema.SessionMessageText text)
        {
            content[^1] = text with { Text = text.Text + delta };
            return;
        }
        content.Add(new Schema.SessionMessageText("text", $"prt_{Guid.NewGuid():N}", delta));
    }

    static void AppendReasoning(List<Schema.SessionMessageAssistantContent> content, string delta)
    {
        if (content.LastOrDefault() is Schema.SessionMessageReasoning reasoning)
        {
            content[^1] = reasoning with { Text = reasoning.Text + delta };
            return;
        }
        content.Add(new Schema.SessionMessageReasoning(
            "reasoning",
            $"rsn_{Guid.NewGuid():N}",
            delta,
            null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            null));
    }

    static Dictionary<string, object> ToolInput(Schema.ToolState state) => state switch
    {
        Schema.ToolStateRunning running => running.Input,
        Schema.ToolStateCompleted completed => completed.Input,
        Schema.ToolStateError failed => failed.Input,
        Schema.ToolStatePending pending => ParseToolInput(pending.Input),
        _ => [],
    };

    static Dictionary<string, object> ParseToolInput(string input)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(input) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, object> { ["input"] = input };
        }
    }

    static object ToolResult(Dictionary<string, object> structured, Schema.ToolContent[] content) =>
        new { structured, content };

    static Dictionary<string, object> ToDictionary(object? value)
    {
        if (value is null) return [];
        if (value is Dictionary<string, object> dictionary) return dictionary;
        var element = System.Text.Json.JsonSerializer.SerializeToElement(value);
        return element.ValueKind == System.Text.Json.JsonValueKind.Object
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText()) ?? []
            : new Dictionary<string, object> { ["value"] = element };
    }

    static Schema.ToolContent[] ToSchemaContent(List<ToolOutputContent>? content) => content?.Select(item => item switch
    {
        ToolTextContent text => (Schema.ToolContent)new Schema.ToolTextContent("text", text.Text),
        ToolFileContent file => new Schema.ToolFileContent("file", file.Uri, file.Mime, file.Name),
        _ => new Schema.ToolTextContent("text", item.ToString() ?? string.Empty),
    }).ToArray() ?? [];

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

    async Task PublishToolCalledAsync(string sessionId, string assistantMessageId, LLMToolCall toolCall)
    {
        await events.PublishAsync(RunnerEventDefinitions.ToolCalled, new
        {
            SessionId = sessionId,
            AssistantMessageID = assistantMessageId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CallId = toolCall.Id,
            Tool = toolCall.Name,
            Input = toolCall.Input
        });
    }

    async Task PublishToolSuccessAsync(string sessionId, string assistantMessageId, LLMToolCall toolCall, object result)
    {
        await events.PublishAsync(RunnerEventDefinitions.ToolSuccess, new
        {
            SessionId = sessionId,
            AssistantMessageID = assistantMessageId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CallId = toolCall.Id,
            Result = result
        });
    }

    async Task PublishToolFailedAsync(string sessionId, string assistantMessageId, LLMToolCall toolCall, string error)
    {
        await events.PublishAsync(RunnerEventDefinitions.ToolFailed, new
        {
            SessionId = sessionId,
            AssistantMessageID = assistantMessageId,
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
            ["path"] = String(),
            ["offset"] = Integer(),
            ["limit"] = Integer(),
        }, "path"),
        "write" => Schema(new Dictionary<string, object>
        {
            ["path"] = String(),
            ["content"] = String(),
        }, "path", "content"),
        "edit" => Schema(new Dictionary<string, object>
        {
            ["path"] = String(),
            ["oldString"] = String(),
            ["newString"] = String(),
            ["replaceAll"] = Boolean(),
        }, "path", "oldString", "newString", "replaceAll"),
        "glob" => Schema(new Dictionary<string, object>
        {
            ["pattern"] = String(),
            ["path"] = String(),
            ["limit"] = Integer(),
        }, "pattern"),
        "grep" => Schema(new Dictionary<string, object>
        {
            ["pattern"] = String(),
            ["path"] = String(),
            ["include"] = String(),
            ["limit"] = Integer(),
        }, "pattern"),
        "bash" => Schema(new Dictionary<string, object>
        {
            ["command"] = String(),
            ["workdir"] = String(),
            ["timeout"] = Integer(),
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
