using System.Text.Json;
using OpenCode.Core;
using OpenCode.Data;

namespace OpenCode.Server;

public sealed class RepositoryDurableEventStore : IDurableEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly Database database;
    private readonly EventRepository events;
    private readonly SemaphoreSlim writes = new(1, 1);

    public RepositoryDurableEventStore(Database database, EventRepository events)
    {
        this.database = database;
        this.events = events;
    }

    public async Task<int> PublishAsync(
        string aggregateId,
        string eventId,
        string type,
        Dictionary<string, object> data)
    {
        await database.InitializeAsync();
        await writes.WaitAsync();
        try
        {
            var sequence = await events.PublishAsync(
                aggregateId,
                eventId,
                type,
                JsonSerializer.Serialize(data, JsonOptions));
            return checked((int)sequence);
        }
        finally
        {
            writes.Release();
        }
    }

    public async Task RemoveAsync(string aggregateId)
    {
        await database.InitializeAsync();
        await writes.WaitAsync();
        try
        {
            await events.RemoveAsync(aggregateId);
        }
        finally
        {
            writes.Release();
        }
    }

    public async Task ClaimAsync(string aggregateId, string ownerId)
    {
        await database.InitializeAsync();
        await writes.WaitAsync();
        try
        {
            await events.ClaimAsync(aggregateId, ownerId);
        }
        finally
        {
            writes.Release();
        }
    }

    public async Task<SerializedEvent[]> ReplayAsync(string aggregateId, int after, int limit)
    {
        await database.InitializeAsync();
        var rows = await events.GetEventsAsync(aggregateId, after, limit);
        return rows.Select(row => new SerializedEvent(
            row.Id,
            row.Type,
            checked((int)row.Seq),
            row.AggregateId,
            JsonSerializer.Deserialize<Dictionary<string, object>>(row.Data, JsonOptions) ?? []))
            .ToArray();
    }
}
