using System.Text.Json;
using OpenCode.Schema;

namespace OpenCode.Data;

public class MessageRepository
{
    readonly Database db;
    public MessageRepository(Database db) => this.db = db;

    public async Task<List<SessionMessageInfo>> GetBySessionAsync(string sessionId, int? afterSeq = null)
    {
        using var conn = db.CreateConnection();

        if (afterSeq.HasValue)
        {
            var rows = await conn.QueryAsync<SessionMessageInfo>(
                "SELECT * FROM \"session_message\" WHERE \"session_id\" = @SessionId AND \"seq\" > @AfterSeq ORDER BY \"seq\" ASC",
                new { SessionId = sessionId, AfterSeq = afterSeq.Value });
            return rows.ToList();
        }
        else
        {
            var rows = await conn.QueryAsync<SessionMessageInfo>(
                "SELECT * FROM \"session_message\" WHERE \"session_id\" = @SessionId ORDER BY \"seq\" ASC",
                new { SessionId = sessionId });
            return rows.ToList();
        }
    }

    public async Task UpsertAsync(SessionMessageInfo msg)
    {
        using var conn = db.CreateConnection();
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
            new
            {
                msg.Id,
                msg.SessionId,
                msg.Type,
                msg.Seq,
                msg.TimeCreated,
                msg.TimeUpdated,
                Data = JsonSerializer.Serialize(msg),
            });
    }

    public async Task DeleteBySessionAsync(string sessionId)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM \"session_message\" WHERE \"session_id\" = @SessionId",
            new { SessionId = sessionId });
    }

    public async Task DeleteAfterSeqAsync(string sessionId, int seq)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM \"session_message\" WHERE \"session_id\" = @SessionId AND \"seq\" > @Seq",
            new { SessionId = sessionId, Seq = seq });
    }

    public async Task<int> GetMaxSeqAsync(string sessionId)
    {
        using var conn = db.CreateConnection();
        var result = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT MAX(\"seq\") FROM \"session_message\" WHERE \"session_id\" = @SessionId",
            new { SessionId = sessionId });
        return result ?? 0;
    }
}
