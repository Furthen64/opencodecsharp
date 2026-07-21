namespace OpenCode.Core;

public record ModelParseResult(
    string ProviderId,
    string ModelId
);

public static class ModelService
{
    public static ModelParseResult Parse(string input)
    {
        var parts = input.Split('/', 2);
        var providerId = parts[0];
        var modelId = parts.Length > 1 ? parts[1] : string.Empty;
        return new ModelParseResult(providerId, modelId);
    }

    public static string ResolveModelId(string? modelId, string? configuredModel)
    {
        if (!string.IsNullOrEmpty(modelId))
            return modelId;
        if (!string.IsNullOrEmpty(configuredModel))
            return configuredModel;
        return "unknown";
    }

    public static string FormatModelRef(string providerId, string modelId)
    {
        return $"{providerId}/{modelId}";
    }

    public static bool TryParseModelRef(string input, out string providerId, out string modelId)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            providerId = string.Empty;
            modelId = string.Empty;
            return false;
        }

        var result = Parse(input);
        providerId = result.ProviderId;
        modelId = result.ModelId;
        return !string.IsNullOrEmpty(providerId);
    }
}
