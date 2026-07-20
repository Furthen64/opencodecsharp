using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record FileDiff(
    string Path,
    string Status,
    int Additions,
    int Deletions,
    string Patch
);

public record RevertState(
    string MessageId,
    string? PartId,
    string? Snapshot,
    string? Diff,
    FileDiff[]? Files
);
