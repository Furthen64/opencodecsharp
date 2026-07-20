using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OpenCode.Core;

public record ConfigInfo(
    [property: JsonPropertyName("$schema")] string? Schema,
    [property: JsonPropertyName("shell")] string? Shell,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("default_agent")] string? DefaultAgent,
    [property: JsonPropertyName("autoupdate")] object? AutoUpdate,
    [property: JsonPropertyName("share")] string? Share,
    [property: JsonPropertyName("enterprise")] ConfigEnterprise? Enterprise,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("permissions")] List<PermissionRule>? Permissions,
    [property: JsonPropertyName("agents")] Dictionary<string, ConfigAgentInfo>? Agents,
    [property: JsonPropertyName("snapshots")] bool? Snapshots,
    [property: JsonPropertyName("watcher")] ConfigWatcherInfo? Watcher,
    [property: JsonPropertyName("formatter")] ConfigFormatterInfo? Formatter,
    [property: JsonPropertyName("lsp")] ConfigLspInfo? Lsp,
    [property: JsonPropertyName("attachments")] ConfigAttachmentsInfo? Attachments,
    [property: JsonPropertyName("tool_output")] ConfigToolOutputInfo? ToolOutput,
    [property: JsonPropertyName("mcp")] ConfigMcpInfo? Mcp,
    [property: JsonPropertyName("compaction")] ConfigCompactionInfo? Compaction,
    [property: JsonPropertyName("skills")] List<string>? Skills,
    [property: JsonPropertyName("commands")] Dictionary<string, ConfigCommandInfo>? Commands,
    [property: JsonPropertyName("instructions")] List<string>? Instructions,
    [property: JsonPropertyName("references")] ConfigReferenceInfo? References,
    [property: JsonPropertyName("plugins")] List<ConfigPluginEntry>? Plugins,
    [property: JsonPropertyName("experimental")] ConfigExperimental? Experimental,
    [property: JsonPropertyName("providers")] Dictionary<string, ConfigProviderInfo>? Providers
);

public record ConfigEnterprise(string? Url);

public record PermissionRule(string Action, string Resource, string Effect);

public record ConfigAgentInfo(
    string? Model,
    string? Variant,
    object? Request,
    string? System,
    string? Description,
    string? Mode,
    bool? Hidden,
    string? Color,
    int? Steps,
    bool? Disabled,
    List<PermissionRule>? Permissions
);

public record ConfigWatcherInfo(bool? Enabled);

public record ConfigFormatterInfo(bool? Enabled);

public record ConfigLspInfo(bool? Enabled);

public record ConfigAttachmentsInfo(bool? Enabled);

public record ConfigToolOutputInfo(int? MaxLines, int? MaxBytes);

public record ConfigMcpInfo(bool? Enabled);

public record ConfigCompactionInfo(
    bool? Auto,
    bool? Prune,
    ConfigCompactionKeep? Keep,
    int? Buffer
);

public record ConfigCompactionKeep(int? Tokens);

public record ConfigCommandInfo(string? Description, string? Prompt);

public record ConfigReferenceInfo(
    List<ConfigReferenceEntry>? Entries
);

public record ConfigReferenceEntry(string Name, string Path);

public record ConfigPluginEntry(string Name, object? Options);

public record ConfigExperimental(
    List<object>? Policies
);

public record ConfigProviderInfo(
    string? ApiKey,
    string? Endpoint,
    object? Headers
);
