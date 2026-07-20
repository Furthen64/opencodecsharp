using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace OpenCode.Core;

public record EditToolInput(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("oldString")] string OldString,
    [property: JsonPropertyName("newString")] string NewString,
    [property: JsonPropertyName("replaceAll")] bool ReplaceAll
);

public record EditToolOutput(
    [property: JsonPropertyName("files")] List<EditToolFileDiff> Files,
    [property: JsonPropertyName("replacements")] int Replacements
);

public record EditToolFileDiff(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("patch")] string Patch,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("additions")] int Additions,
    [property: JsonPropertyName("deletions")] int Deletions
);

public class EditTool : Tool
{
    readonly IFsUtil fs;
    readonly string locationDirectory;

    public EditTool(IFsUtil fs, string locationDirectory)
        : base("edit", "Replace exact text in one file. Relative paths resolve within the active Location. Absolute paths inside the Location are accepted. Explicit external absolute paths require external_directory approval before edit approval.", "edit")
    {
        this.fs = fs;
        this.locationDirectory = locationDirectory;
    }

    public override async Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
    {
        if (input is not EditToolInput editInput)
            throw new ToolFailure("Invalid input for edit tool");

        if (editInput.OldString == editInput.NewString)
            throw new ToolFailure("No changes to apply: oldString and newString are identical.");

        if (editInput.OldString == "")
            throw new ToolFailure("oldString must not be empty. Use write to create or overwrite a file.");

        var resolvedPath = Path.IsPathRooted(editInput.Path)
            ? editInput.Path
            : Path.Combine(locationDirectory, editInput.Path);

        var canonicalPath = await fs.ResolveAsync(resolvedPath);

        var fileBytes = await File.ReadAllBytesAsync(canonicalPath).ConfigureAwait(false);
        var bom = fileBytes.Length >= 3 && fileBytes[0] == 0xef && fileBytes[1] == 0xbb && fileBytes[2] == 0xbf;
        var contentBytes = bom ? fileBytes[3..] : fileBytes;
        var text = Encoding.UTF8.GetString(contentBytes);

        var lineEnding = DetectLineEnding(text);
        var oldString = ConvertToLineEnding(editInput.OldString, lineEnding);
        var newString = ConvertToLineEnding(editInput.NewString, lineEnding);

        var replacements = CountOccurrences(text, oldString);

        if (replacements == 0)
            throw new ToolFailure("Could not find oldString in the file. It must match exactly, including whitespace and indentation.");

        if (replacements > 1 && !editInput.ReplaceAll)
            throw new ToolFailure("Found multiple exact matches for oldString. Provide more surrounding context or set replaceAll to true.");

        var replaced = editInput.ReplaceAll
            ? text.Replace(oldString, newString)
            : ReplaceOnce(text, oldString, newString);

        var newBom = replaced.StartsWith("\uFEFF");
        var newText = newBom ? replaced[1..] : replaced;
        var finalContent = (bom || newBom) ? $"\uFEFF{newText}" : newText;

        await File.WriteAllBytesAsync(canonicalPath, Encoding.UTF8.GetBytes(finalContent)).ConfigureAwait(false);

        var oldLines = NormalizeLineEndings(text).Split('\n');
        var newLines = NormalizeLineEndings(replaced).Split('\n');
        int additions = 0;
        int deletions = 0;
        int oi = 0;
        int nj = 0;
        while (oi < oldLines.Length || nj < newLines.Length)
        {
            if (oi < oldLines.Length && nj < newLines.Length && oldLines[oi] == newLines[nj])
            {
                oi++;
                nj++;
            }
            else if (oi < oldLines.Length)
            {
                deletions++;
                oi++;
            }
            else
            {
                additions++;
                nj++;
            }
        }

        var diffText = GenerateSimpleDiff(text, replaced);
        var fileDiff = new EditToolFileDiff(editInput.Path, diffText, "modified", additions, deletions);
        var output = new EditToolOutput(new List<EditToolFileDiff> { fileDiff }, replacements);

        var message = GenerateModelOutput(output, editInput.OldString, editInput.NewString);

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
            new { path = "(string) File path", oldString = "(string) Exact text to replace", newString = "(string) Replacement text", replaceAll = "(bool) Replace all occurrences" },
            null
        );
    }

    static string GenerateModelOutput(EditToolOutput output, string oldString, string newString)
    {
        var lines = new List<string>
        {
            $"Edited file successfully: {output.Files[0].File}",
            $"Replacements: {output.Replacements}",
            "```diff"
        };

        foreach (var line in NormalizeLineEndings(oldString).Split('\n').Take(6))
        {
            var truncated = line.Length > 240 ? line[..240] + "..." : line;
            lines.Add($"-{truncated}");
        }
        if (NormalizeLineEndings(oldString).Split('\n').Length > 6)
            lines.Add("-...");

        foreach (var line in NormalizeLineEndings(newString).Split('\n').Take(6))
        {
            var truncated = line.Length > 240 ? line[..240] + "..." : line;
            lines.Add($"+{truncated}");
        }
        if (NormalizeLineEndings(newString).Split('\n').Length > 6)
            lines.Add("+...");

        lines.Add("```");

        return string.Join("\n", lines);
    }

    static string GenerateSimpleDiff(string oldText, string newText)
    {
        var oldLines = NormalizeLineEndings(oldText).Split('\n');
        var newLines = NormalizeLineEndings(newText).Split('\n');

        var diff = new List<string>();
        int i = 0;
        int j = 0;

        while (i < oldLines.Length || j < newLines.Length)
        {
            if (i < oldLines.Length && j < newLines.Length && oldLines[i] == newLines[j])
            {
                diff.Add($" {oldLines[i]}");
                i++;
                j++;
            }
            else if (i < oldLines.Length)
            {
                diff.Add($"-{oldLines[i]}");
                i++;
            }
            else
            {
                diff.Add($"+{newLines[j]}");
                j++;
            }
        }

        return string.Join("\n", diff);
    }

    static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");

    static string DetectLineEnding(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    static string ConvertToLineEnding(string text, string ending)
    {
        var normalized = NormalizeLineEndings(text);
        return ending == "\n" ? normalized : normalized.Replace("\n", "\r\n");
    }

    static string ReplaceOnce(string content, string oldValue, string newValue)
    {
        var index = content.IndexOf(oldValue);
        if (index == -1) return content;
        return content.Substring(0, index) + newValue + content.Substring(index + oldValue.Length);
    }

    static int CountOccurrences(string content, string search)
    {
        if (search == "") return content.Length + 1;

        int count = 0, offset = 0;
        while ((offset = content.IndexOf(search, offset)) != -1)
        {
            count++;
            offset += search.Length;
        }
        return count;
    }
}
