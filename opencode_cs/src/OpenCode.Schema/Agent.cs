using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public enum AgentMode
{
    Subagent,
    Primary,
    All
}

public record AgentInfo(
    string Id,
    ModelRef? Model,
    ProviderRequest Request,
    string? System,
    string? Description,
    AgentMode Mode,
    bool Hidden,
    string? Color,
    int? Steps,
    PermissionRule[] Permissions
);
