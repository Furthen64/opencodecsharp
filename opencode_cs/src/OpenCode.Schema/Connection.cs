using System.Text.Json.Serialization;

namespace OpenCode.Schema;

[JsonDerivedType(typeof(ConnectionCredentialInfo), typeDiscriminator: "credential")]
[JsonDerivedType(typeof(ConnectionEnvInfo), typeDiscriminator: "env")]
public abstract record ConnectionInfo;

public record ConnectionCredentialInfo(
    string Type,
    string Id,
    string Label
) : ConnectionInfo;

public record ConnectionEnvInfo(
    string Type,
    string Name
) : ConnectionInfo;
