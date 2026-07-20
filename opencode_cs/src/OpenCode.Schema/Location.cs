using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record LocationRef(
    string Directory,
    string? WorkspaceId
);

public record LocationInfo(
    string Directory,
    string? WorkspaceId,
    LocationProject Project
);

public record LocationProject(
    string Id,
    string Directory
);
