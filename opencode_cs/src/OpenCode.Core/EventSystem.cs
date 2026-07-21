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

public class EventService : IEventService, IDisposable
{
    readonly ConcurrentDictionary<string, Action<EventPayload>> listeners = new();
    readonly ConcurrentDictionary<string, Channel<EventPayload>> typedChannels = new();
    readonly ConcurrentDictionary<string, Channel<bool>> durableWakes = new();
    readonly object lockObj = new();

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

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

            var seq = await PersistDurableEventAsync(aggregateId, payload, definition);
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

    Task<int> PersistDurableEventAsync(string aggregateId, EventPayload payload, EventDefinition definition)
    {
        lock (lockObj)
        {
            var seq = sequences.TryGetValue(aggregateId, out var current) ? current + 1 : 1;
            sequences[aggregateId] = seq;
            durableEvents.Add(new SerializedEvent(
                Id: payload.Id,
                Type: definition.Type,
                Seq: seq,
                AggregateId: aggregateId,
                Data: SerializeData(payload.Data)
            ));
            return Task.FromResult(seq);
        }
    }

    static Dictionary<string, object> SerializeData(object data)
    {
        var json = JsonSerializer.SerializeToElement(data, JsonOptions);
        return json.Deserialize<Dictionary<string, object>>() ?? new();
    }

    readonly Dictionary<string, int> sequences = new();
    readonly List<SerializedEvent> durableEvents = new();

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

    public Task RemoveAsync(string aggregateId)
    {
        lock (lockObj)
        {
            sequences.Remove(aggregateId);
            durableEvents.RemoveAll(e => e.AggregateId == aggregateId);
        }
        durableWakes.TryRemove(aggregateId, out _);
        return Task.CompletedTask;
    }

    public async Task ClaimAsync(string aggregateId, string ownerId)
    {
        await Task.Yield();
    }

    public Task<SerializedEvent[]> ReplayAsync(string aggregateId, int after = -1, int limit = 100)
    {
        lock (lockObj)
        {
            return Task.FromResult(durableEvents
                .Where(e => e.AggregateId == aggregateId && e.Seq > after)
                .OrderBy(e => e.Seq)
                .Take(limit)
                .ToArray());
        }
    }

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
