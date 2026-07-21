using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCode.Core.AI.Providers;

public class AnthropicProviderPlugin : IProviderPlugin
{
    public string Id => "anthropic";

    public const string PackageAISDK = "@ai-sdk/anthropic";

    public Task<ProviderSdkResult?> CreateSdkAsync(ProviderSdkEvent evt)
    {
        if (evt.Package != PackageAISDK)
            return Task.FromResult<ProviderSdkResult?>(null);

        var config = ResolveConfig(evt.Options);
        var sdk = new AnthropicSdk(config);
        return Task.FromResult<ProviderSdkResult?>(new ProviderSdkResult(sdk));
    }

    public Task<ProviderLanguageResult?> CreateLanguageAsync(ProviderLanguageEvent evt)
    {
        if (evt.Model.ProviderId != Schema.ProviderIds.Anthropic)
            return Task.FromResult<ProviderLanguageResult?>(null);

        if (evt.Sdk is not AnthropicSdk sdk)
            return Task.FromResult<ProviderLanguageResult?>(null);

        if (evt.Model.Api is not Schema.ModelApiAISDK aisdk)
            return Task.FromResult<ProviderLanguageResult?>(null);

        var model = new AnthropicLanguageModel(
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
            apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";

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

public class AnthropicSdk
{
    public LanguageModelConfig Config { get; }
    public AnthropicSdk(LanguageModelConfig config) => Config = config;
}

public class AnthropicLanguageModel : ILanguageModel
{
    readonly LanguageModelConfig config;
    readonly HttpClient httpClient;

    public string ProviderId { get; }
    public string ModelId { get; }
    public string ApiId { get; }

    public AnthropicLanguageModel(LanguageModelConfig config, string modelId, string providerId)
    {
        this.config = config;
        ModelId = modelId;
        ApiId = modelId;
        ProviderId = providerId;

        httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        httpClient.DefaultRequestHeaders.Add("User-Agent", "opencode-csharp/1.0");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "anthropic-beta",
            "interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14");

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
        var baseUrl = config.BaseUrl?.TrimEnd('/') ?? "https://api.anthropic.com/v1";
        var endpoint = $"{baseUrl}/messages";

        var body = BuildRequestBody(request);
        var json = JsonSerializer.Serialize(body, SerializerDefaults.JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        using var httpResp = await httpClient.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        httpResp.EnsureSuccessStatusCode();

        using var stream = await httpResp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..];

            AnthropicEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<AnthropicEvent>(data, SerializerDefaults.JsonOptions);
            }
            catch
            {
                continue;
            }

            if (evt == null) continue;

            switch (evt.Type)
            {
                case "content_block_start":
                    break;

                case "content_block_delta":
                    if (evt.Delta?.Type == "text_delta" && evt.Delta?.Text != null)
                    {
                        yield return new LLMStreamEvent("text", evt.Delta.Text, null, null, null, null);
                    }
                    else if (evt.Delta?.Type == "thinking_delta" && evt.Delta?.Thinking != null)
                    {
                        yield return new LLMStreamEvent("reasoning", null, null, evt.Delta.Thinking, null, null);
                    }
                    else if (evt.Delta?.Type == "input_json_delta" && evt.Delta?.PartialJson != null)
                    {
                        if (evt.Index is int index)
                        {
                            yield return new LLMStreamEvent(
                                "tool_call_delta", evt.Delta.PartialJson,
                                new LLMToolCall(index.ToString(), "", new Dictionary<string, object>()),
                                null, null, null);
                        }
                    }
                    break;

                case "content_block_stop":
                    break;

                case "message_delta":
                    if (evt.Delta?.StopReason == "tool_use")
                    {
                        yield break;
                    }
                    break;

                case "message_start":
                    if (evt.Message?.Usage != null)
                    {
                        yield return new LLMStreamEvent(
                            "usage", null, null, null,
                            new LLMUsage(
                                evt.Message.Usage.InputTokens,
                                evt.Message.Usage.OutputTokens,
                                null),
                            null);
                    }
                    break;

                case "message_stop":
                    yield break;

                case "error":
                    yield return new LLMStreamEvent(
                        "error", null, null, null, null,
                        evt.Error?.Message ?? "Unknown Anthropic error");
                    yield break;
            }
        }
    }

    object BuildRequestBody(LLMRequest request)
    {
        var messages = new List<object>();
        var system = new List<object>();

        foreach (var sys in request.System)
        {
            system.Add(new { type = "text", text = sys });
        }

        foreach (var msg in request.Messages)
        {
            if (msg.Role == "system")
            {
                system.Add(new { type = "text", text = msg.Content?.ToString() ?? "" });
            }
            else
            {
                messages.Add(new { role = msg.Role, content = msg.Content?.ToString() ?? "" });
            }
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = ModelId,
            ["max_tokens"] = 16384,
            ["stream"] = true,
            ["messages"] = messages,
        };

        if (system.Count > 0)
        {
            body["system"] = system;
        }

        if (request.Tools is { Length: > 0 })
        {
            body["tools"] = request.Tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = t.InputSchema
            }).ToArray();
        }

        return body;
    }
}

#region JSON DTOs

file class AnthropicEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("delta")]
    public AnthropicDelta? Delta { get; set; }

    [JsonPropertyName("message")]
    public AnthropicMessageStart? Message { get; set; }

    [JsonPropertyName("error")]
    public AnthropicError? Error { get; set; }
}

file class AnthropicDelta
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonPropertyName("partial_json")]
    public string? PartialJson { get; set; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }
}

file class AnthropicMessageStart
{
    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

file class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

file class AnthropicError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

#endregion
