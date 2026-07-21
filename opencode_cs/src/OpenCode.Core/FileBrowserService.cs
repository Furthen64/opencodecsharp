using System.Text;
using System.Text.RegularExpressions;

namespace OpenCode.Core;

public record FileBrowserEntry(string Name, string Path, string Absolute, string Type, bool Ignored);

public record FileBrowserContent(string Type, string Content, string? Encoding, string? MimeType);

public class InvalidFilePathException(string message) : Exception(message);

public class InvalidSearchPatternException(string message) : Exception(message);

public interface IFileBrowserService
{
    Task<FileBrowserEntry[]> ListAsync(string path);
    Task<FileBrowserContent> ReadAsync(string path);
    Task<string[]> FindAsync(string query, string? type, int limit);
    Task<Schema.FileSystemMatch[]> FindTextAsync(string pattern, int limit);
}

public sealed class FileBrowserService : IFileBrowserService
{
    private readonly IFsUtil fs;
    private readonly string root;

    public FileBrowserService(IFsUtil fs, string root)
    {
        this.fs = fs;
        var fullRoot = Path.GetFullPath(root);
        this.root = new DirectoryInfo(fullRoot).ResolveLinkTarget(true)?.FullName ?? fullRoot;
    }

    public async Task<FileBrowserEntry[]> ListAsync(string path)
    {
        var directory = ResolveContained(path);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var entries = await fs.ReadDirectoryEntriesAsync(directory);
        return entries
            .OrderBy(entry => entry.Type == "directory" ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry =>
            {
                var absolute = Path.Combine(directory, entry.Name);
                var relative = Normalize(Path.GetRelativePath(root, absolute));
                return new FileBrowserEntry(
                    entry.Name,
                    relative,
                    absolute,
                    entry.Type == "directory" ? "directory" : "file",
                    IsIgnored(relative));
            })
            .ToArray();
    }

    public async Task<FileBrowserContent> ReadAsync(string path)
    {
        var file = ResolveContained(path);
        if (!File.Exists(file))
            return new FileBrowserContent("text", string.Empty, null, null);

        var bytes = await fs.ReadFileBytesAsync(file);
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Contains('\0')) throw new DecoderFallbackException();
            return new FileBrowserContent("text", text.Trim(), null, null);
        }
        catch (DecoderFallbackException)
        {
            return new FileBrowserContent("binary", Convert.ToBase64String(bytes), "base64", fs.MimeType(file));
        }
    }

    public Task<string[]> FindAsync(string query, string? type, int limit)
    {
        if (type is not null && type is not ("file" or "directory"))
            throw new InvalidFilePathException("Type must be 'file' or 'directory'.");

        var results = EnumerateSafe()
            .Where(item => type is null || item.Type == type)
            .Where(item => item.Relative.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Relative.Length)
            .ThenBy(item => item.Relative, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(item => item.Relative)
            .ToArray();
        return Task.FromResult(results);
    }

    public async Task<Schema.FileSystemMatch[]> FindTextAsync(string pattern, int limit)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            throw new InvalidSearchPatternException($"Invalid regular expression: {pattern}");
        }

        var results = new List<Schema.FileSystemMatch>();
        foreach (var item in EnumerateSafe().Where(item => item.Type == "file"))
        {
            if (results.Count >= limit) break;
            string content;
            try
            {
                content = await File.ReadAllTextAsync(item.Absolute);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                continue;
            }

            var offset = 0;
            foreach (var line in content.Split('\n'))
            {
                var matches = regex.Matches(line);
                if (matches.Count > 0)
                {
                    results.Add(new Schema.FileSystemMatch(
                        new Schema.FileSystemEntry(item.Relative, "file"),
                        content.AsSpan(0, offset).Count('\n') + 1,
                        offset,
                        line.TrimEnd('\r'),
                        matches.Select(match => new Schema.FileSystemSubmatch(match.Value, match.Index, match.Index + match.Length)).ToArray()));
                    if (results.Count >= limit) break;
                }
                offset += line.Length + 1;
            }
        }
        return results.ToArray();
    }

    private IEnumerable<(string Absolute, string Relative, string Type)> EnumerateSafe()
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(child);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                var relative = Normalize(Path.GetRelativePath(root, child));
                if (IsIgnored(relative)) continue;
                yield return (child, relative, isDirectory ? "directory" : "file");
                if (isDirectory) pending.Push(child);
            }
        }
    }

    private string ResolveContained(string path)
    {
        var candidate = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        if (!fs.Contains(root, candidate))
            throw new InvalidFilePathException("Path escapes the server directory.");

        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "" or ".") continue;
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if (!File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) continue;
            var target = Directory.Exists(current)
                ? new DirectoryInfo(current).ResolveLinkTarget(true)?.FullName
                : new FileInfo(current).ResolveLinkTarget(true)?.FullName;
            if (target is null || !fs.Contains(root, target))
                throw new InvalidFilePathException("Path escapes the server directory through a symbolic link.");
            current = target;
        }
        return current;
    }

    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsIgnored(string relative) => relative.Split('/').Any(segment => segment is ".git" or "bin" or "obj");
}
