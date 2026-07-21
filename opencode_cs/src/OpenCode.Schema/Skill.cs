using System.Text.Json.Serialization;

namespace OpenCode.Schema;

[JsonDerivedType(typeof(SkillDirectorySource), typeDiscriminator: "directory")]
[JsonDerivedType(typeof(SkillUrlSource), typeDiscriminator: "url")]
[JsonDerivedType(typeof(SkillEmbeddedSource), typeDiscriminator: "embedded")]
public abstract record SkillSource;

public record SkillDirectorySource(
    string Type,
    string Path
) : SkillSource;

public record SkillUrlSource(
    string Type,
    string Url
) : SkillSource;

public record SkillInfo(
    string Name,
    string? Description,
    bool? Slash,
    string Location,
    string Content
);

public record SkillEmbeddedSource(
    string Type,
    SkillInfo Skill
) : SkillSource;
