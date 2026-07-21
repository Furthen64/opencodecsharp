using System.Text.Json;

namespace OpenCode.Core;

public readonly record struct SnapshotId(string Value)
{
    public override string ToString() => Value;
    public static SnapshotId Make(string value) => new(value);
}

public enum SnapshotOperation
{
    Capture,
    Files,
    Diff,
    Preview,
    Restore
}

public class SnapshotError : Exception
{
    public SnapshotOperation Operation { get; }

    public SnapshotError(SnapshotOperation operation, string message, Exception? cause = null)
        : base($"Snapshot {operation}: {message}", cause)
    {
        Operation = operation;
    }
}

public interface ISnapshotService
{
    Task<SnapshotId?> CaptureAsync();
    Task<string[]> FilesAsync(SnapshotId from, SnapshotId to);
    Task<FileDiff[]> DiffAsync(SnapshotId from, SnapshotId to, int context = 3, string[]? paths = null);
    Task<FileDiff[]> PreviewAsync(SnapshotId current, Dictionary<string, SnapshotId> files, int context = 3);
    Task RestoreAsync(Dictionary<string, SnapshotId> files);
    Task CheckoutAsync(SnapshotId snapshot);
}

public class SnapshotService : ISnapshotService
{
    readonly IGitService git;
    readonly string? projectDirectory;
    readonly string? locationDirectory;
    readonly string? projectId;
    readonly string? dataDirectory;

    public SnapshotService(
        IGitService git,
        string? projectDirectory = null,
        string? locationDirectory = null,
        string? projectId = null,
        string? dataDirectory = null)
    {
        this.git = git;
        this.projectDirectory = projectDirectory;
        this.locationDirectory = locationDirectory;
        this.projectId = projectId;
        this.dataDirectory = dataDirectory;
    }

    public async Task<SnapshotId?> CaptureAsync()
    {
        if (projectDirectory == null || dataDirectory == null || projectId == null)
            return null;

        try
        {
            var source = await git.DiscoverAsync(projectDirectory);
            if (source == null) return null;

            var snapshotGitDir = Path.Combine(dataDirectory, "snapshot", projectId, HashFast(source.Worktree));
            var scope = GetScope(source.Worktree);

            var repository = await GetOrCreateRepositoryAsync(source, snapshotGitDir);
            if (repository == null) return null;

            var treeId = await git.WriteTreeAsync(repository);
            return SnapshotId.Make(treeId);
        }
        catch
        {
            return null;
        }
    }

    public async Task<string[]> FilesAsync(SnapshotId from, SnapshotId to)
    {
        var repository = await RequireRepositoryAsync();
        return await git.TreeFilesAsync(repository, from.Value, to.Value);
    }

    public async Task<FileDiff[]> DiffAsync(SnapshotId from, SnapshotId to, int context = 3, string[]? paths = null)
    {
        var repository = await RequireRepositoryAsync();
        return await git.TreeDiffAsync(repository, from.Value, to.Value, context, paths);
    }

    public async Task<FileDiff[]> PreviewAsync(SnapshotId current, Dictionary<string, SnapshotId> files, int context = 3)
    {
        var repository = await RequireRepositoryAsync();
        var fileArray = files.Select(f => f.Key).ToArray();
        return await git.TreeDiffAsync(repository, current.Value, SnapshotId.Make("").Value, context, fileArray);
    }

    public async Task RestoreAsync(Dictionary<string, SnapshotId> files)
    {
        var repository = await RequireRepositoryAsync();
        foreach (var (file, snapshot) in files)
        {
            var absolutePath = Path.Combine(projectDirectory!, file);
            var patch = await git.CapturePatchAsync(repository, file);
            if (!string.IsNullOrEmpty(patch))
            {
                await git.ApplyPatchAsync(projectDirectory!, patch);
            }
        }
    }

    public async Task CheckoutAsync(SnapshotId snapshot)
    {
        var repository = await RequireRepositoryAsync();
        await git.RunAsync(repository.Worktree, new[] { "read-tree", snapshot.Value });
        await git.RunAsync(repository.Worktree, new[] { "checkout-index", "--all", "--force" });
    }

    async Task<GitRepository?> GetOrCreateRepositoryAsync(GitRepository source, string snapshotGitDir)
    {
        if (File.Exists(Path.Combine(snapshotGitDir, "HEAD")))
        {
            return new GitRepository(source.Worktree, snapshotGitDir, snapshotGitDir);
        }

        try
        {
            return await git.CreateAsync(source.Worktree, snapshotGitDir, source);
        }
        catch
        {
            return null;
        }
    }

    async Task<GitRepository> RequireRepositoryAsync()
    {
        if (projectDirectory == null || dataDirectory == null || projectId == null)
            throw new SnapshotError(SnapshotOperation.Capture, "Project not configured");

        var source = await git.DiscoverAsync(projectDirectory);
        if (source == null)
            throw new SnapshotError(SnapshotOperation.Capture, "Project is not a Git repository");

        var snapshotGitDir = Path.Combine(dataDirectory, "snapshot", projectId, HashFast(source.Worktree));
        var repo = await GetOrCreateRepositoryAsync(source, snapshotGitDir);
        if (repo == null)
            throw new SnapshotError(SnapshotOperation.Capture, "Could not create snapshot repository");

        return repo;
    }

    string GetScope(string worktree)
    {
        if (locationDirectory == null) return ".";
        var relative = Path.GetRelativePath(worktree, locationDirectory);
        if (relative.StartsWith("..") || Path.IsPathRooted(relative))
            return ".";
        var result = relative.Replace('\\', '/');
        return string.IsNullOrEmpty(result) ? "." : result;
    }

    static string HashFast(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }
}
