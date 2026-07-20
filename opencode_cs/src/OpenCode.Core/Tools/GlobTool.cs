using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace OpenCode.Core;

public record GlobToolInput(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("limit")] int? Limit
);

public record GlobToolEntry(
    [property: JsonPropertyName("path")] string EntryPath,
    [property: JsonPropertyName("type")] string EntryType
);

public class GlobTool : Tool
{
    readonly IFsUtil fs;
    readonly string locationDirectory;

    public GlobTool(IFsUtil fs, string locationDirectory)
        : base("glob", "Find files by glob pattern within the active Location. Returns concise relative file resources. Use a relative path to narrow the search and limit to bound the result count.")
    {
        this.fs = fs;
        this.locationDirectory = locationDirectory;
    }

    public override async Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
    {
        if (input is not GlobToolInput globInput)
            throw new ToolFailure("Invalid input for glob tool");

        var searchDir = string.IsNullOrEmpty(globInput.Path)
            ? locationDirectory
            : Path.IsPathRooted(globInput.Path)
                ? globInput.Path
                : Path.Combine(locationDirectory, globInput.Path);

        searchDir = await fs.ResolveAsync(searchDir);

        if (!await fs.IsDirAsync(searchDir))
            throw new ToolFailure($"Not a directory: {searchDir}");

        var limit = globInput.Limit ?? int.MaxValue;
        var entries = await SearchGlob(searchDir, globInput.Pattern, limit);

        var resultEntries = entries.Select(e => new GlobToolEntry(
            Path.GetRelativePath(locationDirectory, e),
            "file"
        )).ToList();

        var outputText = resultEntries.Count == 0
            ? "No files found"
            : string.Join("\n", resultEntries.Select(e => e.EntryPath));

        return new ToolOutput(
            new List<ToolOutputContent> { new ToolTextContent { Text = outputText } },
            resultEntries
        );
    }

    public override ToolDefinition ToDefinition(string name)
    {
        return new ToolDefinition(
            name,
            Description,
            new { pattern = "(string) Glob pattern", path = "(string?) Relative directory", limit = "(int?) Max results" },
            null
        );
    }

    static async Task<List<string>> SearchGlob(string searchDir, string pattern, int limit)
    {
        var results = new List<string>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        var escapePattern = pattern
            .Replace('%', '_')
            .Replace('*', '%');

        var fileName = Path.GetFileName(pattern);
        var directory = Path.GetDirectoryName(pattern) ?? searchDir;

        var baseDir = Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(searchDir, directory);

        baseDir = Path.GetFullPath(baseDir);

        if (!Directory.Exists(baseDir))
            return results;

        var searchPattern = fileName.Replace('%', '_').Replace('*', '%');

        foreach (var file in Directory.EnumerateFiles(baseDir, "*", options))
        {
            var relativeName = Path.GetFileName(file);
            if (GlobMatch(searchPattern, relativeName))
            {
                results.Add(file);
                if (results.Count >= limit)
                    break;
            }
        }

        return results;
    }

    static bool GlobMatch(string pattern, string text)
    {
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("%", ".*") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(text, regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
