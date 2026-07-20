using System.Text.Json.Serialization;

namespace OpenCode.Schema;

public static class PermissionSaved
{
    public record Info(
        string Id,
        string ProjectID,
        string Action,
        string Resource
    );
}
