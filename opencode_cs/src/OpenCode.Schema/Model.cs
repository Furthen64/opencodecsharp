using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public record ModelRef(
    string Id,
    string ProviderId,
    string? Variant
);

public record ModelCapabilities(
    bool Tools,
    string[] Input,
    string[] Output
);

public record ModelCostTier(
    string Type,
    int Size
);

public record ModelCost(
    ModelCostTier? Tier,
    double Input,
    double Output,
    ModelCacheCost Cache
);

public record ModelCacheCost(
    double Read,
    double Write
);

[JsonDerivedType(typeof(ModelApiAISDK), typeDiscriminator: "aisdk")]
[JsonDerivedType(typeof(ModelApiNative), typeDiscriminator: "native")]
public abstract record ModelApi;

public record ModelApiAISDK(
    string Id,
    string Type,
    string Package,
    string? Url,
    Dictionary<string, object>? Settings
) : ModelApi;

public record ModelApiNative(
    string Id,
    string Type,
    string? Url,
    Dictionary<string, object> Settings
) : ModelApi;

public record ModelRequest(
    Dictionary<string, string> Headers,
    Dictionary<string, object> Body,
    string? Variant
);

public record ModelVariant(
    string Id,
    Dictionary<string, string> Headers,
    Dictionary<string, object> Body
);

public record ModelLimit(
    int Context,
    int? Input,
    int Output
);

public record ModelInfo(
    string Id,
    string ProviderId,
    string? Family,
    string Name,
    ModelApi Api,
    ModelCapabilities Capabilities,
    ModelRequest Request,
    ModelVariant[] Variants,
    ModelTime Time,
    ModelCost[] Cost,
    string Status,
    bool Enabled,
    ModelLimit Limit
);

public record ModelTime(
    long Released
);
