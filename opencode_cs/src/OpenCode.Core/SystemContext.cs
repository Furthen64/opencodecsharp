using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCode.Core;

public readonly record struct SystemContextKey(string Value)
{
    public override string ToString() => Value;

    public static SystemContextKey Make(string value)
    {
        if (!IsValidKey(value))
            throw new ArgumentException($"Invalid system context key: {value}", nameof(value));
        return new SystemContextKey(value);
    }

    static bool IsValidKey(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var slashIndex = value.IndexOf('/');
        if (slashIndex <= 0 || slashIndex >= value.Length - 1) return false;
        var prefix = value[..slashIndex];
        var suffix = value[(slashIndex + 1)..];
        return IsValidSegment(prefix) && IsValidSegment(suffix);
    }

    static bool IsValidSegment(string segment) =>
        segment.Length > 0 && segment.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-');
}

public static class SystemContextUnavailable
{
    public static readonly object Value = new();
}

public interface ISystemContextSource
{
    SystemContextKey Key { get; }
    Task<object?> LoadAsync();
    string Baseline(object value);
    string Update(object previous, object current);
    string? Removed(object previous);
    bool TryDecode(JsonElement stored, out object? decoded);
    object Encode(object value);
    bool AreEqual(object a, object b);
}

public interface ISystemContextSource<T> : ISystemContextSource
{
    new Task<T?> LoadAsync();
    string Baseline(T value);
    string Update(T previous, T current);
    string? Removed(T previous);
    bool TryDecode(JsonElement stored, out T? decoded);
    object Encode(T value);
    bool AreEqual(T a, T b);
}

public class SystemContextSource<T> : ISystemContextSource<T>
{
    readonly Func<Task<T?>> loadFn;
    readonly Func<T, string> baselineFn;
    readonly Func<T, T, string> updateFn;
    readonly Func<T, string>? removedFn;
    readonly Func<T, object> encodeFn;
    readonly Func<T, T, bool> equalFn;

    public SystemContextKey Key { get; }

    public SystemContextSource(
        SystemContextKey key,
        Func<Task<T?>> load,
        Func<T, string> baseline,
        Func<T, T, string> update,
        Func<T, string>? removed = null,
        Func<T, object>? encode = null,
        Func<T, T, bool>? equal = null)
    {
        Key = key;
        loadFn = load;
        baselineFn = baseline;
        updateFn = update;
        removedFn = removed;
        encodeFn = encode ?? (v => v!);
        equalFn = equal ?? ((a, b) => Equals(a, b));
    }

    public async Task<T?> LoadAsync() => await loadFn();
    public string Baseline(T value) => baselineFn(value);
    public string Update(T previous, T current) => updateFn(previous, current);
    public string? Removed(T previous) => removedFn?.Invoke(previous);
    public object Encode(T value) => encodeFn(value);
    public bool AreEqual(T a, T b) => equalFn(a, b);

    async Task<object?> ISystemContextSource.LoadAsync()
    {
        var result = await loadFn();
        return result;
    }

    string ISystemContextSource.Baseline(object value) => Baseline((T)value);
    string ISystemContextSource.Update(object previous, object current) => Update((T)previous, (T)current);
    string? ISystemContextSource.Removed(object previous) => Removed((T)previous);

    public bool TryDecode(JsonElement stored, out T? decoded)
    {
        try
        {
            decoded = stored.Deserialize<T>();
            return decoded != null;
        }
        catch
        {
            decoded = default;
            return false;
        }
    }

    bool ISystemContextSource.TryDecode(JsonElement stored, out object? decoded)
    {
        var result = TryDecode(stored, out var typed);
        decoded = typed;
        return result;
    }

    object ISystemContextSource.Encode(object value) => Encode((T)value);
    bool ISystemContextSource.AreEqual(object a, object b) => AreEqual((T)a, (T)b);
}

public record SystemContextSnapshotValue(
    JsonElement Value,
    string? Removed = null
);

public class SystemContextSnapshot : Dictionary<string, SystemContextSnapshotValue>;

public record SystemContextGeneration(
    string Baseline,
    SystemContextSnapshot Snapshot
);

public abstract record ReconcileResult;

