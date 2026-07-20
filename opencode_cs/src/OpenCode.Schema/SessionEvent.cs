using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record SessionEventSource(
    long Start,
    long End,
    string Text
);

public record SessionEventRetryError(
    string Message,
    double? StatusCode,
    bool IsRetryable,
    Dictionary<string, string>? ResponseHeaders,
    string? ResponseBody,
    Dictionary<string, string>? Metadata
);

public record SessionEventBase(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
);

public record SessionEventDurable(
    string AggregateId,
    int Seq,
    int Version
);

public record SessionEventAgentSwitched(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string MessageId,
    string Agent,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventModelSwitched(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string MessageId,
    ModelRef Model,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventMoved(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    LocationRef MovedLocation,
    string? Subdirectory,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, null);

public record SessionEventPrompted(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string MessageId,
    Prompt Prompt,
    SessionDelivery Delivery,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventPromptAdmitted(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string MessageId,
    Prompt Prompt,
    SessionDelivery Delivery,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventContextUpdated(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string MessageId,
    string Text,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventSynthetic(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string MessageId,
    string Text,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventShellStarted(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string MessageId,
    string CallId,
    string Command,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventShellEnded(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string CallId,
    string Output,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventStepStarted(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string Agent,
    ModelRef Model,
    string? Snapshot,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventStepEnded(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string Finish,
    double Cost,
    SessionTokens Tokens,
    string? Snapshot,
    string[]? Files,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventStepFailed(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    SessionUnknownError Error,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventTextStarted(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string TextId,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventTextDelta(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string TextId,
    string Delta,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventTextEnded(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string TextId,
    string Text,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventReasoningStarted(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string ReasoningId,
    LLMProviderMetadata? ProviderMetadata,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventReasoningDelta(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string ReasoningId,
    string Delta,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventReasoningEnded(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string ReasoningId,
    string Text,
    LLMProviderMetadata? ProviderMetadata,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventToolProviderInfo(
    bool Executed,
    LLMProviderMetadata? Metadata
);

public record SessionEventToolInputStarted(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string CallId,
    string Name,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventToolInputDelta(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string CallId,
    string Delta,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventToolInputEnded(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string CallId,
    string Text,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventToolCalled(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string CallId,
    string Tool,
    Dictionary<string, object> Input,
    SessionEventToolProviderInfo Provider,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventToolProgress(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string CallId,
    Dictionary<string, object> Structured,
    ToolContent[] Content,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventToolSuccess(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string CallId,
    Dictionary<string, object> Structured,
    ToolContent[] Content,
    string[]? OutputPaths,
    object? Result,
    SessionEventToolProviderInfo Provider,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventToolFailed(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string AssistantMessageId,
    string CallId,
    SessionUnknownError Error,
    object? Result,
    SessionEventToolProviderInfo Provider,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventRetried(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    double Attempt,
    SessionEventRetryError Error,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventCompactionStarted(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string MessageId,
    string Reason,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventCompactionDelta(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string MessageId,
    string Text,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventCompactionEnded(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string MessageId,
    string Reason,
    string Text,
    string Recent,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventRevertStaged(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    RevertState Revert,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventRevertCleared(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);

public record SessionEventRevertCommitted(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    string MessageId,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location
) : SessionEventBase(Id, Type, Timestamp, SessionId, Metadata, Durable, Location);
