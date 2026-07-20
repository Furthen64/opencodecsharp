using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record SessionTokens(
    double Input,
    double Output,
    double Reasoning,
    SessionCacheTokens Cache
);

public record SessionCacheTokens(
    double Read,
    double Write
);

public record SessionTime(
    long Created,
    long Updated,
    long? Archived
);

public record SessionInfo(
    string Id,
    string? ParentId,
    string ProjectId,
    string? Agent,
    ModelRef? Model,
    double Cost,
    SessionTokens Tokens,
    SessionTime Time,
    string Title,
    LocationRef Location,
    string? Subpath,
    RevertState? Revert
);

public record SessionListAnchor(
    string Id,
    long Time,
    string Direction
);
