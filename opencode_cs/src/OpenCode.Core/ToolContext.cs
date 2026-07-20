namespace OpenCode.Core;

public record ToolContext(
    string SessionId,
    string AgentId,
    string AssistantMessageId,
    string ToolCallId
);
