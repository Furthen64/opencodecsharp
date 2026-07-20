using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record SkillDirectorySource(
    string Type,
    string Path
);

public record SkillUrlSource(
    string Type,
    string Url
);

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
);

public abstract record SkillSource;