public record ReconcileUnchanged() : ReconcileResult;

public record ReconcileUpdated(
    string Text,
    SystemContextSnapshot Snapshot
) : ReconcileResult;

public record ReplacementReady(
    SystemContextGeneration Generation
) : ReconcileResult;

public record ReplacementBlocked() : ReconcileResult;

public class SystemContextInitializationBlocked : Exception
{
    public SystemContextKey[] Keys { get; }
    public SystemContextInitializationBlocked(SystemContextKey[] keys)
        : base($"System context initialization blocked by unavailable sources: {string.Join(", ", keys.Select(k => k.Value))}")
    {
        Keys = keys;
    }
}

public class SystemContextDuplicateKeyError : Exception
{
    public SystemContextKey Key { get; }
    public SystemContextDuplicateKeyError(SystemContextKey key)
        : base($"Duplicate system context key: {key.Value}")
    {
        Key = key;
    }
}

public class SystemContextBuilder
{
    readonly List<ISystemContextSource> sources = new();

    public static SystemContextBuilder Empty() => new();

    public SystemContextBuilder AddSource(ISystemContextSource source)
    {
        sources.Add(source);
        return this;
    }

    public SystemContextBuilder Combine(SystemContextBuilder other)
    {
        foreach (var source in other.sources)
        {
            if (sources.Any(s => s.Key == source.Key))
                throw new SystemContextDuplicateKeyError(source.Key);
            sources.Add(source);
        }
        return this;
    }

    public IReadOnlyList<ISystemContextSource> Sources => sources.AsReadOnly();
}

public interface ISystemContextService
{
    Task<SystemContextGeneration> InitializeAsync(SystemContextBuilder context);
    Task<ReconcileResult> ReconcileAsync(SystemContextBuilder context, SystemContextSnapshot previous);
    Task<ReconcileResult> ReplaceAsync(SystemContextBuilder context, SystemContextSnapshot previous);
}

public class SystemContextService : ISystemContextService
{
    public async Task<SystemContextGeneration> InitializeAsync(SystemContextBuilder context)
    {
        var entries = await ObserveAsync(context);
        var unavailable = entries.Where(e => e.IsUnavailable).Select(e => e.Source.Key).ToArray();
        if (unavailable.Length > 0)
            throw new SystemContextInitializationBlocked(unavailable);
        return InitializeObservation(entries);
    }

    public async Task<ReconcileResult> ReconcileAsync(SystemContextBuilder context, SystemContextSnapshot previous)
    {
        var entries = await ObserveAsync(context);
        var result = ReconcileObservation(entries, previous);
        if (result is ReconcileUnchanged or ReconcileUpdated)
            return result;
        return ReplaceObservation(entries, previous);
    }

    public async Task<ReconcileResult> ReplaceAsync(SystemContextBuilder context, SystemContextSnapshot previous)
    {
        var entries = await ObserveAsync(context);
        return ReplaceObservation(entries, previous);
    }

    async Task<IReadOnlyList<ObservationEntry>> ObserveAsync(SystemContextBuilder context)
    {
        var tasks = context.Sources.Select(async source =>
        {
            var value = await source.LoadAsync();
            if (value == null || (value is object o && ReferenceEquals(o, SystemContextUnavailable.Value)))
                return new ObservationEntry(source, true, null);
            return new ObservationEntry(source, false, value);
        });
        return (await Task.WhenAll(tasks)).AsReadOnly();
    }

    static SystemContextGeneration InitializeObservation(IReadOnlyList<ObservationEntry> entries)
    {
        var available = entries.Where(e => !e.IsUnavailable).ToList();
        var texts = new List<string>();
        var snapshot = new SystemContextSnapshot();

        foreach (var entry in available)
        {
            var text = entry.Source.Baseline(entry.Value!);
            var snapshotValue = new SystemContextSnapshotValue(
                JsonSerializer.SerializeToElement(entry.Source.Encode(entry.Value!))
            );
            texts.Add(text);
            snapshot[entry.Source.Key.Value] = snapshotValue;
        }

        return new SystemContextGeneration(string.Join("\n\n", texts), snapshot);
    }

