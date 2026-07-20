using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace OpenCode.Core;

public record WriteToolInput(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content
);

public record WriteToolOutput(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("existed")] bool Existed
);

public class WriteTool : Tool
{
    readonly IFsUtil fs;
    readonly string locationDirectory;

    public WriteTool(IFsUtil fs, string locationDirectory)
        : base("write", "Write content to one file. Relative paths resolve within the active Location. Absolute paths inside the Location are accepted. Explicit external absolute paths require external_directory approval before edit approval.", "edit")
    {
        this.fs = fs;
        this.locationDirectory = locationDirectory;
    }

    public override async Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
    {
        if (input is not WriteToolInput writeInput)
            throw new ToolFailure("Invalid input for write tool");

        var resolvedPath = Path.IsPathRooted(writeInput.Path)
            ? writeInput.Path
            : Path.Combine(locationDirectory, writeInput.Path);

        var canonicalPath = await fs.ResolveAsync(resolvedPath);
        var existed = await fs.ExistsSafeAsync(canonicalPath);

        await fs.WriteWithDirsAsync(canonicalPath, writeInput.Content);

        var message = existed ? $"Wrote file successfully: {writeInput.Path}" : $"Created file successfully: {writeInput.Path}";
        var output = new WriteToolOutput("write", canonicalPath, writeInput.Path, existed);

        return new ToolOutput(
            new List<ToolOutputContent> { new ToolTextContent { Text = message } },
            output
        );
    }

    public override ToolDefinition ToDefinition(string name)
    {
        return new ToolDefinition(
            name,
            Description,
            new { path = "(string) File path to write", content = "(string) Content to write" },
            null
        );
    }
}
