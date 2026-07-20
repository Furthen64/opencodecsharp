using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record IntegrationWhen(
    string Key,
    string Op,
    string Value
);

[JsonDerivedType(typeof(IntegrationTextPrompt), typeDiscriminator: "text")]
[JsonDerivedType(typeof(IntegrationSelectPrompt), typeDiscriminator: "select")]
public abstract record IntegrationPrompt;

public record IntegrationTextPrompt(
    string Type,
    string Key,
    string Message,
    string? Placeholder,
    IntegrationWhen? When
) : IntegrationPrompt;

public record IntegrationSelectPrompt(
    string Type,
    string Key,
    string Message,
    IntegrationSelectOption[] Options,
    IntegrationWhen? When
) : IntegrationPrompt;

public record IntegrationSelectOption(
    string Label,
    string Value,
    string? Hint
);

[JsonDerivedType(typeof(IntegrationOAuthMethod), typeDiscriminator: "oauth")]
[JsonDerivedType(typeof(IntegrationKeyMethod), typeDiscriminator: "key")]
[JsonDerivedType(typeof(IntegrationEnvMethod), typeDiscriminator: "env")]
public abstract record IntegrationMethod;

public record IntegrationOAuthMethod(
    string Id,
    string Type,
    string Label,
    IntegrationPrompt[]? Prompts
) : IntegrationMethod;

public record IntegrationKeyMethod(
    string Type,
    string? Label
) : IntegrationMethod;

public record IntegrationEnvMethod(
    string Type,
    string[] Names
) : IntegrationMethod;

public record IntegrationRef(
    string Id,
    string Name
);

public record IntegrationInfo(
    string Id,
    string Name,
    IntegrationMethod[] Methods,
    ConnectionInfo[] Connections
);

public record IntegrationAttemptTime(
    double Created,
    double Expires
);

public record IntegrationAttempt(
    string AttemptId,
    string Url,
    string Instructions,
    string Mode,
    IntegrationAttemptTime Time
);

[JsonDerivedType(typeof(IntegrationAttemptStatusPending), typeDiscriminator: "pending")]
[JsonDerivedType(typeof(IntegrationAttemptStatusComplete), typeDiscriminator: "complete")]
[JsonDerivedType(typeof(IntegrationAttemptStatusFailed), typeDiscriminator: "failed")]
[JsonDerivedType(typeof(IntegrationAttemptStatusExpired), typeDiscriminator: "expired")]
public abstract record IntegrationAttemptStatus;

public record IntegrationAttemptStatusPending(
    string Status,
    IntegrationAttemptTime Time
) : IntegrationAttemptStatus;

public record IntegrationAttemptStatusComplete(
    string Status,
    IntegrationAttemptTime Time
) : IntegrationAttemptStatus;

public record IntegrationAttemptStatusFailed(
    string Status,
    string Message,
    IntegrationAttemptTime Time
) : IntegrationAttemptStatus;

public record IntegrationAttemptStatusExpired(
    string Status,
    IntegrationAttemptTime Time
) : IntegrationAttemptStatus;
