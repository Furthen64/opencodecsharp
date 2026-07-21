using OpenCode.Schema;

namespace OpenCode.Data;

public class MessageRepository
{
    private const string SelectColumns = @"
        SELECT
            ""id"" AS Id,
            ""session_id"" AS SessionId,
            ""type"" AS Type,
            ""seq"" AS Seq,
            ""time_created"" AS TimeCreated,
            ""time_updated"" AS TimeUpdated,
            ""data"" AS Data
        FROM ""session_message""";

    readonly Database db;
    public MessageRepository(Database db) => this.db = db;

    public async Task<List<SessionMessageInfo>> GetBySessionAsync(string sessionId, int? afterSeq = null)
    {
        using var conn = db.CreateConnection();

        if (afterSeq.HasValue)
        {
            var parameters = Parameters(("SessionId", sessionId), ("AfterSeq", afterSeq.Value));
            var rows = await conn.QueryAsync<SessionMessageInfo>(
                SelectColumns + " WHERE \"session_id\" = @SessionId AND \"seq\" > @AfterSeq ORDER BY \"seq\" ASC",
                parameters);
            return rows.ToList();
        }
        else
        {
            var parameters = Parameters(("SessionId", sessionId));
            var rows = await conn.QueryAsync<SessionMessageInfo>(
                SelectColumns + " WHERE \"session_id\" = @SessionId ORDER BY \"seq\" ASC",
                parameters);
            return rows.ToList();
        }
    }

    public async Task UpsertAsync(SessionMessageInfo msg)
    {
        using var conn = db.CreateConnection();
        var parameters = Parameters(
            ("Id", msg.Id),
            ("SessionId", msg.SessionId),
            ("Type", msg.Type),
            ("Seq", msg.Seq),
            ("TimeCreated", msg.TimeCreated),
            ("TimeUpdated", msg.TimeUpdated),
            ("Data", msg.Data));
        await conn.ExecuteAsync(@"
            INSERT INTO ""session_message"" (
                ""id"", ""session_id"", ""type"", ""seq"",
                ""time_created"", ""time_updated"", ""data""
            ) VALUES (
                @Id, @SessionId, @Type, @Seq,
                @TimeCreated, @TimeUpdated, @Data
            )
            ON CONFLICT(""id"") DO UPDATE SET
                ""session_id"" = @SessionId,
                ""type"" = @Type,
                ""seq"" = @Seq,
                ""time_created"" = @TimeCreated,
                ""time_updated"" = @TimeUpdated,
                ""data"" = @Data",
            parameters);
    }

    public async Task DeleteBySessionAsync(string sessionId)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM \"session_message\" WHERE \"session_id\" = @SessionId",
            Parameters(("SessionId", sessionId)));
    }

    public async Task DeleteAsync(string sessionId, string messageId)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM \"session_message\" WHERE \"session_id\" = @SessionId AND \"id\" = @MessageId",
            Parameters(("SessionId", sessionId), ("MessageId", messageId)));
    }

    public async Task DeleteAfterSeqAsync(string sessionId, long seq)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM \"session_message\" WHERE \"session_id\" = @SessionId AND \"seq\" > @Seq",
            Parameters(("SessionId", sessionId), ("Seq", seq)));
    }

    public async Task<long> GetMaxSeqAsync(string sessionId)
    {
        using var conn = db.CreateConnection();
        var result = await conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT MAX(\"seq\") FROM \"session_message\" WHERE \"session_id\" = @SessionId",
            Parameters(("SessionId", sessionId)));
        return result ?? 0;
    }

    private static DynamicParameters Parameters(params (string Name, object? Value)[] values)
    {
        var parameters = new DynamicParameters();
        foreach (var (name, value) in values)
            parameters.Add(name, value);
        return parameters;
    }
}
