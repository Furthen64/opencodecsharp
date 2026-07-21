using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCode.Core.AI.Providers;

public class GoogleProviderPlugin : IProviderPlugin
{
    public string Id => "google";

    public const string PackageAISDK = "@ai-sdk/google";

    public Task<ProviderSdkResult?> CreateSdkAsync(ProviderSdkEvent evt)
    {
        if (evt.Package != PackageAISDK)
            return Task.FromResult<ProviderSdkResult?>(null);

        var config = ResolveConfig(evt.Options);
        var sdk = new GoogleSdk(config);
        return Task.FromResult<ProviderSdkResult?>(new ProviderSdkResult(sdk));
    }

    public Task<ProviderLanguageResult?> CreateLanguageAsync(ProviderLanguageEvent evt)
    {
        if (evt.Model.ProviderId != Schema.ProviderIds.Google)
            return Task.FromResult<ProviderLanguageResult?>(null);

        if (evt.Sdk is not GoogleSdk sdk)
            return Task.FromResult<ProviderLanguageResult?>(null);

        if (evt.Model.Api is not Schema.ModelApiAISDK aisdk)
            return Task.FromResult<ProviderLanguageResult?>(null);

        var model = new GoogleLanguageModel(
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
        if (string.IsNullOrEmpty(apiKey))
            apiKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                     ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";

        string? baseUrl = null;
        if (options.TryGetValue("baseURL", out var url))
            baseUrl = url.ToString();

        var headers = new Dictionary<string, string>();
        var settings = new Dictionary<string, object>();
        foreach (var (k, v) in options)
        {
            if (k is "apiKey" or "baseURL" or "name") continue;
            settings[k] = v;
        }

        return new LanguageModelConfig(apiKey, baseUrl, headers, settings);
    }
}

public class GoogleSdk
{
    public LanguageModelConfig Config { get; }
    public GoogleSdk(LanguageModelConfig config) => Config = config;
}

public class GoogleLanguageModel : ILanguageModel
{
    readonly LanguageModelConfig config;
    readonly HttpClient httpClient;

    public string ProviderId { get; }
    public string ModelId { get; }
    public string ApiId { get; }

    public GoogleLanguageModel(LanguageModelConfig config, string modelId, string providerId)
    {
        this.config = config;
        ModelId = modelId;
        ApiId = modelId;
        ProviderId = providerId;

        httpClient = new HttpClient();
        if (config.Headers != null)
        {
            foreach (var (k, v) in config.Headers)
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
        }
    }

    public async IAsyncEnumerable<LLMStreamEvent> StreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var baseUrl = config.BaseUrl?.TrimEnd('/')
                      ?? "https://generativelanguage.googleapis.com/v1beta";
        var endpoint = $"{baseUrl}/models/{ModelId}:streamGenerateContent?alt=sse&key={config.ApiKey}";

        var body = BuildRequestBody(request);
        var json = JsonSerializer.Serialize(body, SerializerDefaults.JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        using var httpResp = await httpClient.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        httpResp.EnsureSuccessStatusCode();

        using var stream = await httpResp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..];

            GoogleGenerateResponse? resp;
            try
            {
                resp = JsonSerializer.Deserialize<GoogleGenerateResponse>(data, SerializerDefaults.JsonOptions);
            }
            catch
            {
                continue;
            }

            if (resp?.Candidates == null) continue;

            foreach (var candidate in resp.Candidates)
            {
                if (candidate.Content?.Parts == null) continue;

                foreach (var part in candidate.Content.Parts)
                {
                    if (part.Text != null)
                    {
                        yield return new LLMStreamEvent("text", part.Text, null, null, null, null);
                    }

                    if (part.FunctionCall != null)
                    {
                        var input = part.FunctionCall.Args != null
                            ? JsonSerializer.Deserialize<Dictionary<string, object>>(
                                JsonSerializer.Serialize(part.FunctionCall.Args, SerializerDefaults.JsonOptions),
                                SerializerDefaults.JsonOptions) ?? new Dictionary<string, object>()
                            : new Dictionary<string, object>();

                        yield return new LLMStreamEvent(
                            "tool_call", null,
                            new LLMToolCall(
                                $"call_{Guid.NewGuid():N}",
                                part.FunctionCall.Name ?? "",
                                input),
                            null, null, null);
                    }
                }
            }

            if (resp.UsageMetadata != null)
            {
                yield return new LLMStreamEvent(
                    "usage", null, null, null,
                    new LLMUsage(
                        resp.UsageMetadata.PromptTokenCount,
                        resp.UsageMetadata.CandidatesTokenCount,
                        null),
                    null);
            }
        }
    }

    object BuildRequestBody(LLMRequest request)
    {
        var contents = new List<object>();

        foreach (var msg in request.Messages)
        {
            var role = msg.Role == "assistant" ? "model" : "user";
            contents.Add(new
            {
                role,
                parts = new[] { new { text = msg.Content?.ToString() ?? "" } }
            });
        }

        var body = new Dictionary<string, object>
        {
            ["contents"] = contents,
            ["generationConfig"] = new { temperature = 1.0 }
        };

        if (request.Tools is { Length: > 0 })
        {
            body["tools"] = new[]
            {
                new
                {
                    functionDeclarations = request.Tools.Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        parameters = t.InputSchema
                    }).ToArray()
                }
            };
        }

        if (request.System.Length > 0)
        {
            body["systemInstruction"] = new
            {
                parts = request.System.Select(s => new { text = s }).ToArray()
            };
        }

        return body;
    }
}

#region JSON DTOs

file class GoogleGenerateResponse
{
    [JsonPropertyName("candidates")]
    public List<GoogleCandidate>? Candidates { get; set; }

    [JsonPropertyName("usageMetadata")]
    public GoogleUsageMetadata? UsageMetadata { get; set; }
}

file class GoogleCandidate
{
    [JsonPropertyName("content")]
    public GoogleContent? Content { get; set; }
}

file class GoogleContent
{
    [JsonPropertyName("parts")]
    public List<GooglePart>? Parts { get; set; }
}

file class GooglePart
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("functionCall")]
    public GoogleFunctionCall? FunctionCall { get; set; }
}

file class GoogleFunctionCall
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("args")]
    public object? Args { get; set; }
}

file class GoogleUsageMetadata
{
    [JsonPropertyName("promptTokenCount")]
    public int PromptTokenCount { get; set; }

    [JsonPropertyName("candidatesTokenCount")]
    public int CandidatesTokenCount { get; set; }
}

#endregion
