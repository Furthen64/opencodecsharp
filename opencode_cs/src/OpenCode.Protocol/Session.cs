namespace OpenCode.Protocol;

public record SessionsQuery(
    string? Directory,
    string? Workspace,
    string? Project,
    string? Subpath,
    string? Cursor,
    int? Limit,
    SessionOrder? Order,
    string? Search
);

public enum SessionOrder
{
    Asc,
    Desc
}

public record SessionsResponse(
    List<SessionInfo> Data,
    PaginationCursor Cursor
);

public record SessionCreateRequest(
    string? Id,
    string? Agent,
    ModelRef? Model,
    LocationRef? Location
);

public record SessionActiveResponse(
    Dictionary<string, SessionActiveStatus> Data
);

public record SessionActiveStatus(
    string Type
);

public record SessionSwitchAgentRequest(
    string Agent
);

public record SessionSwitchModelRequest(
    ModelRef Model
);

public record SessionPromptRequest(
    string? Id,
    PromptInput Prompt,
    SessionDelivery? Delivery,
    bool? Resume
);

public record SessionPromptResponse(
    SessionInputAdmitted Data
);

public record SessionRevertStageRequest(
    string MessageID,
    bool? Files
);

public record SessionRevertStageResponse(
    RevertState Data
);

public record SessionHistoryQuery(
    int? Limit,
    int? After
);

public record SessionMessagesQuery(
    int? Limit,
    SessionOrder? Order,
    string? Cursor
);

public record SessionMessagesResponse(
    List<SessionMessageBase> Data,
    PaginationCursor Cursor
);
