namespace OpenCode.Protocol;

public record LocationQuery(
    ProtocolLocationInfo? Location
);

public record ProtocolLocationInfo(
    string? Directory,
    string? Workspace
);

public record PaginationCursor(
    string? Previous,
    string? Next
);

public record PaginatedResponse<T>(
    T Data,
    PaginationCursor Cursor
);

public record SimpleResponse<T>(
    T Data
);

public record SessionHistoryResponse(
    List<SessionEventDurable> Data,
    bool HasMore
);