    static ReconcileResult ReconcileObservation(IReadOnlyList<ObservationEntry> entries, SystemContextSnapshot previous)
    {
        var keys = new HashSet<string>(entries.Select(e => e.Source.Key.Value));
        var comparisons = new Dictionary<string, CompareResult>();

        foreach (var entry in entries.Where(e => !e.IsUnavailable))
        {
            if (!previous.TryGetValue(entry.Source.Key.Value, out var stored)) continue;
            var decoded = entry.Source.TryDecode(stored.Value, out var decodedValue);
            if (!decoded) return new ReplacementReady(new SystemContextGeneration("", new()));
            if (!entry.Source.AreEqual(decodedValue!, entry.Value!))
                comparisons[entry.Source.Key.Value] = new CompareResult.Updated(entry);
            else
                comparisons[entry.Source.Key.Value] = new CompareResult.Unchanged();
        }

        foreach (var key in previous.Keys.Where(k => !keys.Contains(k)).OrderBy(k => k))
        {
            if (previous[key].Removed == null)
                return new ReplacementReady(new SystemContextGeneration("", new()));
        }

        var texts = new List<string>();
        var snapshot = new SystemContextSnapshot();

        foreach (var entry in entries)
        {
            var stored = previous.TryGetValue(entry.Source.Key.Value, out var s) ? s : null;

            if (entry.IsUnavailable)
            {
                if (stored != null) snapshot[entry.Source.Key.Value] = stored;
                continue;
            }

            if (stored == null)
            {
                var text = entry.Source.Baseline(entry.Value!);
                texts.Add(text);
                snapshot[entry.Source.Key.Value] = new SystemContextSnapshotValue(
                    JsonSerializer.SerializeToElement(entry.Source.Encode(entry.Value!))
                );
                continue;
            }

            if (comparisons.TryGetValue(entry.Source.Key.Value, out var cmp) && cmp is CompareResult.Updated upd)
            {
                var text = entry.Source.Baseline(entry.Value!);
                texts.Add(text);
                snapshot[entry.Source.Key.Value] = new SystemContextSnapshotValue(
                    JsonSerializer.SerializeToElement(entry.Source.Encode(entry.Value!))
                );
                continue;
            }

            snapshot[entry.Source.Key.Value] = stored;
        }

        foreach (var key in previous.Keys.Where(k => !keys.Contains(k)).OrderBy(k => k))
        {
            if (previous[key].Removed != null)
                texts.Add(previous[key].Removed!);
        }

        if (texts.Count == 0) return new ReconcileUnchanged();
        return new ReconcileUpdated(string.Join("\n\n", texts), snapshot);
    }

    static ReconcileResult ReplaceObservation(IReadOnlyList<ObservationEntry> entries, SystemContextSnapshot previous)
    {
        var blocked = entries.Any(e => e.IsUnavailable && previous.ContainsKey(e.Source.Key.Value));
        if (blocked) return new ReplacementBlocked();
        return new ReplacementReady(InitializeObservation(entries));
    }

    record ObservationEntry(ISystemContextSource Source, bool IsUnavailable, object? Value);

    abstract record CompareResult
    {
        public record Unchanged() : CompareResult;
        public record Updated(ObservationEntry Entry) : CompareResult;
    }
}

public static class SystemContextFactory
{
    public static SystemContextSource<string> MakeStringSource(
        SystemContextKey key,
        Func<Task<string?>> load,
        Func<string, string> baseline,
        Func<string, string, string> update,
        Func<string, string>? removed = null)
    {
        return new SystemContextSource<string>(
            key,
            load,
            baseline,
            update,
            removed,
            encode: v => v,
            equal: (a, b) => string.Equals(a, b, StringComparison.Ordinal)
        );
    }

    public static SystemContextSource<JsonElement> MakeJsonSource(
        SystemContextKey key,
        Func<Task<JsonElement>> load,
        Func<JsonElement, string> baseline,
        Func<JsonElement, JsonElement, string> update,
        Func<JsonElement, string>? removed = null)
    {
        return new SystemContextSource<JsonElement>(
            key,
            load,
            baseline,
            update,
            removed,
            encode: v => v,
            equal: (a, b) => a.GetRawText() == b.GetRawText()
        );
    }
}
