using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public enum PermissionEffect
{
    Allow,
    Deny,
    Ask
}

public record PermissionRule(
    string Action,
    string Resource,
    PermissionEffect Effect
);

public record PermissionSource(
    string Type,
    string MessageId,
    string CallId
);

public record PermissionRequest(
    string Id,
    string SessionId,
    string Action,
    string[] Resources,
    string[]? Save,
    Dictionary<string, object>? Metadata,
    PermissionSource? Source
);

public enum PermissionReply
{
    Once,
    Always,
    Reject
}
