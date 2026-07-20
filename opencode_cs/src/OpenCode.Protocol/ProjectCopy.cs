namespace OpenCode.Protocol;

public record ProjectCopyCreateRequest(
    string Strategy,
    string Directory,
    string? Name
);

public record ProjectCopyRemoveRequest(
    string Directory,
    bool Force
);

public record ProjectCopyError(
    string Message,
    bool? ForceRequired
);
