using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public static class ProviderIds
{
    public const string OpenCode = "opencode";
    public const string Anthropic = "anthropic";
    public const string OpenAI = "openai";
    public const string Google = "google";
    public const string GoogleVertex = "google-vertex";
    public const string GithubCopilot = "github-copilot";
    public const string AmazonBedrock = "amazon-bedrock";
    public const string Azure = "azure";
    public const string OpenRouter = "openrouter";
    public const string Mistral = "mistral";
    public const string GitLab = "gitlab";
}

[JsonDerivedType(typeof(ProviderAISDK), typeDiscriminator: "aisdk")]
[JsonDerivedType(typeof(ProviderNative), typeDiscriminator: "native")]
public abstract record ProviderApi;

public record ProviderAISDK(
    string Type,
    string Package,
    string? Url,
    Dictionary<string, object>? Settings
) : ProviderApi;

public record ProviderNative(
    string Type,
    string? Url,
    Dictionary<string, object> Settings
) : ProviderApi;

public record ProviderRequest(
    Dictionary<string, string> Headers,
    Dictionary<string, object> Body
);

public record ProviderInfo(
    string Id,
    string? IntegrationId,
    string Name,
    bool? Disabled,
    ProviderApi Api,
    ProviderRequest Request
);
