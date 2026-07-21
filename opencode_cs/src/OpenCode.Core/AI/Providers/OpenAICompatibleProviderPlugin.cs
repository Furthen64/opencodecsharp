using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCode.Core.AI.Providers;

public class OpenAICompatibleProviderPlugin : IProviderPlugin
{
    public string Id => "openai-compatible";

    public const string PackageAISDK = "@ai-sdk/openai-compatible";

    public Task<ProviderSdkResult?> CreateSdkAsync(ProviderSdkEvent evt)
    {
        if (!evt.Package.Contains("@ai-sdk/openai-compatible"))
            return Task.FromResult<ProviderSdkResult?>(null);

        if (!evt.Options.ContainsKey("includeUsage"))
            evt.Options["includeUsage"] = true;

        var config = ResolveConfig(evt.Options);
        var sdk = new OpenAICompatibleSdk(config);
        return Task.FromResult<ProviderSdkResult?>(new ProviderSdkResult(sdk));
    }

    public Task<ProviderLanguageResult?> CreateLanguageAsync(ProviderLanguageEvent evt)
    {
        if (evt.Sdk is not OpenAICompatibleSdk sdk)
            return Task.FromResult<ProviderLanguageResult?>(null);

        if (evt.Model.Api is not Schema.ModelApiAISDK aisdk)
            return Task.FromResult<ProviderLanguageResult?>(null);

        var model = new OpenAICompatibleLanguageModel(
            sdk.Config,
            aisdk.Id,
            evt.Model.ProviderId
        );
        return Task.FromResult<ProviderLanguageResult?>(new ProviderLanguageResult(model));
    }

    static LanguageModelConfig ResolveConfig(Dictionary<string, object> options)
    {
        var apiKey = "";
        if (options.TryGetValue("apiKey", out var ak))
            apiKey = ak.ToString() ?? "";

        string? baseUrl = null;
        if (options.TryGetValue("baseURL", out var url))
            baseUrl = url.ToString();

        var headers = new Dictionary<string, string>();
        var settings = new Dictionary<string, object>();
        foreach (var (k, v) in options)
        {
            if (k is "apiKey" or "baseURL" or "name" or "includeUsage") continue;
            settings[k] = v;
        }

        if (options.TryGetValue("headers", out var hdrObj) && hdrObj is JsonElement hdrEl)
        {
            if (hdrEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in hdrEl.EnumerateObject())
                {
                    headers[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
        }

        return new LanguageModelConfig(apiKey, baseUrl, headers, settings);
    }
}

public class OpenAICompatibleSdk
{
    public LanguageModelConfig Config { get; }
    public OpenAICompatibleSdk(LanguageModelConfig config) => Config = config;
}

public class OpenAICompatibleLanguageModel : OpenAILanguageModel
{
    public OpenAICompatibleLanguageModel(LanguageModelConfig config, string modelId, string providerId)
        : base(config, modelId, providerId)
    {
    }
}
