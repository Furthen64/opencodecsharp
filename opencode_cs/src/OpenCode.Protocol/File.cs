namespace OpenCode.Protocol;

public record FileTextValue(string Text);

public record FileTextSubmatch(FileTextValue Match, int Start, int End);

public record FileTextMatch(
    FileTextValue Path,
    FileTextValue Lines,
    int LineNumber,
    int AbsoluteOffset,
    FileTextSubmatch[] Submatches
);

public record FileNode(
    string Name,
    string Path,
    string Absolute,
    string Type,
    bool Ignored
);

public record FileContent(
    string Type,
    string Content,
    string? Encoding,
    string? MimeType
);
