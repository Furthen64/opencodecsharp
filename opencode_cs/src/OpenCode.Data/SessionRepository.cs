using System.Text.Json;
using OpenCode.Schema;

namespace OpenCode.Data;

public class SessionRepository
{
    readonly Database db;
    public SessionRepository(Database db) => this.db = db;

    public async Task<SessionInfo?> GetAsync(string id)
    {
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<SessionInfo?>(
            "SELECT * FROM \"session\" WHERE \"id\" = @Id",
            new { Id = id });
    }

    public async Task<List<SessionInfo>> ListAsync(
        string? projectId,
        string? directory,
        string? titleFilter,
        int limit,
        string? cursor)
    {
        using var conn = db.CreateConnection();

        var sql = "SELECT * FROM \"session\" WHERE 1=1";
        var p = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(projectId))
        {
            sql += " AND \"project_id\" = @ProjectId";
            p["ProjectId"] = projectId;
        }

        if (!string.IsNullOrEmpty(directory))
        {
            sql += " AND \"directory\" = @Directory";
            p["Directory"] = directory;
        }

        if (!string.IsNullOrEmpty(titleFilter))
        {
            sql += " AND \"title\" LIKE @TitleFilter";
            p["TitleFilter"] = $"%{titleFilter}%";
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            sql += " AND \"time_created\" < @Cursor";
            p["Cursor"] = cursor;
        }

        sql += " ORDER BY \"time_created\" DESC LIMIT @Limit";
        p["Limit"] = limit;

        var rows = await conn.QueryAsync<SessionInfo>(sql, p);
        return rows.ToList();
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
            )",
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
        await conn.ExecuteAsync(
            "DELETE FROM \"session\" WHERE \"id\" = @Id",
            new { Id = id });
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
        var rows = await conn.QueryAsync<SessionInfo>(
            "SELECT * FROM \"session\" WHERE \"title\" LIKE @Query ORDER BY \"time_created\" DESC LIMIT @Limit",
            new { Query = $"%{query}%", Limit = limit });
        return rows.ToList();
    }

    private static object MapToParams(SessionInfo session) => new
    {
        session.Id,
        session.ProjectId,
        WorkspaceId = (session.Location as LocationRef)?.WorkspaceId,
        session.ParentId,
        Slug = session.Title,
        Directory = (session.Location as LocationRef)?.Directory ?? "",
        Path = session.Subpath,
        session.Title,
        Version = "",
        ShareUrl = (string?)null,
        session.Cost,
        TokensInput = (int)session.Tokens.Input,
        TokensOutput = (int)session.Tokens.Output,
        TokensReasoning = (int)session.Tokens.Reasoning,
        TokensCacheRead = (int)session.Tokens.Cache.Read,
        TokensCacheWrite = (int)session.Tokens.Cache.Write,
        Revert = session.Revert != null ? JsonSerializer.Serialize(session.Revert) : null,
        Permission = (string?)null,
        session.Agent,
        Model = session.Model != null ? JsonSerializer.Serialize(session.Model) : null,
        session.Time.Created,
        session.Time.Updated,
        TimeCompacting = (long?)null,
        session.Time.Archived,
    };
}
