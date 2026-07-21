namespace OpenCode.Core;

public abstract record PatchHunk
{
    public abstract string Type { get; }
    public string Path { get; init; } = null!;
}

public record PatchAddHunk : PatchHunk
{
    public override string Type => "add";
    public string Contents { get; init; } = null!;
}

public record PatchDeleteHunk : PatchHunk
{
    public override string Type => "delete";
}

public record UpdateFileChunk(
    string[] OldLines,
    string[] NewLines,
    string? ChangeContext,
    bool? EndOfFile
);

public record PatchUpdateHunk : PatchHunk
{
    public override string Type => "update";
    public string? MovePath { get; init; }
    public UpdateFileChunk[] Chunks { get; init; } = null!;
}

public record FileUpdateResult(
    string Content,
    bool Bom
);

public static class PatchParser
{
    public static PatchHunk[] Parse(string patchText)
    {
        var lines = StripHeredoc(patchText.Trim()).Split("\n");
        int begin = Array.FindIndex(lines, l => l.Trim() == "*** Begin Patch");
        int end = Array.FindIndex(lines, l => l.Trim() == "*** End Patch");
        if (begin == -1 || end == -1 || begin >= end)
            throw new InvalidOperationException("Invalid patch format: missing Begin/End markers");

        var hunks = new List<PatchHunk>();
        int index = begin + 1;
        while (index < end)
        {
            var line = lines[index];
            if (line.StartsWith("*** Add File:"))
            {
                var path = line["*** Add File:".Length..].Trim();
                if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Invalid add file path");
                var parsed = ParseAdd(lines, index + 1);
                hunks.Add(new PatchAddHunk { Path = path, Contents = parsed.content });
                index = parsed.next;
                continue;
            }
            if (line.StartsWith("*** Delete File:"))
            {
                var path = line["*** Delete File:".Length..].Trim();
                if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Invalid delete file path");
                hunks.Add(new PatchDeleteHunk { Path = path });
                index++;
                continue;
            }
            if (line.StartsWith("*** Update File:"))
            {
                var path = line["*** Update File:".Length..].Trim();
                if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Invalid update file path");
                int next = index + 1;
                string? movePath = null;
                if (next < end && lines[next].StartsWith("*** Move to:"))
                {
                    movePath = lines[next]["*** Move to:".Length..].Trim();
                    if (string.IsNullOrEmpty(movePath)) throw new InvalidOperationException("Invalid move file path");
                    next++;
                }
                var parsed = ParseUpdate(lines, next, end);
                if (parsed.chunks.Length == 0)
                    throw new InvalidOperationException($"Invalid update hunk for {path}: expected at least one @@ chunk");
                hunks.Add(new PatchUpdateHunk { Path = path, MovePath = movePath, Chunks = parsed.chunks });
                index = parsed.next;
                continue;
            }
            throw new InvalidOperationException($"Invalid patch line: {line}");
        }
        return hunks.ToArray();
    }

    public static FileUpdateResult Derive(string path, UpdateFileChunk[] chunks, string original)
    {
        var source = SplitBom(original);
        var lines = source.text.Split("\n");
        if (lines.Length > 0 && lines[^1] == "") lines = lines[..^1];
        var replacements = ComputeReplacements(lines, path, chunks);
        var updated = new List<string>(lines);
        foreach (var (start, remove, insert) in replacements.OrderByDescending(r => r.start))
        {
            updated.RemoveRange(start, Math.Min(remove, updated.Count - start));
            updated.InsertRange(start, insert);
        }
        if (updated.Count == 0 || updated[^1] != "") updated.Add("");
        var next = SplitBom(string.Join("\n", updated));
        return new FileUpdateResult(next.text, source.bom || next.bom);
    }

    public static string JoinBom(string text, bool bom)
    {
        var stripped = SplitBom(text).text;
        return bom ? "\uFEFF" + stripped : stripped;
    }

    static (string content, int next) ParseAdd(string[] lines, int start)
    {
        var content = new List<string>();
        int index = start;
        while (index < lines.Length && !lines[index].StartsWith("***"))
        {
            if (!lines[index].StartsWith("+"))
                throw new InvalidOperationException($"Invalid add file line: {lines[index]}");
            content.Add(lines[index][1..]);
            index++;
        }
        return (string.Join("\n", content), index);
    }

