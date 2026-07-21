namespace OpenCode.Core;

public record FileMutationTarget(
    string Canonical,
    string Resource
);

public record FileMutationWriteResult(
    string Operation,
    string Target,
    string Resource,
    bool Existed
);

public record FileMutationWriteInput(
    FileMutationTarget Target,
    string Content
);

public record FileMutationConditionalWriteInput(
    FileMutationTarget Target,
    byte[] Expected,
    string Content
);

public record FileMutationRemoveInput(
    FileMutationTarget Target
);

public class FileMutationStaleContentError : Exception
{
    public string Path { get; }
    public FileMutationStaleContentError(string path) : base($"Stale content: {path}")
    {
        Path = path;
    }
}

public class FileMutationTargetExistsError : Exception
{
    public string Path { get; }
    public FileMutationTargetExistsError(string path) : base($"Target already exists: {path}")
    {
        Path = path;
    }
}

public interface IFileMutationService
{
    Task<FileMutationWriteResult> CreateAsync(FileMutationWriteInput input);
    Task<FileMutationWriteResult> WriteAsync(FileMutationWriteInput input);
    Task<FileMutationWriteResult> WriteTextPreservingBomAsync(FileMutationWriteInput input);
    Task<FileMutationWriteResult> WriteIfUnchangedAsync(FileMutationConditionalWriteInput input);
    Task<FileMutationWriteResult> RemoveAsync(FileMutationRemoveInput input);
}

public class FileMutationService : IFileMutationService
{
    readonly IFsUtil fs;

    public FileMutationService(IFsUtil fs)
    {
        this.fs = fs;
    }

    public async Task<FileMutationWriteResult> CreateAsync(FileMutationWriteInput input)
    {
        if (await fs.ExistsAsync(input.Target.Canonical))
            throw new FileMutationTargetExistsError(input.Target.Canonical);

        var content = EnsureNewline(input.Content);
        await fs.WriteFileStringAsync(input.Target.Canonical, content);
        return new FileMutationWriteResult("write", input.Target.Canonical, input.Target.Resource, false);
    }

    public async Task<FileMutationWriteResult> WriteAsync(FileMutationWriteInput input)
    {
        var existed = await fs.ExistsAsync(input.Target.Canonical);
        var content = EnsureNewline(input.Content);
        await fs.WriteFileStringAsync(input.Target.Canonical, content);
        return new FileMutationWriteResult("write", input.Target.Canonical, input.Target.Resource, existed);
    }

    public async Task<FileMutationWriteResult> WriteTextPreservingBomAsync(FileMutationWriteInput input)
    {
        var next = SplitBom(input.Content);
        string? current = await fs.ReadFileStringSafeAsync(input.Target.Canonical);
        bool hasBom = current != null && HasUtf8Bom(current);
        var content = JoinBom(next.Text, hasBom || next.Bom);
        await fs.WriteFileStringAsync(input.Target.Canonical, content);
        return new FileMutationWriteResult("write", input.Target.Canonical, input.Target.Resource, current != null);
    }

    public async Task<FileMutationWriteResult> WriteIfUnchangedAsync(FileMutationConditionalWriteInput input)
    {
        var current = await fs.ReadFileBytesAsync(input.Target.Canonical);
        if (!SameBytes(current, input.Expected))
            throw new FileMutationStaleContentError(input.Target.Canonical);

        await fs.WriteFileStringAsync(input.Target.Canonical, EnsureNewline(input.Content));
        return new FileMutationWriteResult("write", input.Target.Canonical, input.Target.Resource, true);
    }

    public async Task<FileMutationWriteResult> RemoveAsync(FileMutationRemoveInput input)
    {
        var existed = await fs.ExistsAsync(input.Target.Canonical);
        await fs.RemoveAsync(input.Target.Canonical);
        return new FileMutationWriteResult("remove", input.Target.Canonical, input.Target.Resource, existed);
    }

    static string EnsureNewline(string content) =>
        string.IsNullOrEmpty(content) || content.EndsWith('\n') ? content : content + "\n";

    static bool HasUtf8Bom(string content) =>
        content.Length >= 1 && content[0] == '\uFEFF';

    static (bool Bom, string Text) SplitBom(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
            return (true, text[1..]);
        return (false, text);
    }

    static string JoinBom(string text, bool bom)
    {
        var stripped = SplitBom(text).Text;
        return bom ? "\uFEFF" + stripped : stripped;
    }

    static bool SameBytes(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
            if (left[i] != right[i]) return false;
        return true;
    }
}
