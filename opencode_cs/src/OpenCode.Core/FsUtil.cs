using System.IO;
using System.Threading.Tasks;

namespace OpenCode.Core;

public interface IFsUtil
{
    Task<bool> ExistsSafeAsync(string path);
    Task<string?> ReadFileStringSafeAsync(string path);
    Task<bool> IsDirAsync(string path);
    Task<bool> IsFileAsync(string path);
    Task<DirEntry[]> ReadDirectoryEntriesAsync(string path);
    Task<string> ResolveAsync(string path);
    Task<object> ReadJsonAsync(string path);
    Task WriteJsonAsync(string path, object data);
    Task EnsureDirAsync(string path);
    Task WriteWithDirsAsync(string path, string content);
    Task<string[]> FindUpAsync(string target, string start, string? stop = null);
    Task<string[]> UpAsync(string[] targets, string start, string? stop = null);
    string MimeType(string path);
    bool Overlaps(string a, string b);
    bool Contains(string parent, string child);
    Task<bool> ExistsAsync(string path);
    Task<FileStat> StatAsync(string path);
    Task<byte[]> ReadFileBytesAsync(string path);
    Task WriteFileBytesAsync(string path, byte[] content);
    Task WriteFileStringAsync(string path, string content, bool append = false);
    Task RemoveAsync(string path);
}

public record DirEntry(string Name, string Type);

public record FileStat(string Type, long Size, long Modified);

public class FsUtil : IFsUtil
{
    public Task<bool> ExistsSafeAsync(string path)
    {
        try
        {
            return Task.FromResult(File.Exists(path) || Directory.Exists(path));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<string?> ReadFileStringSafeAsync(string path)
    {
        try
        {
            return await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public Task<bool> IsDirAsync(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return Task.FromResult(attr.HasFlag(FileAttributes.Directory));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> IsFileAsync(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return Task.FromResult(!attr.HasFlag(FileAttributes.Directory));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<DirEntry[]> ReadDirectoryEntriesAsync(string path)
    {
        var entries = System.IO.Directory.EnumerateFileSystemEntries(path);
        var list = new List<DirEntry>();
        foreach (var entry in entries)
        {
            var attr = File.GetAttributes(entry);
            var type = attr.HasFlag(FileAttributes.Directory) ? "directory"
                : attr.HasFlag(FileAttributes.ReparsePoint) ? "symlink"
                : "file";
            list.Add(new DirEntry(Path.GetFileName(entry), type));
        }
        return Task.FromResult(list.ToArray());
    }

    public Task<string> ResolveAsync(string path) => Task.FromResult(Path.GetFullPath(path));

    public async Task<object> ReadJsonAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Deserialize<object>(text)
            ?? throw new InvalidDataException($"JSON file is empty: {path}");
    }

    public async Task WriteJsonAsync(string path, object data)
    {
        var content = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
    }

    public Task EnsureDirAsync(string path) => Task.FromResult(Directory.CreateDirectory(path));

    public async Task WriteWithDirsAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
    }

    public Task<string[]> FindUpAsync(string target, string start, string? stop = null)
    {
        var result = new System.Collections.Generic.List<string>();
        var current = Path.GetFullPath(start);
        while (true)
        {
            var search = Path.Combine(current, target);
            if (File.Exists(search) || Directory.Exists(search))
                result.Add(search);
            if (stop != null && Path.GetFullPath(current) == Path.GetFullPath(stop)) break;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent == null || parent == current) break;
            current = parent;
        }
        return Task.FromResult(result.ToArray());
    }

    public Task<string[]> UpAsync(string[] targets, string start, string? stop = null)
    {
        var result = new System.Collections.Generic.List<string>();
        var current = Path.GetFullPath(start);
        while (true)
        {
            foreach (var target in targets)
            {
                var search = Path.Combine(current, target);
                if (File.Exists(search) || Directory.Exists(search))
                    result.Add(search);
            }
            if (stop != null && Path.GetFullPath(current) == Path.GetFullPath(stop)) break;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent == null || parent == current) break;
            current = parent;
        }
        return Task.FromResult(result.ToArray());
    }

    public string MimeType(string path)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant();
        return ext switch
        {
            ".json" => "application/json",
            ".txt" => "text/plain",
            ".html" => "text/html",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".js" => "application/javascript",
            ".ts" => "text/typescript",
            ".cs" => "text/x-csharp",
            ".py" => "text/x-python",
            ".md" => "text/markdown",
            ".yaml" or ".yml" => "text/yaml",
            _ => "application/octet-stream"
        };
    }

    public bool Overlaps(string a, string b) => Contains(a, b) || Contains(b, a);

    public bool Contains(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "" || relative == "." || (!Path.IsPathRooted(relative) && !relative.StartsWith(".."));
    }

    public Task<bool> ExistsAsync(string path)
    {
        try
        {
            return Task.FromResult(File.Exists(path) || Directory.Exists(path));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<FileStat> StatAsync(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            var dirInfo = new DirectoryInfo(path);
            if (dirInfo.Exists)
                return Task.FromResult(new FileStat("Directory", 0, new DateTimeOffset(dirInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
            throw new FileNotFoundException($"Path not found: {path}");
        }
        return Task.FromResult(new FileStat("File", info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
    }

    public async Task<byte[]> ReadFileBytesAsync(string path)
    {
        return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
    }

    public async Task WriteFileBytesAsync(string path, byte[] content)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(path, content).ConfigureAwait(false);
    }

    public async Task WriteFileStringAsync(string path, string content, bool append = false)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        if (append)
            await File.AppendAllTextAsync(path, content).ConfigureAwait(false);
        else
            await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
    }

    public Task RemoveAsync(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        else if (Directory.Exists(path))
            Directory.Delete(path, true);
        return Task.CompletedTask;
    }
}
