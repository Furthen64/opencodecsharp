using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public enum ProjectVcs
{
    Git
}

public record ProjectIcon(
    string? Url,
    string? Override,
    string? Color
);

public record ProjectCommands(
    string? Start
);

public record ProjectTime(
    long Created,
    long Updated,
    long? Initialized
);

public record ProjectInfo(
    string Id,
    string Worktree,
    ProjectVcs? Vcs,
    string? Name,
    ProjectIcon? Icon,
    ProjectCommands? Commands,
    ProjectTime Time,
    string[] Sandboxes
);
