using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record FileSystemEntry(
    string Path,
    string Type
);

public record FileSystemSubmatch(
    string Text,
    int Start,
    int End
);

public record FileSystemMatch(
    FileSystemEntry Entry,
    int Line,
    int Offset,
    string Text,
    FileSystemSubmatch[] Submatches
);

public record FileSystemFindInput(
    string Query,
    string? Type,
    int? Limit
);