    static (UpdateFileChunk[] chunks, int next) ParseUpdate(string[] lines, int start, int end)
    {
        var chunks = new List<UpdateFileChunk>();
        int index = start;
        while (index < end)
        {
            if (!lines[index].StartsWith("@@"))
                throw new InvalidOperationException($"Invalid update file line: {lines[index]}");
            var changeContext = lines[index][2..].Trim() is { Length: > 0 } ctx ? ctx : null;
            var oldLines = new List<string>();
            var newLines = new List<string>();
            bool endOfFile = false;
            index++;
            while (index < end && !lines[index].StartsWith("@@"))
            {
                var line = lines[index];
                if (line == "*** End of File")
                {
                    endOfFile = true;
                    index++;
                    break;
                }
                if (line.StartsWith("***")) break;
                if (line.StartsWith(' '))
                {
                    oldLines.Add(line[1..]);
                    newLines.Add(line[1..]);
                }
                else if (line.StartsWith('-'))
                    oldLines.Add(line[1..]);
                else if (line.StartsWith('+'))
                    newLines.Add(line[1..]);
                else
                    throw new InvalidOperationException($"Invalid update chunk line: {line}");
                index++;
            }
            chunks.Add(new UpdateFileChunk(
                OldLines: oldLines.ToArray(),
                NewLines: newLines.ToArray(),
                ChangeContext: changeContext,
                EndOfFile: endOfFile ? true : null
            ));
        }
        return (chunks.ToArray(), index);
    }

    static List<(int start, int remove, string[] insert)> ComputeReplacements(string[] lines, string path, UpdateFileChunk[] chunks)
    {
        var replacements = new List<(int start, int remove, string[] insert)>();
        int lineIndex = 0;
        foreach (var chunk in chunks)
        {
            if (chunk.ChangeContext != null)
            {
                int context = Seek(lines, new[] { chunk.ChangeContext }, lineIndex);
                if (context == -1)
                    throw new InvalidOperationException($"Failed to find context '{chunk.ChangeContext}' in {path}");
                lineIndex = context + 1;
            }
            if (chunk.OldLines.Length == 0)
            {
                replacements.Add((lines.Length, 0, chunk.NewLines));
                continue;
            }
            var oldLines = chunk.OldLines;
            var newLines = chunk.NewLines;
            int found = Seek(lines, oldLines, lineIndex, chunk.EndOfFile == true);
            if (found == -1 && oldLines.Length > 0 && oldLines[^1] == "")
            {
                oldLines = oldLines[..^1];
                if (newLines.Length > 0 && newLines[^1] == "")
                    newLines = newLines[..^1];
                found = Seek(lines, oldLines, lineIndex, chunk.EndOfFile == true);
            }
            if (found == -1)
                throw new InvalidOperationException($"Failed to find expected lines in {path}:\n{string.Join("\n", chunk.OldLines)}");
            replacements.Add((found, oldLines.Length, newLines));
            lineIndex = found + oldLines.Length;
        }
        return replacements.OrderBy(r => r.start).ToList();
    }

    static int Seek(string[] lines, string[] pattern, int start, bool eof = false)
    {
        if (pattern.Length == 0) return -1;
        var comparers = new Func<string, string, bool>[] { Exact, Rstrip, TrimCompare, Normalized };
        foreach (var compare in comparers)
        {
            if (eof)
            {
                int offset = lines.Length - pattern.Length;
                if (offset >= start && Matches(lines, pattern, offset, compare)) return offset;
            }
            for (int offset = start; offset <= lines.Length - pattern.Length; offset++)
            {
                if (Matches(lines, pattern, offset, compare)) return offset;
            }
        }
        return -1;
    }

    static bool Matches(string[] lines, string[] pattern, int offset, Func<string, string, bool> compare)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (!compare(lines[offset + i], pattern[i])) return false;
        }
        return true;
    }

    static bool Exact(string a, string b) => a == b;
    static bool Rstrip(string a, string b) => a.TrimEnd() == b.TrimEnd();
    static bool TrimCompare(string a, string b) => a.Trim() == b.Trim();
    static bool Normalized(string a, string b) => Normalize(a.Trim()) == Normalize(b.Trim());

    static string Normalize(string value) =>
        value
            .Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201A', '\'').Replace('\u201B', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"').Replace('\u201E', '"').Replace('\u201F', '"')
            .Replace('\u2010', '-').Replace('\u2011', '-').Replace('\u2012', '-').Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u2015', '-')
            .Replace("\u2026", "...")
            .Replace('\u00A0', ' ');

    static (bool bom, string text) SplitBom(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
            return (true, text[1..]);
        return (false, text);
    }

    static string StripHeredoc(string input)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            input,
            @"^(?:cat\s+)?<<['""]?(\w+)['""]?\s*\n([\s\S]*?)\n\1\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline
        );
        return match.Success ? match.Groups[2].Value : input;
    }
}
