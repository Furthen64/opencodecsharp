using System.Text.Json;
using OpenCode.Schema;

namespace OpenCode.Data;

public class SessionRepository
{
    private const string SelectColumns = @"
        SELECT
            ""id"" AS Id,
            ""project_id"" AS ProjectId,
            ""workspace_id"" AS WorkspaceId,
            ""parent_id"" AS ParentId,
            ""directory"" AS Directory,
            ""path"" AS Path,
            ""title"" AS Title,
            ""cost"" AS Cost,
            ""tokens_input"" AS TokensInput,
            ""tokens_output"" AS TokensOutput,
            ""tokens_reasoning"" AS TokensReasoning,
            ""tokens_cache_read"" AS TokensCacheRead,
            ""tokens_cache_write"" AS TokensCacheWrite,
            ""revert"" AS Revert,
            ""agent"" AS Agent,
            ""model"" AS Model,
            ""time_created"" AS TimeCreated,
            ""time_updated"" AS TimeUpdated,
            ""time_archived"" AS TimeArchived
        FROM ""session""";

    readonly Database db;
    public SessionRepository(Database db) => this.db = db;

    public async Task<SessionInfo?> GetAsync(string id)
    {
        using var conn = db.CreateConnection();
        var parameters = new DynamicParameters();
        parameters.Add("Id", id);
        var row = await conn.QuerySingleOrDefaultAsync<SessionRow>(
            SelectColumns + " WHERE \"id\" = @Id",
            parameters);
        return row is null ? null : MapFromRow(row);
    }

    public async Task<List<SessionInfo>> ListAllAsync()
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<SessionRow>(SelectColumns + " ORDER BY \"time_created\" DESC, \"id\" DESC");
        return rows.Select(MapFromRow).ToList();
    }

    public async Task<List<SessionInfo>> ListAsync(
        string? projectId,
        string? directory,
        string? titleFilter,
        int limit,
        long? cursor)
    {
        using var conn = db.CreateConnection();

        var sql = SelectColumns + " WHERE 1=1";
        var p = new DynamicParameters();

        if (!string.IsNullOrEmpty(projectId))
        {
            sql += " AND \"project_id\" = @ProjectId";
            p.Add("ProjectId", projectId);
        }

        if (!string.IsNullOrEmpty(directory))
        {
            sql += " AND \"directory\" = @Directory";
            p.Add("Directory", directory);
        }

        if (!string.IsNullOrEmpty(titleFilter))
        {
            sql += " AND \"title\" LIKE @TitleFilter";
            p.Add("TitleFilter", $"%{titleFilter}%");
        }

        if (cursor.HasValue)
        {
            sql += " AND \"time_created\" < @Cursor";
            p.Add("Cursor", cursor);
        }

        sql += " ORDER BY \"time_created\" DESC LIMIT @Limit";
        p.Add("Limit", limit);

        var rows = await conn.QueryAsync<SessionRow>(sql, p);
        return rows.Select(MapFromRow).ToList();
    }

    public async Task InsertAsync(SessionInfo session)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO ""session"" (
                ""id"", ""project_id"", ""workspace_id"", ""parent_id"",
                ""slug"", ""directory"", ""path"", ""title"", ""version"",
                ""share_url"", ""cost"",
                ""tokens_input"", ""tokens_output"", ""tokens_reasoning"",
                ""tokens_cache_read"", ""tokens_cache_write"",
                ""revert"", ""permission"", ""agent"", ""model"",
                ""time_created"", ""time_updated"", ""time_compacting"", ""time_archived""
            ) VALUES (
                @Id, @ProjectId, @WorkspaceId, @ParentId,
                @Slug, @Directory, @Path, @Title, @Version,
                @ShareUrl, @Cost,
                @TokensInput, @TokensOutput, @TokensReasoning,
                @TokensCacheRead, @TokensCacheWrite,
                @Revert, @Permission, @Agent, @Model,
                @TimeCreated, @TimeUpdated, @TimeCompacting, @TimeArchived
            )
            ON CONFLICT(""id"") DO UPDATE SET
                ""project_id"" = excluded.""project_id"",
                ""workspace_id"" = excluded.""workspace_id"",
                ""parent_id"" = excluded.""parent_id"",
                ""directory"" = excluded.""directory"",
                ""path"" = excluded.""path"",
                ""title"" = excluded.""title"",
                ""cost"" = excluded.""cost"",
                ""tokens_input"" = excluded.""tokens_input"",
                ""tokens_output"" = excluded.""tokens_output"",
                ""tokens_reasoning"" = excluded.""tokens_reasoning"",
                ""tokens_cache_read"" = excluded.""tokens_cache_read"",
                ""tokens_cache_write"" = excluded.""tokens_cache_write"",
                ""revert"" = excluded.""revert"",
                ""agent"" = excluded.""agent"",
                ""model"" = excluded.""model"",
                ""time_created"" = excluded.""time_created"",
                ""time_updated"" = excluded.""time_updated"",
                ""time_archived"" = excluded.""time_archived""",
            MapToParams(session));
    }

    public async Task UpdateAsync(SessionInfo session)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE ""session"" SET
                ""project_id"" = @ProjectId,
                ""workspace_id"" = @WorkspaceId,
                ""parent_id"" = @ParentId,
                ""slug"" = @Slug,
                ""directory"" = @Directory,
                ""path"" = @Path,
                ""title"" = @Title,
                ""version"" = @Version,
                ""share_url"" = @ShareUrl,
                ""cost"" = @Cost,
                ""tokens_input"" = @TokensInput,
                ""tokens_output"" = @TokensOutput,
                ""tokens_reasoning"" = @TokensReasoning,
                ""tokens_cache_read"" = @TokensCacheRead,
                ""tokens_cache_write"" = @TokensCacheWrite,
                ""revert"" = @Revert,
                ""permission"" = @Permission,
                ""agent"" = @Agent,
                ""model"" = @Model,
                ""time_created"" = @TimeCreated,
                ""time_updated"" = @TimeUpdated,
                ""time_compacting"" = @TimeCompacting,
                ""time_archived"" = @TimeArchived
            WHERE ""id"" = @Id",
            MapToParams(session));
    }

    public async Task DeleteAsync(string id)
    {
        using var conn = db.CreateConnection();
        var parameters = new DynamicParameters();
        parameters.Add("Id", id);
        await conn.ExecuteAsync(
            "DELETE FROM \"session\" WHERE \"id\" = @Id",
            parameters);
    }

    public async Task UpdateUsageAsync(string id, double costDelta, int inputTokens, int outputTokens, int reasoningTokens)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE ""session"" SET
                ""cost"" = ""cost"" + @CostDelta,
                ""tokens_input"" = ""tokens_input"" + @InputTokens,
                ""tokens_output"" = ""tokens_output"" + @OutputTokens,
                ""tokens_reasoning"" = ""tokens_reasoning"" + @ReasoningTokens,
                ""time_updated"" = @TimeUpdated
            WHERE ""id"" = @Id",
            new
            {
                Id = id,
                CostDelta = costDelta,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ReasoningTokens = reasoningTokens,
                TimeUpdated = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
    }

    public async Task UpdateAgentAsync(string id, string? agent)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE ""session"" SET
                ""agent"" = @Agent,
                ""time_updated"" = @TimeUpdated
            WHERE ""id"" = @Id",
            new
            {
                Id = id,
                Agent = agent,
                TimeUpdated = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
    }

    public async Task UpdateModelAsync(string id, string? modelJson)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE ""session"" SET
                ""model"" = @Model,
                ""time_updated"" = @TimeUpdated
            WHERE ""id"" = @Id",
            new
            {
                Id = id,
                Model = modelJson,
                TimeUpdated = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
    }

    public async Task ArchiveAsync(string id)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE ""session"" SET
                ""time_archived"" = @TimeArchived,
                ""time_updated"" = @TimeUpdated
            WHERE ""id"" = @Id",
            new
            {
                Id = id,
                TimeArchived = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                TimeUpdated = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
    }

    public async Task<List<SessionInfo>> SearchAsync(string query, int limit)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<SessionRow>(
            SelectColumns + " WHERE \"title\" LIKE @Query ORDER BY \"time_created\" DESC LIMIT @Limit",
            new { Query = $"%{query}%", Limit = limit });
        return rows.Select(MapFromRow).ToList();
    }

    private static SessionInfo MapFromRow(SessionRow row) => new(
        row.Id,
        row.ParentId,
        row.ProjectId,
        row.Agent,
        Deserialize<OpenCode.Schema.ModelRef>(row.Model),
        row.Cost,
        new SessionTokens(
            row.TokensInput,
            row.TokensOutput,
            row.TokensReasoning,
            new SessionCacheTokens(row.TokensCacheRead, row.TokensCacheWrite)),
        new SessionTime(row.TimeCreated, row.TimeUpdated, row.TimeArchived),
        row.Title,
        new LocationRef(row.Directory, row.WorkspaceId),
        row.Path,
        Deserialize<RevertState>(row.Revert));

    private static T? Deserialize<T>(string? json) => string.IsNullOrWhiteSpace(json)
        ? default
        : JsonSerializer.Deserialize<T>(json);

    private static DynamicParameters MapToParams(SessionInfo session)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", session.Id);
        parameters.Add("ProjectId", session.ProjectId);
        parameters.Add("WorkspaceId", session.Location.WorkspaceId);
        parameters.Add("ParentId", session.ParentId);
        parameters.Add("Slug", session.Title);
        parameters.Add("Directory", session.Location.Directory);
        parameters.Add("Path", session.Subpath);
        parameters.Add("Title", session.Title);
        parameters.Add("Version", "");
        parameters.Add("ShareUrl", null);
        parameters.Add("Cost", session.Cost);
        parameters.Add("TokensInput", (long)session.Tokens.Input);
        parameters.Add("TokensOutput", (long)session.Tokens.Output);
        parameters.Add("TokensReasoning", (long)session.Tokens.Reasoning);
        parameters.Add("TokensCacheRead", (long)session.Tokens.Cache.Read);
        parameters.Add("TokensCacheWrite", (long)session.Tokens.Cache.Write);
        parameters.Add("Revert", session.Revert != null ? JsonSerializer.Serialize(session.Revert) : null);
        parameters.Add("Permission", null);
        parameters.Add("Agent", session.Agent);
        parameters.Add("Model", session.Model != null ? JsonSerializer.Serialize(session.Model) : null);
        parameters.Add("TimeCreated", session.Time.Created);
        parameters.Add("TimeUpdated", session.Time.Updated);
        parameters.Add("TimeCompacting", null);
        parameters.Add("TimeArchived", session.Time.Archived);
        return parameters;
    }

    private sealed record SessionRow(
        string Id,
        string ProjectId,
        string? WorkspaceId,
        string? ParentId,
        string Directory,
        string? Path,
        string Title,
        double Cost,
        long TokensInput,
        long TokensOutput,
        long TokensReasoning,
        long TokensCacheRead,
        long TokensCacheWrite,
        string? Revert,
        string? Agent,
        string? Model,
        long TimeCreated,
        long TimeUpdated,
        long? TimeArchived);
}
