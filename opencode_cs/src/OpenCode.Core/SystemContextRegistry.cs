namespace OpenCode.Core;

public record SystemContextRegistryEntry(
    SystemContextKey Key,
    Func<Task<SystemContextBuilder>> Load
);

public interface ISystemContextRegistryService
{
    Task RegisterAsync(SystemContextRegistryEntry entry);
    Task<SystemContextBuilder> LoadAllAsync();
}

public class SystemContextRegistryService : ISystemContextRegistryService
{
    readonly List<SystemContextRegistryEntry> entries = new();
    readonly SemaphoreSlim semaphore = new(1, 1);

    public async Task RegisterAsync(SystemContextRegistryEntry entry)
    {
        await semaphore.WaitAsync();
        try
        {
            if (entries.Any(e => e.Key == entry.Key))
                throw new SystemContextDuplicateKeyError(entry.Key);
            entries.Add(entry);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<SystemContextBuilder> LoadAllAsync()
    {
        await semaphore.WaitAsync();
        IReadOnlyList<SystemContextRegistryEntry> snapshot;
        try
        {
            snapshot = entries.OrderBy(e => e.Key.Value).ToList();
        }
        finally
        {
            semaphore.Release();
        }

        var builder = SystemContextBuilder.Empty();
        var tasks = snapshot.Select(async entry => new { entry.Key, Context = await entry.Load() });
        var results = await Task.WhenAll(tasks);

        foreach (var result in results.OrderBy(r => r.Key.Value))
        {
            builder.Combine(result.Context);
        }

        return builder;
    }
}
