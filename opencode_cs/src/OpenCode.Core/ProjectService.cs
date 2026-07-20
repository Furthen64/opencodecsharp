using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenCode.Core;

public record ProjectInfo(
    string Id,
    string Directory,
    ProjectVcs? Vcs
);

public record ProjectVcs(
    string Type,
    string? Remote
);

public interface IProjectService
{
    Task<ProjectInfo> ResolveAsync(string directory);
}

public class ProjectService : IProjectService
{
    readonly Dictionary<string, ProjectInfo> projects = new();

    public Task<ProjectInfo> ResolveAsync(string directory)
    {
        var id = System.Guid.NewGuid().ToString("n")[..10];
        var project = new ProjectInfo(id, directory, null);
        projects[directory] = project;
        return Task.FromResult(project);
    }
}
