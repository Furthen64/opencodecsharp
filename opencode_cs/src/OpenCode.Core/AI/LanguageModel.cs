using System.Text.Json;

namespace OpenCode.Core.AI;

public static class SerializerDefaults
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

public interface ILanguageModel
{
    string ProviderId { get; }
    string ModelId { get; }
    string ApiId { get; }
    IAsyncEnumerable<LLMStreamEvent> StreamAsync(LLMRequest request, CancellationToken ct = default);
}

public record LanguageModelConfig(
    string ApiKey,
    string? BaseUrl,
    Dictionary<string, string>? Headers,
    Dictionary<string, object>? Settings
);
