using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record PtyInfo(
    string Id,
    string Title,
    string Command,
    string[] Args,
    string Cwd,
    string Status,
    int Pid,
    int? ExitCode
);

public record PtyCreateInput(
    string? Command,
    string[]? Args,
    string? Cwd,
    string? Title,
    Dictionary<string, string>? Env
);

public record PtyUpdateInput(
    string? Title,
    PtySize? Size
);

public record PtySize(
    int Rows,
    int Cols
);
