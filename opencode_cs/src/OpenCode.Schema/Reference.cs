using System.Text.Json.Serialization;

namespace OpenCode.Schema;

[JsonDerivedType(typeof(ReferenceLocalSource), typeDiscriminator: "local")]
[JsonDerivedType(typeof(ReferenceGitSource), typeDiscriminator: "git")]
public abstract record ReferenceSource;

public record ReferenceLocalSource(
    string Type,
    string Path,
    string? Description,
    bool? Hidden
) : ReferenceSource;

public record ReferenceGitSource(
    string Type,
    string Repository,
    string? Branch,
    string? Description,
    bool? Hidden
) : ReferenceSource;

public record ReferenceInfo(
    string Name,
    string Path,
    string? Description,
    bool? Hidden,
    ReferenceSource Source
);
