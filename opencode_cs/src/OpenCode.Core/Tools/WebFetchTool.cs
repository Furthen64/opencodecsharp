using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OpenCode.Core;

public record WebFetchToolInput(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("timeout")] int? Timeout
);

public record WebFetchToolOutput(
    [property: JsonPropertyName("url")] string FetchUrl,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("output")] string OutputText
);

public class WebFetchTool : Tool
{
    const int MaxResponseBytes = 5 * 1024 * 1024;
    const int DefaultTimeoutSeconds = 30;
    const int MaxTimeoutSeconds = 120;

    static readonly string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36";

    readonly HttpClient httpClient;

    public WebFetchTool()
        : base("webfetch", "Fetch content from an HTTP or HTTPS URL and return it as text, markdown, or HTML. Markdown is the default. This tool is read-only.")
    {
        httpClient = new HttpClient();
    }

    public override async Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
    {
        if (input is not WebFetchToolInput fetchInput)
            throw new ToolFailure("Invalid input for webfetch tool");

        if (!Uri.TryCreate(fetchInput.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ToolFailure("URL must use http:// or https://");

        var format = string.IsNullOrEmpty(fetchInput.Format) ? "markdown" : fetchInput.Format;
        if (format != "text" && format != "markdown" && format != "html")
            format = "markdown";

        var timeout = fetchInput.Timeout ?? DefaultTimeoutSeconds;
        if (timeout > MaxTimeoutSeconds)
            timeout = MaxTimeoutSeconds;

        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        var acceptHeader = format switch
        {
            "markdown" => "text/markdown;q=1.0, text/x-markdown;q=0.9, text/plain;q=0.8, text/html;q=0.7, */*;q=0.1",
            "text" => "text/plain;q=1.0, text/markdown;q=0.9, text/html;q=0.8, */*;q=0.1",
            "html" => "text/html;q=1.0, application/xhtml+xml;q=0.9, text/plain;q=0.8, text/markdown;q=0.7, */*;q=0.1",
            _ => "*/*"
        };
        httpClient.DefaultRequestHeaders.Accept.Clear();
        httpClient.DefaultRequestHeaders.Accept.ParseAdd(acceptHeader);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            var response = await httpClient.GetAsync(fetchInput.Url, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    httpClient.DefaultRequestHeaders.UserAgent.Clear();
                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("opencode");
                    response = await httpClient.GetAsync(fetchInput.Url, cts.Token).ConfigureAwait(false);
                }

                if (!response.IsSuccessStatusCode)
                    throw new ToolFailure($"HTTP error: {(int)response.StatusCode} {response.StatusCode}");
            }

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
            var mime = contentType.Split(';', 1)[0].Trim().ToLowerInvariant();

            if (mime.StartsWith("image/") && mime != "image/svg+xml")
                throw new ToolFailure($"Unsupported fetched image content type: {mime}");

            if (!IsTextualMime(mime))
                throw new ToolFailure($"Unsupported fetched file content type: {mime}");

            var contentBytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);

            if (contentBytes.Length > MaxResponseBytes)
                throw new ToolFailure($"Response too large (exceeds {MaxResponseBytes} byte limit)");

            var content = System.Text.Encoding.UTF8.GetString(contentBytes);
            var output = ConvertContent(content, contentType, format);

            var result = new WebFetchToolOutput(fetchInput.Url, contentType, format, output);

            return new ToolOutput(
                new List<ToolOutputContent> { new ToolTextContent { Text = output } },
                result
            );
        }
        catch (TaskCanceledException)
        {
            throw new ToolFailure("Request timed out");
        }
    }

    public override ToolDefinition ToDefinition(string name)
    {
        return new ToolDefinition(
            name,
            Description,
            new { url = "(string) HTTP/HTTPS URL", format = "(string) Output format: text, markdown, html", timeout = "(int?) Timeout in seconds" },
            null
        );
    }

    static bool IsTextualMime(string mime)
    {
        return string.IsNullOrEmpty(mime)
            || mime.StartsWith("text/")
            || mime == "application/json"
            || mime.EndsWith("+json")
            || mime == "application/xml"
            || mime.EndsWith("+xml")
            || mime == "application/javascript"
            || mime == "application/x-javascript";
    }

    static string ConvertContent(string content, string contentType, string format)
    {
        if (!contentType.Contains("text/html"))
            return content;

        if (format == "markdown")
            return ConvertHtmlToMarkdown(content);
        if (format == "text")
            return ExtractTextFromHtml(content);

        return content;
    }

    static string ExtractTextFromHtml(string html)
    {
        var result = new System.Text.StringBuilder();
        var skipTags = new HashSet<string> { "script", "style", "noscript", "iframe", "object", "embed" };
        var skipDepth = 0;

        var tagRegex = new Regex(@"<(\/?)(\w+)[^>]*>\s*", RegexOptions.Multiline);
        var lastEnd = 0;

        foreach (Match match in tagRegex.Matches(html))
        {
            if (match.Index > lastEnd && skipDepth == 0)
                result.Append(html.Substring(lastEnd, match.Index - lastEnd));

            var isClosing = match.Groups[1].Value == "/";
            var tagName = match.Groups[2].Value.ToLowerInvariant();

            if (isClosing)
            {
                if (skipDepth > 0)
                    skipDepth--;
            }
            else if (skipTags.Contains(tagName))
            {
                skipDepth++;
            }

            lastEnd = match.Index + match.Length;
        }

        if (lastEnd < html.Length && skipDepth == 0)
            result.Append(html.Substring(lastEnd));

        return result.ToString().Trim();
    }

    static string ConvertHtmlToMarkdown(string html)
    {
        var text = ExtractTextFromHtml(html);

        text = Regex.Replace(text, @"<(h[1-6])[^>]*>(.*?)<\/\1>", (m) =>
        {
            var level = int.Parse(m.Groups[1].Value.Substring(1));
            var prefix = new string('#', level);
            return $"\n\n{prefix} {m.Groups[2].Value}\n\n";
        }, RegexOptions.Multiline | RegexOptions.Singleline);

        text = Regex.Replace(text, @"<(?:b|strong)[^>]*>(.*?)<\/(?:b|strong)>", "**$1**", RegexOptions.Multiline | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<(?:i|em)[^>]*>(.*?)<\/(?:i|em)>", "*$1*", RegexOptions.Multiline | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<a[^>]*href=[""']([^""']*)[""'][^>]*>(.*?)<\/a>", "[$2]($1)", RegexOptions.Multiline | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<code[^>]*>(.*?)<\/code>", "`$1`", RegexOptions.Multiline | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<pre[^>]*>(.*?)<\/pre>", "\n\n```\n$1\n```\n\n", RegexOptions.Multiline | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.Multiline);
        text = Regex.Replace(text, @"<p[^>]*>(.*?)<\/p>", "\n\n$1\n\n", RegexOptions.Multiline | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<blockquote[^>]*>(.*?)<\/blockquote>", "\n\n> $1\n\n", RegexOptions.Multiline | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<hr\s*/?>", "\n\n---\n\n", RegexOptions.Multiline);

        text = Regex.Replace(text, @"<li[^>]*>(.*?)<\/li>", "- $1\n", RegexOptions.Multiline | RegexOptions.Singleline);

        text = Regex.Replace(text, @"<[^>]+>", "");

        text = text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")
            .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&nbsp;", " ");

        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

        return text;
    }
}
