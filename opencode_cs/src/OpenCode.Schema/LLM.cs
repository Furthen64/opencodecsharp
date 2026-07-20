using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record LLMProviderMetadata(
    Dictionary<string, Dictionary<string, object>> Data
);

[JsonDerivedType(typeof(ToolTextContent), typeDiscriminator: "text")]
[JsonDerivedType(typeof(ToolFileContent), typeDiscriminator: "file")]
public abstract record ToolContent;

public record ToolTextContent(
    string Type,
    string Text
) : ToolContent;

public record ToolFileContent(
    string Type,
    string Uri,
    string Mime,
    string? Name
) : ToolContent;
