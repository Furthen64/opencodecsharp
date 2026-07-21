using System.Collections.Concurrent;
using System.Text.Json;

namespace OpenCode.Core;

public record EventPayload(
    string Id,
    string Type,
    long Timestamp,
    string SessionId,
    Dictionary<string, object>? Metadata,
    SessionEventDurable? Durable,
    LocationRef? Location,
    object Data
);

public record EventDefinition(
    string Type,
    bool IsDurable,
    string? AggregateField,
    int Version
);

public record SerializedEvent(
    string Id,
    string Type,
    int Seq,
    string AggregateId,
    Dictionary<string, object> Data
);

public record PublishOptions(
    string? Id,
    Dictionary<string, object>? Metadata,
    LocationRef? Location
);

public class InvalidDurableEventError : Exception
{
    public string Type { get; }
    public InvalidDurableEventError(string type, string message) : base(message)
    {
        Type = type;
    }
}

public interface IEventService
{
    Task<EventPayload> PublishAsync(EventDefinition definition, object data, PublishOptions? options = null);
    IDisposable Subscribe(Action<EventPayload> listener);
    Task UnsubscribeAsync(CancellationToken ct);
    Task RemoveAsync(string aggregateId);
    Task ClaimAsync(string aggregateId, string ownerId);
    Task<SerializedEvent[]> ReplayAsync(string aggregateId, int after = -1, int limit = 100);
}

public interface IDurableEventStore
{
    Task<int> PublishAsync(string aggregateId, string eventId, string type, Dictionary<string, object> data);
    Task RemoveAsync(string aggregateId);
    Task ClaimAsync(string aggregateId, string ownerId);
    Task<SerializedEvent[]> ReplayAsync(string aggregateId, int after, int limit);
}

public sealed class InMemoryDurableEventStore : IDurableEventStore
{
    private readonly Dictionary<string, int> sequences = new();
    private readonly List<SerializedEvent> events = [];
    private readonly object sync = new();

    public Task<int> PublishAsync(
        string aggregateId,
        string eventId,
        string type,
        Dictionary<string, object> data)
    {
        lock (sync)
        {
            var sequence = sequences.TryGetValue(aggregateId, out var current) ? current + 1 : 1;
            sequences[aggregateId] = sequence;
            events.Add(new SerializedEvent(eventId, type, sequence, aggregateId, data));
            return Task.FromResult(sequence);
        }
    }

    public Task RemoveAsync(string aggregateId)
    {
        lock (sync)
        {
            sequences.Remove(aggregateId);
            events.RemoveAll(item => item.AggregateId == aggregateId);
        }
        return Task.CompletedTask;
    }

    public Task ClaimAsync(string aggregateId, string ownerId) => Task.CompletedTask;

    public Task<SerializedEvent[]> ReplayAsync(string aggregateId, int after, int limit)
    {
        lock (sync)
        {
            return Task.FromResult(events
                .Where(item => item.AggregateId == aggregateId && item.Seq > after)
                .OrderBy(item => item.Seq)
                .Take(limit)
                .ToArray());
        }
    }
}

public class EventService : IEventService, IDisposable
{
    readonly ConcurrentDictionary<string, Action<EventPayload>> listeners = new();
    readonly ConcurrentDictionary<string, Channel<EventPayload>> typedChannels = new();
    readonly ConcurrentDictionary<string, Channel<bool>> durableWakes = new();
    readonly IDurableEventStore durableStore;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public EventService() : this(new InMemoryDurableEventStore())
    {
    }

    public EventService(IDurableEventStore durableStore)
    {
        this.durableStore = durableStore;
    }

    public async Task<EventPayload> PublishAsync(EventDefinition definition, object data, PublishOptions? options = null)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = options?.Id ?? Guid.NewGuid().ToString();

        var payload = new EventPayload(
            Id: id,
            Type: definition.Type,
            Timestamp: timestamp,
            SessionId: ExtractSessionId(data),
            Metadata: options?.Metadata,
            Durable: null,
            Location: options?.Location,
            Data: data
        );

        if (definition.IsDurable)
        {
            var aggregateId = ExtractAggregateId(data, definition.AggregateField);
            if (string.IsNullOrEmpty(aggregateId))
                throw new InvalidDurableEventError(definition.Type, $"Expected aggregate field {definition.AggregateField}");

            var seq = await durableStore.PublishAsync(
                aggregateId,
                payload.Id,
                VersionedType(definition),
                SerializeData(payload.Data));
            payload = payload with { Durable = new SessionEventDurable(aggregateId, seq, definition.Version) };

            await NotifyDurableWakesAsync(aggregateId);
        }

        await NotifyListenersAsync(payload);
        await NotifyTypedChannelAsync(payload);

        return payload;
    }

    public IDisposable Subscribe(Action<EventPayload> listener)
    {
        var id = Guid.NewGuid().ToString("N");
        listeners[id] = listener;
        return new Subscription(() => listeners.TryRemove(id, out _));
    }

    static string ExtractSessionId(object data)
    {
        var prop = data?.GetType().GetProperty("SessionId");
        return prop?.GetValue(data)?.ToString() ?? string.Empty;
    }

    static string? ExtractAggregateId(object data, string? field)
    {
        if (string.IsNullOrEmpty(field)) return null;
        var prop = data.GetType().GetProperty(field);
        return prop?.GetValue(data)?.ToString();
    }

    static Dictionary<string, object> SerializeData(object data)
    {
        var json = JsonSerializer.SerializeToElement(data, JsonOptions);
        return json.Deserialize<Dictionary<string, object>>() ?? new();
    }

    private static string VersionedType(EventDefinition definition) =>
        $"{definition.Type}.{definition.Version}";

    async Task NotifyListenersAsync(EventPayload payload)
    {
        var allListeners = listeners.Values.ToList();
        foreach (var listener in allListeners)
        {
            try
            {
                listener(payload);
            }
            catch
            {
            }
        }
        await Task.Yield();
    }

    async Task NotifyTypedChannelAsync(EventPayload payload)
    {
        if (typedChannels.TryGetValue(payload.Type, out var channel))
        {
            await channel.Writer.WriteAsync(payload);
        }
        await Task.Yield();
    }

    async Task NotifyDurableWakesAsync(string aggregateId)
    {
        if (durableWakes.TryGetValue(aggregateId, out var wake))
        {
            await wake.Writer.WriteAsync(true);
        }
        await Task.Yield();
    }

    public async Task UnsubscribeAsync(CancellationToken ct)
    {
        foreach (var channel in typedChannels.Values)
        {
            channel.Writer.Complete();
        }
        foreach (var wake in durableWakes.Values)
        {
            wake.Writer.Complete();
        }
        listeners.Clear();
        await Task.Yield();
    }

    public async Task RemoveAsync(string aggregateId)
    {
        await durableStore.RemoveAsync(aggregateId);
        durableWakes.TryRemove(aggregateId, out _);
    }

    public Task ClaimAsync(string aggregateId, string ownerId) => durableStore.ClaimAsync(aggregateId, ownerId);

    public Task<SerializedEvent[]> ReplayAsync(string aggregateId, int after = -1, int limit = 100) =>
        durableStore.ReplayAsync(aggregateId, after, limit);

    public void Dispose()
    {
        foreach (var channel in typedChannels.Values)
            channel.Writer.TryComplete();
        foreach (var wake in durableWakes.Values)
            wake.Writer.TryComplete();
        listeners.Clear();
    }

    sealed class Subscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}
