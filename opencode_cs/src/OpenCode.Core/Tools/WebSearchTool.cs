using System.Text;
using System.Text.Json;

namespace OpenCode.Core;

public record WebSearchToolInput(
    string Query,
    int? NumResults,
    string? Livecrawl,
    string? Type,
    int? ContextMaxCharacters
);

public record WebSearchToolOutput(
    string Provider,
    string Text
);

public class WebSearchToolImpl : Tool
{
    readonly IPermissionService permission;
    readonly string exaApiKey;
    readonly string parallelApiKey;

    const string ExaUrl = "https://mcp.exa.ai/mcp";
    const string ParallelUrl = "https://search.parallel.ai/mcp";
    const int MaxNumResults = 20;
    const int MaxContextCharacters = 50_000;
    const int MaxResponseBytes = 256 * 1024;

    public WebSearchToolImpl(
        IPermissionService permission,
        string? exaApiKey = null,
        string? parallelApiKey = null)
        : base("websearch",
            $"Search the web using the session's local web search provider. Use this for current information beyond knowledge cutoff.\n\n" +
            $"Optional controls support result count, live crawling ('fallback' or 'preferred'), search type ('auto', 'fast', or 'deep'), and maximum context characters.\n\n" +
            $"The current year is {DateTime.Now.Year}. Use this year when searching for recent information or current events.")
    {
        this.permission = permission;
        this.exaApiKey = exaApiKey ?? "";
        this.parallelApiKey = parallelApiKey ?? "";
    }

    public override async Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
    {
        if (input is not WebSearchToolInput searchInput)
            throw new ToolFailure("Invalid input for websearch tool");

        var provider = SelectProvider(context.SessionId);

        await permission.AssertAsync(new PermissionAssertInput(
            Id: null,
            SessionId: context.SessionId,
            Action: "websearch",
            Resources: new[] { searchInput.Query },
            Save: new[] { "*" },
            Metadata: new Dictionary<string, object>
            {
                ["query"] = searchInput.Query,
                ["provider"] = provider
            },
            Source: $"tool:{context.AssistantMessageId}:{context.ToolCallId}",
                Agent: context.AgentId
        ));

        try
        {
            var text = provider == "exa"
                ? await CallExaAsync(searchInput, context.SessionId)
                : await CallParallelAsync(searchInput, context.SessionId);

            return new ToolOutput(
                new List<ToolOutputContent> { new ToolTextContent { Text = text ?? "No search results found. Please try a different query." } },
                new WebSearchToolOutput(provider, text ?? "No search results found. Please try a different query.")
            );
        }
        catch
        {
            throw new ToolFailure($"Unable to search the web for {searchInput.Query}");
        }
    }

    string SelectProvider(string sessionId)
    {
        if (!string.IsNullOrEmpty(exaApiKey)) return "exa";
        if (!string.IsNullOrEmpty(parallelApiKey)) return "parallel";
        var hash = Math.Abs(sessionId.GetHashCode());
        return hash % 2 == 0 ? "exa" : "parallel";
    }

    async Task<string?> CallExaAsync(WebSearchToolInput input, string sessionId)
    {
        using var client = new HttpClient();
        var url = ExaUrl;
        if (!string.IsNullOrEmpty(exaApiKey))
        {
            var uri = new Uri(ExaUrl);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            query["exaApiKey"] = exaApiKey;
            url = uri.GetLeftPart(UriPartial.Path) + "?" + query.ToString();
        }

        var args = new Dictionary<string, object>
        {
            ["query"] = input.Query,
            ["type"] = input.Type ?? "auto",
            ["numResults"] = input.NumResults ?? 8,
            ["livecrawl"] = input.Livecrawl ?? "fallback"
        };
        if (input.ContextMaxCharacters.HasValue)
            args["contextMaxCharacters"] = input.ContextMaxCharacters.Value;

        return await CallMcpAsync(client, url, "web_search_exa", args);
    }

    async Task<string?> CallParallelAsync(WebSearchToolInput input, string sessionId)
    {
        using var client = new HttpClient();
        var headers = new Dictionary<string, string> { ["User-Agent"] = "opencode/1.0" };
        if (!string.IsNullOrEmpty(parallelApiKey))
            headers["Authorization"] = $"Bearer {parallelApiKey}";

        var args = new Dictionary<string, object>
        {
            ["objective"] = input.Query,
            ["search_queries"] = new[] { input.Query },
            ["session_id"] = sessionId
        };

        return await CallMcpAsync(client, ParallelUrl, "web_search", args, headers);
    }

    async Task<string?> CallMcpAsync(
        HttpClient client,
        string url,
        string tool,
        Dictionary<string, object> toolArgs,
        Dictionary<string, string>? extraHeaders = null)
    {
        var mcpRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments = toolArgs }
        };

        var json = JsonSerializer.Serialize(mcpRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = content;
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (extraHeaders != null)
        {
            foreach (var (key, value) in extraHeaders)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await client.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cts.Token);
        return await ParseResponseAsync(body);
    }

    static async Task<string?> ParseResponseAsync(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length > 0)
        {
            var direct = await TryParsePayloadAsync(trimmed);
            if (direct != null) return direct;
        }

        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("data: ")) continue;
            var data = await TryParsePayloadAsync(line[6..]);
            if (data != null) return data;
        }
        return null;
    }

    static async Task<string?> TryParsePayloadAsync(string payload)
    {
        var trimmed = payload.Trim();
        if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var result) &&
                result.TryGetProperty("content", out var content) &&
                content.GetArrayLength() > 0)
            {
                var first = content[0];
                if (first.TryGetProperty("text", out var text))
                    return text.GetString();
            }
        }
        catch { }
        return null;
    }

    public override ToolDefinition ToDefinition(string name)
    {
        return new ToolDefinition(
            name,
            Description,
            new
            {
                query = "(string) Websearch query",
                numResults = "(int?) Number of results",
                livecrawl = "(string?) Live crawl mode",
                type = "(string?) Search type",
                contextMaxCharacters = "(int?) Max context characters"
            },
            null
        );
    }
}
