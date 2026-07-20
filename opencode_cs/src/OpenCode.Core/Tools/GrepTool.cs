using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OpenCode.Core;

public record GrepToolInput(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("include")] string? Include,
    [property: JsonPropertyName("limit")] int? Limit
);

public record GrepToolMatch(
    [property: JsonPropertyName("entry")] GrepToolEntry Entry,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("text")] string Text
);

public record GrepToolEntry(
    [property: JsonPropertyName("path")] string EntryPath,
    [property: JsonPropertyName("type")] string EntryType
);

public class GrepTool : Tool
{
    readonly IFsUtil fs;
    readonly string locationDirectory;

    public GrepTool(IFsUtil fs, string locationDirectory)
        : base("grep", "Search file contents by regular expression within the active Location. Use a path to narrow the search, include to filter files by glob, and limit to bound the match count.")
    {
        this.fs = fs;
        this.locationDirectory = locationDirectory;
    }

    public override async Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
    {
        if (input is not GrepToolInput grepInput)
            throw new ToolFailure("Invalid input for grep tool");

        var searchDir = string.IsNullOrEmpty(grepInput.Path)
            ? locationDirectory
            : Path.IsPathRooted(grepInput.Path)
                ? grepInput.Path
                : Path.Combine(locationDirectory, grepInput.Path);

        searchDir = await fs.ResolveAsync(searchDir);

        if (!await fs.IsDirAsync(searchDir))
        {
            searchDir = Path.GetDirectoryName(searchDir) ?? locationDirectory;
        }

        var limit = grepInput.Limit ?? int.MaxValue;

        try
        {
            var regex = new Regex(grepInput.Pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);

            var matches = new List<GrepToolMatch>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            };

            foreach (var file in Directory.EnumerateFiles(searchDir, "*", options))
            {
                if (matches.Count >= limit)
                    break;

                if (grepInput.Include != null && !GlobMatch(grepInput.Include, Path.GetFileName(file)))
                    continue;

                try
                {
                    var content = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                    var lines = content.Split('\n');

                    for (int i = 0; i < lines.Length && matches.Count < limit; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            var relativePath = Path.GetRelativePath(locationDirectory, file);
                            var truncatedLine = lines[i].Length > 2000
                                ? lines[i][..2000] + "..."
                                : lines[i];

                            matches.Add(new GrepToolMatch(
                                new GrepToolEntry(relativePath, "file"),
                                i + 1,
                                truncatedLine
                            ));
                        }
                    }
                }
                catch
                {
                }
            }

            var outputText = FormatOutput(matches);

            return new ToolOutput(
                new List<ToolOutputContent> { new ToolTextContent { Text = outputText } },
                matches
            );
        }
        catch (RegexParseException)
        {
            throw new ToolFailure($"Invalid regex pattern: {grepInput.Pattern}");
        }
    }

    public override ToolDefinition ToDefinition(string name)
    {
        return new ToolDefinition(
            name,
            Description,
            new { pattern = "(string) Regex pattern", path = "(string?) Search directory", include = "(string?) File glob filter", limit = "(int?) Max matches" },
            null
        );
    }

    static string FormatOutput(List<GrepToolMatch> matches)
    {
        if (matches.Count == 0)
            return "No files found";

        var lines = new List<string> { $"Found {matches.Count} matches" };
        string? currentFile = null;

        foreach (var match in matches)
        {
            if (currentFile != match.Entry.EntryPath)
            {
                if (currentFile != null)
                    lines.Add("");
                currentFile = match.Entry.EntryPath;
                lines.Add($"{match.Entry.EntryPath}:");
            }
            lines.Add($"  Line {match.Line}: {match.Text}");
        }

        return string.Join("\n", lines);
    }

    static bool GlobMatch(string pattern, string text)
    {
        var regexPattern = "^" + Regex.Escape(pattern).Replace("%", ".*") + "$";
        return Regex.IsMatch(text, regexPattern, RegexOptions.IgnoreCase);
    }
}
