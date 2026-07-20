using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record PromptInput(
    string Text,
    string? SessionId
);
