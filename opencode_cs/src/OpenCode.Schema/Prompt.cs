using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record PromptSource(
    double Start,
    double End,
    string Text
);

public record FileAttachment(
    string Uri,
    string Mime,
    string? Name,
    string? Description,
    PromptSource? Source
);

public record AgentAttachment(
    string Name,
    PromptSource? Source
);

public record Prompt(
    string Text,
    FileAttachment[]? Files,
    AgentAttachment[]? Agents
);
