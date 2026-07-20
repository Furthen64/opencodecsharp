using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record SessionUnknownError(
    string Type,
    string Message
);

public record SessionMessageBase(
    string Id,
    Dictionary<string, object>? Metadata,
    long Created
);

public record SessionMessageAgentSwitched(
    string Id,
    Dictionary<string, object>? Metadata,
    long Created,
    string Type,
    string Agent
) : SessionMessageBase(Id, Metadata, Created);

public record SessionMessageModelSwitched(
    string Id,
    Dictionary<string, object>? Metadata,
    long Created,
    string Type,
    ModelRef Model
) : SessionMessageBase(Id, Metadata, Created);

public record SessionMessageUser(
    string Id,
    Dictionary<string, object>? Metadata,
    long Created,
    string Type,
    string Text,
    FileAttachment[]? Files,
    AgentAttachment[]? Agents
) : SessionMessageBase(Id, Metadata, Created);

public record SessionMessageSynthetic(
    string Id,
    Dictionary<string, object>? Metadata,
    long Created,
    string Type,
    string SessionId,
    string Text
) : SessionMessageBase(Id, Metadata, Created);

public record SessionMessageSystem(
    string Id,
    Dictionary<string, object>? Metadata,
    long Created,
    string Type,
    string Text
) : SessionMessageBase(Id, Metadata, Created);

public record SessionMessageShell(
    string Id,
    Dictionary<string, object>? Metadata,
    long Created,
    string Type,
    string CallId,
    string Command,
    string Output,
    long? Completed
) : SessionMessageBase(Id, Metadata, Created);

[JsonDerivedType(typeof(ToolStatePending), typeDiscriminator: "pending")]
[JsonDerivedType(typeof(ToolStateRunning), typeDiscriminator: "running")]
[JsonDerivedType(typeof(ToolStateCompleted), typeDiscriminator: "completed")]
[JsonDerivedType(typeof(ToolStateError), typeDiscriminator: "error")]
public abstract record ToolState;

public record ToolStatePending(
    string Status,
    string Input
) : ToolState;

public record ToolStateRunning(
    string Status,
    Dictionary<string, object> Input,
    Dictionary<string, object> Structured,
    ToolContent[] Content
) : ToolState;

public record ToolStateCompleted(
    string Status,
    Dictionary<string, object> Input,
    FileAttachment[]? Attachments,
    ToolContent[] Content,
    string[]? OutputPaths,
    Dictionary<string, object> Structured,
    object? Result
) : ToolState;

public record ToolStateError(
    string Status,
    Dictionary<string, object> Input,
    ToolContent[] Content,
    Dictionary<string, object> Structured,
    SessionUnknownError Error,
    object? Result
) : ToolState;

public record SessionMessageProviderInfo(
    bool Executed,
    LLMProviderMetadata? Metadata,
    LLMProviderMetadata? ResultMetadata
);

public abstract record SessionMessageAssistantContent;

public record SessionMessageTool(
    string Type,
    string Id,
    string Name,
    SessionMessageProviderInfo? Provider,
    ToolState State,
    long Created,
    long? Ran,
    long? Completed,
    long? Pruned
) : SessionMessageAssistantContent;

public record SessionMessageText(
    string Type,
    string Id,
    string Text
) : SessionMessageAssistantContent;

public record SessionMessageReasoning(
    string Type,
    string Id,
    string Text,
    LLMProviderMetadata? ProviderMetadata,
    long? Created,
    long? Completed
) : SessionMessageAssistantContent;

public record SessionMessageSnapshot(
    string? Start,
    string? End,
    string[]? Files
);

public record SessionMessageAssistant(
    string Id,
    Dictionary<string, object>? Metadata,
    long Created,
    string Type,
    string Agent,
    ModelRef Model,
    SessionMessageAssistantContent[] Content,
    SessionMessageSnapshot? Snapshot,
    string? Finish,
    double? Cost,
    SessionTokens? Tokens,
    SessionUnknownError? Error,
    long? Completed
) : SessionMessageBase(Id, Metadata, Created);

public record SessionMessageCompaction(
    string Id,
    Dictionary<string, object>? Metadata,
    long Created,
    string Type,
    string Reason,
    string Summary,
    string Recent
) : SessionMessageBase(Id, Metadata, Created);
