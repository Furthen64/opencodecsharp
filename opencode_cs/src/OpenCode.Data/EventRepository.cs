using OpenCode.Schema;

namespace OpenCode.Data;

public record EventRow(
    string Id,
    string AggregateId,
    int Seq,
    string Type,
    string Data
);

public class EventRepository
{
    readonly Database db;
    public EventRepository(Database db) => this.db = db;

    public async Task<int> PublishAsync(string aggregateId, string type, string dataJson)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        var currentSeq = await conn.QuerySingleOrDefaultAsync<int>(
            "SELECT \"seq\" FROM \"event_sequence\" WHERE \"aggregate_id\" = @AggregateId",
            new { AggregateId = aggregateId },
            tx);

        var newSeq = currentSeq + 1;

        await conn.ExecuteAsync(@"
            INSERT INTO ""event_sequence"" (""aggregate_id"", ""seq"")
            VALUES (@AggregateId, @Seq)
            ON CONFLICT(""aggregate_id"") DO UPDATE SET ""seq"" = @Seq",
            new { AggregateId = aggregateId, Seq = newSeq },
            tx);

        var eventId = EventId.Create();
        await conn.ExecuteAsync(@"
            INSERT INTO ""event"" (""id"", ""aggregate_id"", ""seq"", ""type"", ""data"")
            VALUES (@Id, @AggregateId, @Seq, @Type, @Data)",
            new { Id = eventId, AggregateId = aggregateId, Seq = newSeq, Type = type, Data = dataJson },
            tx);

        tx.Commit();
        return newSeq;
    }

    public async Task<List<EventRow>> GetEventsAsync(string aggregateId, int afterSeq = 0, int limit = 100)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<EventRow>(
            "SELECT * FROM \"event\" WHERE \"aggregate_id\" = @AggregateId AND \"seq\" > @AfterSeq ORDER BY \"seq\" ASC LIMIT @Limit",
            new { AggregateId = aggregateId, AfterSeq = afterSeq, Limit = limit });
        return rows.ToList();
    }

    public async Task<int> GetSequenceAsync(string aggregateId)
    {
        using var conn = db.CreateConnection();
        var result = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT \"seq\" FROM \"event_sequence\" WHERE \"aggregate_id\" = @AggregateId",
            new { AggregateId = aggregateId });
        return result ?? 0;
    }

    public async Task RemoveAsync(string aggregateId)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            "DELETE FROM \"event\" WHERE \"aggregate_id\" = @AggregateId",
            new { AggregateId = aggregateId },
            tx);

        await conn.ExecuteAsync(
            "DELETE FROM \"event_sequence\" WHERE \"aggregate_id\" = @AggregateId",
            new { AggregateId = aggregateId },
            tx);

        tx.Commit();
    }

    public async Task<bool> ClaimAsync(string aggregateId, string ownerId)
    {
        using var conn = db.CreateConnection();

        var updated = await conn.ExecuteAsync(@"
            UPDATE ""event_sequence""
            SET ""owner_id"" = @OwnerId
            WHERE ""aggregate_id"" = @AggregateId
              AND (""owner_id"" IS NULL OR ""owner_id"" = @OwnerId)",
            new { AggregateId = aggregateId, OwnerId = ownerId });

        return updated > 0;
    }
}
