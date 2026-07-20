using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public static class ProjectCopy
{
    public record CreateInput(
        string ProjectID,
        string Strategy,
        string SourceDirectory,
        string Directory,
        string? Name
    );

    public record RemoveInput(
        string ProjectID,
        string Directory,
        bool Force
    );

    public record Copy(
        string Directory
    );
}
