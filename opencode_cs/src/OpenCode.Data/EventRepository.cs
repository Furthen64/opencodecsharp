namespace OpenCode.Data;

public record EventRow(
    string Id,
    string AggregateId,
    long Seq,
    string Type,
    string Data
);

public class EventRepository
{
    readonly Database db;
    public EventRepository(Database db) => this.db = db;

    public async Task<long> PublishAsync(string aggregateId, string eventId, string type, string dataJson)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        var newSeq = await conn.QuerySingleAsync<long>(@"
            INSERT INTO ""event_sequence"" (""aggregate_id"", ""seq"")
            VALUES (@AggregateId, 1)
            ON CONFLICT(""aggregate_id"") DO UPDATE
            SET ""seq"" = ""event_sequence"".""seq"" + 1
            RETURNING ""seq""",
            Parameters(("AggregateId", aggregateId)),
            tx);

        await conn.ExecuteAsync(@"
            INSERT INTO ""event"" (""id"", ""aggregate_id"", ""seq"", ""type"", ""data"")
            VALUES (@Id, @AggregateId, @Seq, @Type, @Data)",
            Parameters(
                ("Id", eventId),
                ("AggregateId", aggregateId),
                ("Seq", newSeq),
                ("Type", type),
                ("Data", dataJson)),
            tx);

        tx.Commit();
        return newSeq;
    }

    public async Task<List<EventRow>> GetEventsAsync(string aggregateId, long afterSeq = -1, int limit = 100)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<EventRow>(
            @"SELECT
                ""id"" AS Id,
                ""aggregate_id"" AS AggregateId,
                ""seq"" AS Seq,
                ""type"" AS Type,
                ""data"" AS Data
              FROM ""event""
              WHERE ""aggregate_id"" = @AggregateId AND ""seq"" > @AfterSeq
              ORDER BY ""seq"" ASC
              LIMIT @Limit",
            Parameters(("AggregateId", aggregateId), ("AfterSeq", afterSeq), ("Limit", limit)));
        return rows.ToList();
    }

    public async Task<long> GetSequenceAsync(string aggregateId)
    {
        using var conn = db.CreateConnection();
        var result = await conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT \"seq\" FROM \"event_sequence\" WHERE \"aggregate_id\" = @AggregateId",
            Parameters(("AggregateId", aggregateId)));
        return result ?? 0;
    }

    public async Task<string?> GetOwnerAsync(string aggregateId)
    {
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT \"owner_id\" FROM \"event_sequence\" WHERE \"aggregate_id\" = @AggregateId",
            Parameters(("AggregateId", aggregateId)));
    }

    public async Task RemoveAsync(string aggregateId)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(
            "DELETE FROM \"event\" WHERE \"aggregate_id\" = @AggregateId",
            Parameters(("AggregateId", aggregateId)),
            tx);

        await conn.ExecuteAsync(
            "DELETE FROM \"event_sequence\" WHERE \"aggregate_id\" = @AggregateId",
            Parameters(("AggregateId", aggregateId)),
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
            Parameters(("AggregateId", aggregateId), ("OwnerId", ownerId)));

        return updated > 0;
    }

    private static DynamicParameters Parameters(params (string Name, object? Value)[] values)
    {
        var parameters = new DynamicParameters();
        foreach (var (name, value) in values)
            parameters.Add(name, value);
        return parameters;
    }
}
