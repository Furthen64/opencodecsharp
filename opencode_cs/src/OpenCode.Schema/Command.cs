using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record CommandInfo(
    string Name,
    string Template,
    string? Description,
    string? Agent,
    ModelRef? Model,
    bool? Subtask
);
