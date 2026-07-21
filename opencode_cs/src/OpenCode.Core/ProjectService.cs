using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace OpenCode.Core;

public record ProjectInfo(
    string Id,
    string Directory,
    ProjectVcs? Vcs,
    long Created,
    long Updated
);

public record ProjectVcs(
    string Type,
    string? Remote
);

public interface IProjectService
{
    Task<ProjectInfo> ResolveAsync(string directory);
    Task<List<ProjectInfo>> AllAsync();
}

public class ProjectService : IProjectService
{
    readonly IGitService git;
    readonly ConcurrentDictionary<string, ProjectInfo> projects = new(StringComparer.Ordinal);

    public ProjectService(IGitService git)
    {
        this.git = git;
    }

    public async Task<ProjectInfo> ResolveAsync(string directory)
    {
        var resolvedDirectory = Path.GetFullPath(directory);
        var repository = await git.DiscoverAsync(resolvedDirectory);
        var worktree = repository?.Worktree ?? resolvedDirectory;
        if (projects.TryGetValue(worktree, out var existing))
            return existing;

        var remote = repository is null ? null : await git.GetRemoteAsync(repository);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(worktree)))[..16].ToLowerInvariant();
        return projects.GetOrAdd(worktree, new ProjectInfo(
            id,
            worktree,
            repository is null ? null : new ProjectVcs("git", remote),
            now,
            now));
    }

    public Task<List<ProjectInfo>> AllAsync() => Task.FromResult(projects.Values
        .OrderByDescending(project => project.Updated)
        .ThenBy(project => project.Id, StringComparer.Ordinal)
        .ToList());
}
