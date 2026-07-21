using OpenCode.Schema;

namespace OpenCode.Data;

public record PermissionRow(
    string Id,
    string ProjectId,
    string Action,
    string Resource,
    long TimeCreated,
    long TimeUpdated
);

public class PermissionRepository
{
    readonly Database db;
    public PermissionRepository(Database db) => this.db = db;

    public async Task<List<PermissionRow>> ListByProjectAsync(string projectId)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<PermissionRow>(
            "SELECT * FROM \"permission\" WHERE \"project_id\" = @ProjectId ORDER BY \"time_created\" DESC",
            new { ProjectId = projectId });
        return rows.ToList();
    }

    public async Task AddAsync(string id, string projectId, string action, string resource)
    {
        using var conn = db.CreateConnection();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await conn.ExecuteAsync(@"
            INSERT INTO ""permission"" (
                ""id"", ""project_id"", ""action"", ""resource"",
                ""time_created"", ""time_updated""
            ) VALUES (
                @Id, @ProjectId, @Action, @Resource,
                @TimeCreated, @TimeUpdated
            )",
            new
            {
                Id = id,
                ProjectId = projectId,
                Action = action,
                Resource = resource,
                TimeCreated = now,
                TimeUpdated = now,
            });
    }

    public async Task RemoveAsync(string id)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM \"permission\" WHERE \"id\" = @Id",
            new { Id = id });
    }
}
