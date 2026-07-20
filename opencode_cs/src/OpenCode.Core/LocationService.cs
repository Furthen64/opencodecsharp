namespace OpenCode.Core;

public record LocationRef(
    string Directory,
    string? WorkspaceId
);

public record LocationInfo(
    string Directory,
    string? WorkspaceId,
    LocationProject Project,
    ProjectVcs? Vcs
);

public record LocationProject(
    string Id,
    string Directory
);

public interface ILocationService
{
    LocationInfo Info { get; }
}

public class LocationService : ILocationService
{
    public LocationInfo Info { get; init; }

    public LocationService(LocationInfo info)
    {
        Info = info;
    }
}
