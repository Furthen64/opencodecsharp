using System.Text.Json.Serialization;

namespace OpenCode.Schema;

[JsonDerivedType(typeof(CredentialOAuth), typeDiscriminator: "oauth")]
[JsonDerivedType(typeof(CredentialKey), typeDiscriminator: "key")]
public abstract record CredentialValue;

public record CredentialOAuth(
    string Type,
    string MethodId,
    string Refresh,
    string Access,
    long Expires,
    Dictionary<string, object>? Metadata
) : CredentialValue;

public record CredentialKey(
    string Type,
    string Key,
    Dictionary<string, object>? Metadata
) : CredentialValue;
