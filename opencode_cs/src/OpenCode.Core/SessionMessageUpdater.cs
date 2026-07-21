using System.Text.Json;

namespace OpenCode.Core;

public static class SessionMessageUpdater
{
    public static void Update(List<Schema.SessionMessageBase> messages, EventPayload @event)
    {
        switch (@event.Type)
        {
            case "session.next.agent.switched":
                messages.Add(new Schema.SessionMessageAgentSwitched(
                    Id: ExtractField<string>(@event.Data, "messageID") ?? GenerateId(),
                    Metadata: ExtractField<Dictionary<string, object>>(@event.Data, "metadata"),
                    Created: @event.Timestamp,
                    Type: "agent-switched",
                    Agent: ExtractField<string>(@event.Data, "agent") ?? ""
                ));
                break;

            case "session.next.model.switched":
                var modelData = ExtractField<Dictionary<string, object>>(@event.Data, "model");
                var modelRef = modelData != null
                    ? new Schema.ModelRef(
                        Id: GetDictString(modelData, "id") ?? "",
                        ProviderId: GetDictString(modelData, "providerID") ?? "",
                        Variant: GetDictString(modelData, "variant")
                    )
                    : new Schema.ModelRef("", "", null);
                messages.Add(new Schema.SessionMessageModelSwitched(
                    Id: ExtractField<string>(@event.Data, "messageID") ?? GenerateId(),
                    Metadata: ExtractField<Dictionary<string, object>>(@event.Data, "metadata"),
                    Created: @event.Timestamp,
                    Type: "model-switched",
                    Model: modelRef
                ));
                break;

            case "session.next.prompted":
                var promptData = ExtractField<Dictionary<string, object>>(@event.Data, "prompt");
                messages.Add(new Schema.SessionMessageUser(
                    Id: ExtractField<string>(@event.Data, "messageID") ?? GenerateId(),
                    Metadata: ExtractField<Dictionary<string, object>>(@event.Data, "metadata"),
                    Created: @event.Timestamp,
                    Type: "user",
                    Text: promptData != null ? GetDictString(promptData, "text") ?? "" : "",
                    Files: null,
                    Agents: null
                ));
                break;

            case "session.next.context.updated":
                messages.Add(new Schema.SessionMessageSystem(
                    Id: ExtractField<string>(@event.Data, "messageID") ?? GenerateId(),
                    Metadata: null,
                    Created: @event.Timestamp,
                    Type: "system",
                    Text: ExtractField<string>(@event.Data, "text") ?? ""
                ));
                break;

            case "session.next.synthetic":
                messages.Add(new Schema.SessionMessageSynthetic(
                    Id: ExtractField<string>(@event.Data, "messageID") ?? GenerateId(),
                    Metadata: null,
                    Created: @event.Timestamp,
                    Type: "synthetic",
                    SessionId: ExtractField<string>(@event.Data, "sessionID") ?? "",
                    Text: ExtractField<string>(@event.Data, "text") ?? ""
                ));
                break;

            case "session.next.shell.started":
                messages.Add(new Schema.SessionMessageShell(
                    Id: ExtractField<string>(@event.Data, "messageID") ?? GenerateId(),
                    Metadata: ExtractField<Dictionary<string, object>>(@event.Data, "metadata"),
                    Created: @event.Timestamp,
                    Type: "shell",
                    CallId: ExtractField<string>(@event.Data, "callID") ?? "",
                    Command: ExtractField<string>(@event.Data, "command") ?? "",
                    Output: "",
                    Completed: null
                ));
                break;

            case "session.next.shell.ended":
                var callId = ExtractField<string>(@event.Data, "callID") ?? "";
                var shellMsg = messages.OfType<Schema.SessionMessageShell>()
                    .LastOrDefault(s => s.CallId == callId);
                if (shellMsg != null)
                {
                    var idx = messages.IndexOf(shellMsg);
                    messages[idx] = shellMsg with
                    {
                        Output = ExtractField<string>(@event.Data, "output") ?? "",
                        Completed = @event.Timestamp
                    };
                }
                break;

            case "session.next.step.started":
                var currentAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Completed == null);
                if (currentAssistant != null)
                {
                    var idx = messages.IndexOf(currentAssistant);
                    messages[idx] = currentAssistant with { Completed = @event.Timestamp };
                }
                var agentId = ExtractField<string>(@event.Data, "agent") ?? "";
                var modelRef2 = new Schema.ModelRef(
                    Id: ExtractField<string>(@event.Data, "modelId") ?? "",
                    ProviderId: ExtractField<string>(@event.Data, "providerId") ?? "",
                    Variant: ExtractField<string>(@event.Data, "variant")
                );
                messages.Add(new Schema.SessionMessageAssistant(
                    Id: ExtractField<string>(@event.Data, "assistantMessageID") ?? GenerateId(),
                    Metadata: null,
                    Created: @event.Timestamp,
                    Type: "assistant",
                    Agent: agentId,
                    Model: modelRef2,
                    Content: Array.Empty<Schema.SessionMessageAssistantContent>(),
                    Snapshot: null,
                    Finish: null,
                    Cost: null,
                    Tokens: null,
                    Error: null,
                    Completed: null
                ));
                break;

            case "session.next.step.ended":
                var endedId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var endedAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == endedId);
                if (endedAssistant != null)
                {
                    var idx = messages.IndexOf(endedAssistant);
                    messages[idx] = endedAssistant with
                    {
                        Completed = @event.Timestamp,
                        Finish = ExtractField<string>(@event.Data, "finish"),
                        Cost = ExtractField<double?>(@event.Data, "cost")
                    };
                }
                break;

