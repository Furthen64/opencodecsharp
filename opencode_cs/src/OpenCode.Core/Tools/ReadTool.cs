using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace OpenCode.Core;

public record ReadToolInput(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("offset")] int? Offset,
    [property: JsonPropertyName("limit")] int? Limit
);

public record ReadToolTextPageOutput(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("mime")] string Mime,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("next")] int? Next
)
{
    public ReadToolTextPageOutput(string content, string mime, int offset, bool truncated, int? next = null)
        : this("text-page", content, mime, offset, truncated, next) { }
}

public record ReadToolListPageOutput(
    [property: JsonPropertyName("entries")] List<ReadToolDirEntry> Entries,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("next")] int? Next
);

public record ReadToolDirEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] string Type
);

public record ReadToolFileOutput(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("encoding")] string Encoding,
    [property: JsonPropertyName("mime")] string Mime
);

public class ReadTool : Tool
{
    const int MaxReadLines = 2000;
    const int MaxReadBytes = 50 * 1024;
    const int MaxMediaIngestBytes = 20 * 1024 * 1024;
    const int MaxLineLength = 2000;

    static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".tar", ".gz", ".exe", ".dll", ".so", ".class", ".jar", ".war", ".7z",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
        ".bin", ".dat", ".obj", ".o", ".a", ".lib", ".wasm", ".pyc", ".pyo"
    };

    static readonly HashSet<string> SupportedImageMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp"
    };

    readonly IFsUtil fs;
    readonly string locationDirectory;

    public ReadTool(IFsUtil fs, string locationDirectory)
        : base("read", "Read a text file or supported image, page through a large UTF-8 text file by line offset, or list a directory page. Relative paths resolve from the current location; absolute paths inside it are accepted, while external absolute paths require external_directory approval.")
    {
        this.fs = fs;
        this.locationDirectory = locationDirectory;
    }

    public override async Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
    {
        if (input is not ReadToolInput readInput)
            throw new ToolFailure("Invalid input for read tool");

        var resolvedPath = Path.IsPathRooted(readInput.Path)
            ? readInput.Path
            : Path.Combine(locationDirectory, readInput.Path);

        var canonicalPath = await fs.ResolveAsync(resolvedPath);
        var isDir = await fs.IsDirAsync(canonicalPath);

        if (isDir)
        {
            var text = await ListDirectory(canonicalPath, readInput);
            return new ToolOutput(
                new List<ToolOutputContent> { new ToolTextContent { Text = text } },
                null
            );
        }

        var fileOutput = await ReadFile(canonicalPath, readInput);
        if (fileOutput is ReadToolFileOutput fileContent && fileContent.Encoding == "base64" && SupportedImageMimes.Contains(fileContent.Mime))
        {
            return new ToolOutput(
                new List<ToolOutputContent> { new ToolTextContent { Text = "Image read successfully" } },
                fileContent
            );
        }

        if (fileOutput is ReadToolFileOutput plainContent)
        {
            return new ToolOutput(
                new List<ToolOutputContent> { new ToolTextContent { Text = plainContent.Content } },
                plainContent
            );
        }

        if (fileOutput is ReadToolTextPageOutput pageOutput)
        {
            return new ToolOutput(
                new List<ToolOutputContent> { new ToolTextContent { Text = pageOutput.Content } },
                pageOutput
            );
        }

        throw new ToolFailure($"Unable to read {readInput.Path}");
    }

    public override ToolDefinition ToDefinition(string name)
    {
        return new ToolDefinition(
            name,
            Description,
            new { path = "(string) File or directory path", offset = "(int?) 1-based line offset", limit = "(int?) max lines to read" },
            null
        );
    }

    async Task<string> ListDirectory(string path, ReadToolInput input)
    {
        var entries = await fs.ReadDirectoryEntriesAsync(path);
        var offset = (input.Offset ?? 1) - 1;
        var limit = Math.Min(input.Limit ?? MaxReadLines, MaxReadLines);

        var sorted = entries
            .OrderBy(e => e.Type == "directory" ? 0 : 1)
            .ThenBy(e => e.Name)
            .ToArray();

        var selected = sorted.Skip(offset).Take(limit).ToArray();
        var truncated = offset + selected.Length < sorted.Length;

        var lines = new List<string>();
        foreach (var entry in selected)
        {
            var suffix = entry.Type == "directory" ? "/" : "";
            lines.Add($"{entry.Name}{suffix}");
        }

        if (lines.Count == 0)
            return "Directory is empty";

        var result = string.Join("\n", lines);
        if (truncated)
            result += $"\n... (truncated, use offset={offset + selected.Length} to continue)";

        return result;
    }

    async Task<object> ReadFile(string path, ReadToolInput input)
    {
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);

        var imageMime = DetectImageMime(bytes);
        if (imageMime != null)
        {
            if (bytes.Length > MaxMediaIngestBytes)
                throw new ToolFailure($"Media exceeds {MaxMediaIngestBytes} byte ingestion limit: {path}");

            var fileUri = new UriBuilder("file", path).Uri.ToString();
            var fileName = Path.GetFileName(path);
            var fileContent = Convert.ToBase64String(bytes);
            var fileEncoding = "base64";

            return new ReadToolFileOutput(fileUri, fileName, fileContent, fileEncoding, imageMime);
        }

        if (BinaryExtensions.Contains(Path.GetExtension(path)) || IsBinary(bytes))
            throw new ToolFailure($"Cannot read binary file: {path}");

        var paged = bytes.Length > MaxReadBytes || input.Offset.HasValue || input.Limit.HasValue;

        if (!paged)
        {
            var text = Encoding.UTF8.GetString(bytes);
            var fileUri = new UriBuilder("file", path).Uri.ToString();
            var fileName = Path.GetFileName(path);
            var fileEncoding = "utf8";

            return new ReadToolFileOutput(fileUri, fileName, text, fileEncoding, fs.MimeType(path));
        }

        return await ReadPaged(path, bytes, input);
    }

    async Task<ReadToolTextPageOutput> ReadPaged(string path, byte[] bytes, ReadToolInput input)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n');
        var offset = input.Offset ?? 1;
        var limit = Math.Min(input.Limit ?? MaxReadLines, MaxReadLines);

        if (offset > lines.Length)
            throw new ToolFailure($"Offset {offset} is out of range");

        var selectedLines = new List<string>();
        var byteCount = 0;

        for (int i = offset - 1; i < lines.Length && selectedLines.Count < limit; i++)
        {
            var line = lines[i];
            var truncatedLine = line.Length > MaxLineLength
                ? line[..MaxLineLength] + "... (line truncated to 2000 chars)"
                : line;
            var lineBytes = Encoding.UTF8.GetByteCount(truncatedLine) + (selectedLines.Count > 0 ? 1 : 0);

            if (byteCount + lineBytes > MaxReadBytes && selectedLines.Count > 0)
                break;

            selectedLines.Add(truncatedLine);
            byteCount += lineBytes;
        }

        var truncated = offset - 1 + selectedLines.Count < lines.Length;
        int? next = truncated ? offset + selectedLines.Count : null;

        return new ReadToolTextPageOutput(
            string.Join("\n", selectedLines),
            fs.MimeType(path),
            offset,
            truncated,
            next
        );
    }

    static string? DetectImageMime(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
            return "image/jpeg";
        if (bytes.Length >= 4 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            return "image/gif";
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";
        return null;
    }

    static bool IsBinary(byte[] bytes)
    {
        if (bytes.Length == 0) return false;

        int nonPrintable = 0;
        foreach (var b in bytes)
        {
            if (b == 0) return true;
            if (b < 9 || (b > 13 && b < 32)) nonPrintable++;
        }

        return (double)nonPrintable / bytes.Length > 0.3;
    }
}
