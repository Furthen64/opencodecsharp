namespace OpenCode.Core.AI;

public class ModelLLMClient : ILLMClient
{
    readonly IAISDKService aisdk;

    public ModelLLMClient(IAISDKService aisdk)
    {
        this.aisdk = aisdk;
    }

    public async IAsyncEnumerable<LLMStreamEvent> StreamAsync(LLMRequest request)
    {
        var model = await ResolveModelAsync(request.Model);
        await foreach (var evt in model.StreamAsync(request))
        {
            yield return evt;
        }
    }

    async Task<ILanguageModel> ResolveModelAsync(string modelRef)
    {
        var parts = modelRef.Split('/', 2);
        var providerId = parts.Length > 1 ? parts[0] : modelRef;
        var modelId = parts.Length > 1 ? parts[1] : modelRef;

        var modelInfo = new Schema.ModelInfo(
            Id: modelId,
            ProviderId: providerId,
            Family: null,
            Name: modelId,
            Api: new Schema.ModelApiAISDK(
                Id: modelId,
                Type: "aisdk",
                Package: ResolvePackage(providerId),
                Url: null,
                Settings: null),
            Capabilities: new Schema.ModelCapabilities(
                Tools: true,
                Input: new[] { "text" },
                Output: new[] { "text" }),
            Request: new Schema.ModelRequest(
                Headers: new Dictionary<string, string>(),
                Body: new Dictionary<string, object>(),
                Variant: null),
            Variants: Array.Empty<Schema.ModelVariant>(),
            Time: new Schema.ModelTime(Released: 0),
            Cost: Array.Empty<Schema.ModelCost>(),
            Status: "active",
            Enabled: true,
            Limit: new Schema.ModelLimit(Context: 128000, Input: null, Output: 4096)
        );

        var result = await aisdk.GetLanguageModelAsync(modelInfo);

        if (result is ILanguageModel lm)
            return lm;

        throw new ProviderInitError(providerId,
            $"Provider returned {result?.GetType().Name ?? "null"} instead of ILanguageModel");
    }

    static string ResolvePackage(string providerId) => providerId switch
    {
        Schema.ProviderIds.OpenAI => "@ai-sdk/openai",
        Schema.ProviderIds.Anthropic => "@ai-sdk/anthropic",
        Schema.ProviderIds.Google => "@ai-sdk/google",
        _ => "@ai-sdk/openai-compatible"
    };
}
