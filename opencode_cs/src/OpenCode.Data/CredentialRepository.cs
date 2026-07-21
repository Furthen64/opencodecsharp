using OpenCode.Schema;

namespace OpenCode.Data;

public record CredentialRow(
    string Id,
    string? IntegrationId,
    string Label,
    string Value,
    string? ConnectorId,
    string? MethodId,
    bool? Active,
    long TimeCreated,
    long TimeUpdated
);

public class CredentialRepository
{
    readonly Database db;
    public CredentialRepository(Database db) => this.db = db;

    public async Task<List<CredentialRow>> ListAllAsync()
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<CredentialRow>(
            "SELECT * FROM \"credential\" ORDER BY \"time_created\" DESC");
        return rows.ToList();
    }

    public async Task<List<CredentialRow>> ListByIntegrationAsync(string integrationId)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<CredentialRow>(
            "SELECT * FROM \"credential\" WHERE \"integration_id\" = @IntegrationId ORDER BY \"time_created\" DESC",
            new { IntegrationId = integrationId });
        return rows.ToList();
    }

    public async Task<CredentialRow?> GetAsync(string id)
    {
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<CredentialRow?>(
            "SELECT * FROM \"credential\" WHERE \"id\" = @Id",
            new { Id = id });
    }

    public async Task UpsertAsync(
        string id,
        string? integrationId,
        string label,
        string value,
        string? connectorId,
        string? methodId,
        bool? active)
    {
        using var conn = db.CreateConnection();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await conn.ExecuteAsync(@"
            INSERT INTO ""credential"" (
                ""id"", ""integration_id"", ""label"", ""value"",
                ""connector_id"", ""method_id"", ""active"",
                ""time_created"", ""time_updated""
            ) VALUES (
                @Id, @IntegrationId, @Label, @Value,
                @ConnectorId, @MethodId, @Active,
                @TimeCreated, @TimeUpdated
            )
            ON CONFLICT(""id"") DO UPDATE SET
                ""integration_id"" = @IntegrationId,
                ""label"" = @Label,
                ""value"" = @Value,
                ""connector_id"" = @ConnectorId,
                ""method_id"" = @MethodId,
                ""active"" = @Active,
                ""time_updated"" = @TimeUpdated",
            new
            {
                Id = id,
                IntegrationId = integrationId,
                Label = label,
                Value = value,
                ConnectorId = connectorId,
                MethodId = methodId,
                Active = active.HasValue ? (active.Value ? 1 : 0) : (int?)null,
                TimeCreated = now,
                TimeUpdated = now,
            });
    }

    public async Task UpdateAsync(string id, string label, string value)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE ""credential"" SET
                ""label"" = @Label,
                ""value"" = @Value,
                ""time_updated"" = @TimeUpdated
            WHERE ""id"" = @Id",
            new
            {
                Id = id,
                Label = label,
                Value = value,
                TimeUpdated = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
    }

    public async Task DeleteAsync(string id)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM \"credential\" WHERE \"id\" = @Id",
            new { Id = id });
    }
}
