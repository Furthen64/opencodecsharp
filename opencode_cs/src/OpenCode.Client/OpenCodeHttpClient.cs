using System.Net.Http.Json;
using System.Text.Json;
using OpenCode.Protocol;
using Schema = OpenCode.Schema;

namespace OpenCode.Client;

public sealed class OpenCodeHttpClient : IDisposable
{
    private readonly HttpClient http;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public OpenCodeHttpClient(string serverUrl)
    {
        http = new HttpClient { BaseAddress = new Uri(EnsureTrailingSlash(serverUrl)) };
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync("global/health", cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            var health = await response.Content.ReadFromJsonAsync<HealthResponse>(JsonOptions, cancellationToken);
            return health?.Healthy == true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<Schema.SessionInfo> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("session", new { }, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Schema.SessionInfo>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Server returned an empty session response.");
    }

    public async Task PromptAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        var request = new SessionPromptRequest(null, new Schema.PromptInput(text, null), null, null);
        using var response = await http.PostAsJsonAsync($"session/{Uri.EscapeDataString(sessionId)}/message", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<JsonElement> MessagesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"session/{Uri.EscapeDataString(sessionId)}/message?order=asc", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    public void Dispose() => http.Dispose();

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : $"{url}/";
}
