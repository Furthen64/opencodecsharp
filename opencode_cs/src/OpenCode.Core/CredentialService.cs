namespace OpenCode.Core;

public record CredentialInfo(
    string Id,
    string IntegrationId,
    string Label,
    Schema.CredentialValue Value
);

public interface ICredentialService
{
    Task<IReadOnlyList<CredentialInfo>> GetAllAsync();
    Task<IReadOnlyList<CredentialInfo>> ListAsync(string integrationId);
    Task<CredentialInfo?> GetAsync(string id);
    Task<CredentialInfo> CreateAsync(string integrationId, Schema.CredentialValue value, string? label = null);
    Task UpdateAsync(string id, string? label = null, Schema.CredentialValue? value = null);
    Task RemoveAsync(string id);
}

public class InMemoryCredentialService : ICredentialService
{
    readonly Dictionary<string, CredentialInfo> credentials = new();
    readonly SemaphoreSlim semaphore = new(1, 1);

    public async Task<IReadOnlyList<CredentialInfo>> GetAllAsync()
    {
        await semaphore.WaitAsync();
        try
        {
            return credentials.Values.OrderBy(c => c.Id).ToList();
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<CredentialInfo>> ListAsync(string integrationId)
    {
        await semaphore.WaitAsync();
        try
        {
            return credentials.Values
                .Where(c => c.IntegrationId == integrationId)
                .OrderBy(c => c.Id)
                .ToList();
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<CredentialInfo?> GetAsync(string id)
    {
        await semaphore.WaitAsync();
        try
        {
            return credentials.TryGetValue(id, out var cred) ? cred : null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<CredentialInfo> CreateAsync(string integrationId, Schema.CredentialValue value, string? label = null)
    {
        await semaphore.WaitAsync();
        try
        {
            var credential = new CredentialInfo(
                Id: Guid.NewGuid().ToString(),
                IntegrationId: integrationId,
                Label: label ?? "default",
                Value: value
            );

            var existing = credentials.Values.Where(c => c.IntegrationId == integrationId).ToList();
            foreach (var e in existing)
                credentials.Remove(e.Id);

            credentials[credential.Id] = credential;
            return credential;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task UpdateAsync(string id, string? label = null, Schema.CredentialValue? value = null)
    {
        await semaphore.WaitAsync();
        try
        {
            if (!credentials.TryGetValue(id, out var existing))
                return;

            credentials[id] = existing with
            {
                Label = label ?? existing.Label,
                Value = value ?? existing.Value
            };
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task RemoveAsync(string id)
    {
        await semaphore.WaitAsync();
        try
        {
            credentials.Remove(id);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