            case "session.next.step.failed":
                var failedId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var failedAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == failedId);
                if (failedAssistant != null)
                {
                    var errorMsg = ExtractField<Dictionary<string, object>>(@event.Data, "error");
                    var idx = messages.IndexOf(failedAssistant);
                    messages[idx] = failedAssistant with
                    {
                        Completed = @event.Timestamp,
                        Finish = "error",
                        Error = errorMsg != null
                            ? new Schema.SessionUnknownError(
                                Type: GetDictString(errorMsg, "type") ?? "unknown",
                                Message: GetDictString(errorMsg, "message") ?? ""
                            )
                            : null
                    };
                }
                break;

            case "session.next.text.started":
                var textAssistantId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var textAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == textAssistantId);
                if (textAssistant != null)
                {
                    var idx = messages.IndexOf(textAssistant);
                    var newContent = textAssistant.Content.Append(new Schema.SessionMessageText(
                        Type: "text",
                        Id: ExtractField<string>(@event.Data, "textID") ?? GenerateId(),
                        Text: ""
                    )).ToArray();
                    messages[idx] = textAssistant with { Content = newContent };
                }
                break;

            case "session.next.text.delta":
                var deltaTextAssistantId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var textId = ExtractField<string>(@event.Data, "textID") ?? "";
                var deltaAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == deltaTextAssistantId);
                if (deltaAssistant != null)
                {
                    var idx = messages.IndexOf(deltaAssistant);
                    var updatedContent = deltaAssistant.Content.Select(c =>
                    {
                        if (c is Schema.SessionMessageText t && t.Id == textId)
                            return (Schema.SessionMessageAssistantContent)(t with { Text = t.Text + (ExtractField<string>(@event.Data, "delta") ?? "") });
                        return c;
                    }).ToArray();
                    messages[idx] = deltaAssistant with { Content = updatedContent };
                }
                break;

            case "session.next.text.ended":
                var endTextAssistantId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var endTextId = ExtractField<string>(@event.Data, "textID") ?? "";
                var endTextAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == endTextAssistantId);
                if (endTextAssistant != null)
                {
                    var idx = messages.IndexOf(endTextAssistant);
                    var updatedContent = endTextAssistant.Content.Select(c =>
                    {
                        if (c is Schema.SessionMessageText t && t.Id == endTextId)
                            return (Schema.SessionMessageAssistantContent)(t with { Text = ExtractField<string>(@event.Data, "text") ?? t.Text });
                        return c;
                    }).ToArray();
                    messages[idx] = endTextAssistant with { Content = updatedContent };
                }
                break;

            case "session.next.tool.input.started":
                var toolInputAssistantId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var toolInputAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == toolInputAssistantId);
                if (toolInputAssistant != null)
                {
                    var idx = messages.IndexOf(toolInputAssistant);
                    var newContent = toolInputAssistant.Content.Append(new Schema.SessionMessageTool(
                        Type: "tool",
                        Id: ExtractField<string>(@event.Data, "callID") ?? GenerateId(),
                        Name: ExtractField<string>(@event.Data, "name") ?? "",
                        Provider: null,
                        State: new Schema.ToolStatePending("pending", ""),
                        Created: @event.Timestamp,
                        Ran: null,
                        Completed: null,
                        Pruned: null
                    )).ToArray();
                    messages[idx] = toolInputAssistant with { Content = newContent };
                }
                break;

            case "session.next.tool.input.ended":
                var toolInputEndAssistantId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var callId2 = ExtractField<string>(@event.Data, "callID") ?? "";
                var toolInputEndAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == toolInputEndAssistantId);
                if (toolInputEndAssistant != null)
                {
                    var idx = messages.IndexOf(toolInputEndAssistant);
                    var updatedContent = toolInputEndAssistant.Content.Select(c =>
                    {
                        if (c is Schema.SessionMessageTool tool && tool.Id == callId2 && tool.State is Schema.ToolStatePending pending)
                            return (Schema.SessionMessageAssistantContent)(tool with
                            {
                                State = pending with { Input = ExtractField<string>(@event.Data, "text") ?? pending.Input }
                            });
                        return c;
                    }).ToArray();
                    messages[idx] = toolInputEndAssistant with { Content = updatedContent };
                }
                break;

            case "session.next.tool.called":
                var toolCalledAssistantId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var toolCalledCallId = ExtractField<string>(@event.Data, "callID") ?? "";
                var toolCalledAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == toolCalledAssistantId);
                if (toolCalledAssistant != null)
                {
                    var idx = messages.IndexOf(toolCalledAssistant);
                    var updatedContent = toolCalledAssistant.Content.Select(c =>
                    {
                        if (c is Schema.SessionMessageTool tool && tool.Id == toolCalledCallId)
                        {
                            var inputObj = ExtractField<Dictionary<string, object>>(@event.Data, "input") ?? new();
                            return (Schema.SessionMessageAssistantContent)(tool with
                            {
                                Ran = @event.Timestamp,
                                State = new Schema.ToolStateRunning("running", inputObj, new(), Array.Empty<Schema.ToolContent>())
                            });
                        }
                        return c;
                    }).ToArray();
                    messages[idx] = toolCalledAssistant with { Content = updatedContent };
                }
                break;

            case "session.next.tool.success":
                var toolSuccessAssistantId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var toolSuccessCallId = ExtractField<string>(@event.Data, "callID") ?? "";
                var toolSuccessAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == toolSuccessAssistantId);
                if (toolSuccessAssistant != null)
                {
                    var idx = messages.IndexOf(toolSuccessAssistant);
                    var updatedContent = toolSuccessAssistant.Content.Select(c =>
                    {
                        if (c is Schema.SessionMessageTool tool && tool.Id == toolSuccessCallId && tool.State is Schema.ToolStateRunning running)
                        {
                            return (Schema.SessionMessageAssistantContent)(tool with
                            {
                                Completed = @event.Timestamp,
                                State = new Schema.ToolStateCompleted(
                                    Status: "completed",
                                    Input: running.Input,
                                    Attachments: null,
                                    Content: running.Content,
                                    OutputPaths: null,
                                    Structured: running.Structured,
                                    Result: ExtractField<object>(@event.Data, "result")
                                )
                            });
                        }
                        return c;
                    }).ToArray();
                    messages[idx] = toolSuccessAssistant with { Content = updatedContent };
                }
                break;

            case "session.next.tool.failed":
                var toolFailedAssistantId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var toolFailedCallId = ExtractField<string>(@event.Data, "callID") ?? "";
                var toolFailedAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == toolFailedAssistantId);
                if (toolFailedAssistant != null)
                {
                    var idx = messages.IndexOf(toolFailedAssistant);
                    var errorData = ExtractField<Dictionary<string, object>>(@event.Data, "error");
                    var updatedContent = toolFailedAssistant.Content.Select(c =>
                    {
                        if (c is Schema.SessionMessageTool tool && tool.Id == toolFailedCallId &&
                            (tool.State is Schema.ToolStatePending || tool.State is Schema.ToolStateRunning))
                        {
                            var toolState = tool.State;
                            var runningState = toolState as Schema.ToolStateRunning;
                            return (Schema.SessionMessageAssistantContent)(tool with
                            {
                                Completed = @event.Timestamp,
                                State = new Schema.ToolStateError(
                                    Status: "error",
                                    Input: runningState?.Input ?? new Dictionary<string, object>(),
                                    Content: runningState?.Content ?? Array.Empty<Schema.ToolContent>(),
                                    Structured: runningState?.Structured ?? new Dictionary<string, object>(),
                                    Error: errorData != null
                                        ? new Schema.SessionUnknownError(
                                            Type: GetDictString(errorData, "type") ?? "unknown",
                                            Message: GetDictString(errorData, "message") ?? ""
                                        )
                                        : new Schema.SessionUnknownError("unknown", "Unknown error"),
                                    Result: null
                                )
                            });
                        }
                        return c;
                    }).ToArray();
                    messages[idx] = toolFailedAssistant with { Content = updatedContent };
                }
                break;

            case "session.next.reasoning.started":
                var reasoningAssistantId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var reasoningAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == reasoningAssistantId);
                if (reasoningAssistant != null)
                {
                    var idx = messages.IndexOf(reasoningAssistant);
                    var newContent = reasoningAssistant.Content.Append(new Schema.SessionMessageReasoning(
                        Type: "reasoning",
                        Id: ExtractField<string>(@event.Data, "reasoningID") ?? GenerateId(),
                        Text: "",
                        ProviderMetadata: null,
                        Created: @event.Timestamp,
                        Completed: null
                    )).ToArray();
                    messages[idx] = reasoningAssistant with { Content = newContent };
                }
                break;

            case "session.next.reasoning.delta":
                var reasoningDeltaAssistantId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var reasoningId = ExtractField<string>(@event.Data, "reasoningID") ?? "";
                var reasoningDeltaAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == reasoningDeltaAssistantId);
                if (reasoningDeltaAssistant != null)
                {
                    var idx = messages.IndexOf(reasoningDeltaAssistant);
                    var updatedContent = reasoningDeltaAssistant.Content.Select(c =>
                    {
                        if (c is Schema.SessionMessageReasoning r && r.Id == reasoningId)
                            return (Schema.SessionMessageAssistantContent)(r with { Text = r.Text + (ExtractField<string>(@event.Data, "delta") ?? "") });
                        return c;
                    }).ToArray();
                    messages[idx] = reasoningDeltaAssistant with { Content = updatedContent };
                }
                break;

            case "session.next.reasoning.ended":
                var reasoningEndAssistantId = ExtractField<string>(@event.Data, "assistantMessageID") ?? "";
                var reasoningEndId = ExtractField<string>(@event.Data, "reasoningID") ?? "";
                var reasoningEndAssistant = messages.OfType<Schema.SessionMessageAssistant>()
                    .LastOrDefault(a => a.Id == reasoningEndAssistantId);
                if (reasoningEndAssistant != null)
                {
                    var idx = messages.IndexOf(reasoningEndAssistant);
                    var updatedContent = reasoningEndAssistant.Content.Select(c =>
                    {
                        if (c is Schema.SessionMessageReasoning r && r.Id == reasoningEndId)
                            return (Schema.SessionMessageAssistantContent)(r with
                            {
                                Text = ExtractField<string>(@event.Data, "text") ?? r.Text,
                                Completed = @event.Timestamp
                            });
                        return c;
                    }).ToArray();
                    messages[idx] = reasoningEndAssistant with { Content = updatedContent };
                }
                break;

            case "session.next.compaction.ended":
                messages.Add(new Schema.SessionMessageCompaction(
                    Id: ExtractField<string>(@event.Data, "messageID") ?? GenerateId(),
                    Metadata: null,
                    Created: @event.Timestamp,
                    Type: "compaction",
                    Reason: ExtractField<string>(@event.Data, "reason") ?? "auto",
                    Summary: ExtractField<string>(@event.Data, "text") ?? "",
                    Recent: ExtractField<string>(@event.Data, "recent") ?? ""
                ));
                break;
        }
    }

    static string GenerateId() => "msg_" + Guid.NewGuid().ToString("N")[..12];

    static T? ExtractField<T>(object data, string fieldName)
    {
        if (data is JsonElement element)
        {
            if (element.TryGetProperty(fieldName, out var prop))
                return JsonSerializer.Deserialize<T>(prop.GetRawText());
            return default;
        }
        if (data is Dictionary<string, object> dict)
        {
            if (dict.TryGetValue(fieldName, out var value))
            {
                if (value is T typed) return typed;
                if (value is JsonElement je)
                    return JsonSerializer.Deserialize<T>(je.GetRawText());
                return default;
            }
            return default;
        }
        var propInfo = data.GetType().GetProperty(fieldName);
        if (propInfo != null)
            return (T?)propInfo.GetValue(data);
        return default;
    }

    static string? GetDictString(Dictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var value))
        {
            if (value is string s) return s;
            if (value is JsonElement je) return je.GetString();
            return value?.ToString();
        }
        return null;
    }
}
