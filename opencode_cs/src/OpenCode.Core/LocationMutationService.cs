namespace OpenCode.Core;

public record LocationMutationTarget(
    string Canonical,
    string Resource,
    LocationMutationExternalDirectory? ExternalDirectory
);

public record LocationMutationExternalDirectory(
    string Action,
    string Directory,
    string Resource,
    string Save
);

public record LocationMutationResolveInput(
    string Path,
    string? Kind
);

public class LocationMutationPathError : Exception
{
    public string ErrorPath { get; }
    public string Reason { get; }
    public LocationMutationPathError(string path, string reason) : base($"Path error for {path}: {reason}")
    {
        ErrorPath = path;
        Reason = reason;
    }
}

public interface ILocationMutationService
{
    Task<LocationMutationTarget> ResolveAsync(LocationMutationResolveInput input);
}

public class LocationMutationService : ILocationMutationService
{
    readonly IFsUtil fs;
    readonly string locationDirectory;

    public LocationMutationService(IFsUtil fs, string locationDirectory)
    {
        this.fs = fs;
        this.locationDirectory = Path.GetFullPath(locationDirectory);
    }

    public async Task<LocationMutationTarget> ResolveAsync(LocationMutationResolveInput input)
    {
        bool relative = !Path.IsPathRooted(input.Path);
        string absolute = Path.GetFullPath(Path.Combine(locationDirectory, input.Path));
        bool lexicallyInternal = absolute.StartsWith(locationDirectory, StringComparison.Ordinal);

        if (relative && !lexicallyInternal)
            throw new LocationMutationPathError(input.Path, "relative_escape");

        var resolved = await ResolvePathAsync(absolute);
        if (resolved == null)
            throw new LocationMutationPathError(input.Path, "non_directory_ancestor");

        bool external = !lexicallyInternal;
        var relPath = StripSlashes(Path.GetRelativePath(locationDirectory, resolved.Canonical)).Replace('\\', '/');
        string resource = external
            ? resolved.Canonical.Replace('\\', '/')
            : (string.IsNullOrEmpty(relPath) ? "." : relPath);

        string externalDir = input.Kind == "directory" && resolved.Type == "Directory"
            ? resolved.Canonical
            : resolved.Directory;

        string externalResource = externalDir.Replace('\\', '/') + "/*";

        return new LocationMutationTarget(
            Canonical: resolved.Canonical,
            Resource: resource,
            ExternalDirectory: external
                ? new LocationMutationExternalDirectory(
                    Action: "external_directory",
                    Directory: externalDir,
                    Resource: externalResource,
                    Save: externalResource
                )
                : null
        );
    }

    async Task<ResolvedPath?> ResolvePathAsync(string absolute)
    {
        if (await fs.ExistsAsync(absolute))
        {
            var stat = await fs.StatAsync(absolute);
            return new ResolvedPath(
                Canonical: absolute,
                Type: stat.Type,
                Directory: stat.Type == "Directory" ? absolute : Path.GetDirectoryName(absolute) ?? absolute
            );
        }

        string anchor = Path.GetDirectoryName(absolute) ?? absolute;
        while (true)
        {
            if (await fs.ExistsAsync(anchor))
            {
                var stat = await fs.StatAsync(anchor);
                if (stat.Type != "Directory")
                    return null;
                return new ResolvedPath(
                    Canonical: Path.GetFullPath(Path.Combine(anchor, Path.GetRelativePath(anchor, absolute))),
                    Type: stat.Type,
                    Directory: anchor
                );
            }
            var parent = Path.GetDirectoryName(anchor);
            if (parent == null || parent == anchor) return null;
            anchor = parent;
        }
    }

    static string StripSlashes(string path) =>
        path.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    record ResolvedPath(string Canonical, string Type, string Directory);
}
