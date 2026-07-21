using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCode.Core.AI.Providers;

public class OpenAIProviderPlugin : IProviderPlugin
{
    public string Id => "openai";

    public const string PackageAISDK = "@ai-sdk/openai";

    public Task<ProviderSdkResult?> CreateSdkAsync(ProviderSdkEvent evt)
    {
        if (evt.Package != PackageAISDK)
            return Task.FromResult<ProviderSdkResult?>(null);

        var config = ResolveConfig(evt.Options);
        var sdk = new OpenAISdk(config);
        return Task.FromResult<ProviderSdkResult?>(new ProviderSdkResult(sdk));
    }

    public Task<ProviderLanguageResult?> CreateLanguageAsync(ProviderLanguageEvent evt)
    {
        if (evt.Model.ProviderId != Schema.ProviderIds.OpenAI)
            return Task.FromResult<ProviderLanguageResult?>(null);

        if (evt.Sdk is not OpenAISdk sdk)
            return Task.FromResult<ProviderLanguageResult?>(null);

        if (evt.Model.Api is not Schema.ModelApiAISDK aisdk)
            return Task.FromResult<ProviderLanguageResult?>(null);

        var model = new OpenAILanguageModel(
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
            apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";

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

public class OpenAISdk
{
    public LanguageModelConfig Config { get; }
    public OpenAISdk(LanguageModelConfig config) => Config = config;
}

public class OpenAILanguageModel : ILanguageModel
{
    protected readonly LanguageModelConfig config;
    readonly HttpClient httpClient;

    public string ProviderId { get; }
    public string ModelId { get; }
    public string ApiId { get; }

    public OpenAILanguageModel(LanguageModelConfig config, string modelId, string providerId)
    {
        this.config = config;
        ModelId = modelId;
        ApiId = modelId;
        ProviderId = providerId;

        httpClient = new HttpClient();
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
        httpClient.DefaultRequestHeaders.Add("User-Agent", "opencode-csharp/1.0");

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
        var baseUrl = config.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        var endpoint = $"{baseUrl}/chat/completions";

        var body = BuildRequestBody(request);
        var json = JsonSerializer.Serialize(body, SerializerDefaults.JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        using var httpResp = await httpClient.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        httpResp.EnsureSuccessStatusCode();

        using var stream = await httpResp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var pendingToolCalls = new Dictionary<int, PendingToolCall>();

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (data == "[DONE]") break;

            ChatCompletionChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(data, SerializerDefaults.JsonOptions);
            }
            catch
            {
                continue;
            }

            if (chunk == null) continue;

            foreach (var choice in chunk.Choices)
            {
                if (choice.Delta?.Content != null)
                {
                    yield return new LLMStreamEvent("text", choice.Delta.Content, null, null, null, null);
                }

                if (choice.Delta?.ToolCalls != null)
                {
                    foreach (var tc in choice.Delta.ToolCalls)
                    {
                        if (!pendingToolCalls.TryGetValue(tc.Index, out var pending))
                            pendingToolCalls[tc.Index] = pending = new PendingToolCall(tc.Index);
                        pending.Id ??= tc.Id;
                        pending.Name ??= tc.Function?.Name;
                        if (tc.Function?.Arguments is not null)
                            pending.Arguments.Append(tc.Function.Arguments);
                    }
                }

                if (choice.FinishReason == "tool_calls")
                {
                    foreach (var pending in pendingToolCalls.Values)
                    {
                        if (string.IsNullOrWhiteSpace(pending.Name))
                            continue;
                        var input = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            pending.Arguments.ToString(), SerializerDefaults.JsonOptions)
                            ?? new Dictionary<string, object>();
                        yield return new LLMStreamEvent(
                            "tool_call", null,
                            new LLMToolCall(pending.Id ?? $"call_{pending.Index}", pending.Name, input),
                            null, null, null);
                    }
                    yield break;
                }
            }

            if (chunk.Usage != null)
            {
                yield return new LLMStreamEvent(
                    "usage", null, null, null,
                    new LLMUsage(
                        chunk.Usage.PromptTokens,
                        chunk.Usage.CompletionTokens,
                        null),
                    null);
            }
        }
    }

    protected virtual object BuildRequestBody(LLMRequest request)
    {
        var messages = new List<object>();

        foreach (var sys in request.System)
        {
            messages.Add(new { role = "system", content = sys });
        }

        foreach (var msg in request.Messages)
        {
            messages.Add(new { role = msg.Role, content = msg.Content });
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = ModelId,
            ["stream"] = true,
            ["messages"] = messages,
        };

        if (request.Tools is { Length: > 0 })
        {
            body["tools"] = request.Tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.InputSchema
                }
            }).ToArray();
        }

        if (request.ToolChoice != null)
        {
            body["tool_choice"] = request.ToolChoice;
        }

        return body;
    }
}

#region JSON DTOs

file class ChatCompletionChunk
{
    [JsonPropertyName("choices")]
    public List<ChunkChoice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public ChunkUsage? Usage { get; set; }
}

file class ChunkChoice
{
    [JsonPropertyName("delta")]
    public ChunkDelta? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

file class ChunkDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<ChunkToolCall>? ToolCalls { get; set; }
}

file class ChunkToolCall
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("function")]
    public ChunkFunction? Function { get; set; }
}

file class ChunkFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

file class PendingToolCall
{
    public int Index { get; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public StringBuilder Arguments { get; } = new();

    public PendingToolCall(int index) => Index = index;
}

file class ChunkUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }
}

#endregion
