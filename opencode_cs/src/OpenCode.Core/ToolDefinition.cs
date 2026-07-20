using System.Text.Json.Serialization;

namespace OpenCode.Core;

public record ToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] object? InputSchema,
    [property: JsonPropertyName("outputSchema")] object? OutputSchema
);
