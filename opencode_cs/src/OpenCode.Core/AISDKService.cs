namespace OpenCode.Core;

public interface IProviderPlugin
{
    string Id { get; }
    Task<ProviderSdkResult?> CreateSdkAsync(ProviderSdkEvent evt);
    Task<ProviderLanguageResult?> CreateLanguageAsync(ProviderLanguageEvent evt);
}

public record ProviderSdkEvent(
    Schema.ModelInfo Model,
    string Package,
    Dictionary<string, object> Options
);

public record ProviderSdkResult(object Sdk);

public record ProviderLanguageEvent(
    Schema.ModelInfo Model,
    object Sdk,
    Dictionary<string, object> Options
);

public record ProviderLanguageResult(object Language);

public interface IAISDKService
{
    Task<object> GetLanguageModelAsync(Schema.ModelInfo model);
    Task RegisterSdkHookAsync(Func<ProviderSdkEvent, Task<ProviderSdkResult?>> callback);
    Task RegisterLanguageHookAsync(Func<ProviderLanguageEvent, Task<ProviderLanguageResult?>> callback);
}

public class AISDKService : IAISDKService
{
    readonly List<Func<ProviderSdkEvent, Task<ProviderSdkResult?>>> sdkHooks = new();
    readonly List<Func<ProviderLanguageEvent, Task<ProviderLanguageResult?>>> languageHooks = new();
    readonly Dictionary<string, object> sdkCache = new();
    readonly Dictionary<string, object> languageCache = new();
    readonly SemaphoreSlim semaphore = new(1, 1);

    public Task RegisterSdkHookAsync(Func<ProviderSdkEvent, Task<ProviderSdkResult?>> callback)
    {
        lock (sdkHooks) { sdkHooks.Add(callback); }
        return Task.CompletedTask;
    }

    public Task RegisterLanguageHookAsync(Func<ProviderLanguageEvent, Task<ProviderLanguageResult?>> callback)
    {
        lock (languageHooks) { languageHooks.Add(callback); }
        return Task.CompletedTask;
    }

    public async Task<object> GetLanguageModelAsync(Schema.ModelInfo model)
    {
        var key = $"{model.ProviderId}/{model.Id}/{model.Request.Variant ?? "default"}";

        await semaphore.WaitAsync();
        try
        {
            if (languageCache.TryGetValue(key, out var cached))
                return cached;
        }
        finally
        {
            semaphore.Release();
        }

        if (model.Api is not Schema.ModelApiAISDK aisdkApi)
            throw new ProviderInitError(model.ProviderId, $"Unsupported API type: {model.Api}");

        var options = PrepareOptions(model, aisdkApi.Package);
        var sdkKey = System.Text.Json.JsonSerializer.Serialize(new { model.ProviderId, api = model.Api, options });

        object? sdk = null;
        await semaphore.WaitAsync();
        try
        {
            if (sdkCache.TryGetValue(sdkKey, out var cachedSdk))
            {
                sdk = cachedSdk;
            }
            else
            {
                var sdkEvent = new ProviderSdkEvent(model, aisdkApi.Package, options);
                foreach (var hook in sdkHooks)
                {
                    var result = await hook(sdkEvent);
                    if (result != null) { sdk = result.Sdk; break; }
                }
                if (sdk == null)
                    throw new ProviderInitError(model.ProviderId, "No provider plugin returned an SDK");
                sdkCache[sdkKey] = sdk;
            }
        }
        finally
        {
            semaphore.Release();
        }

        var langEvent = new ProviderLanguageEvent(model, sdk, options);
        object? language = null;
        foreach (var hook in languageHooks)
        {
            var result = await hook(langEvent);
            if (result != null) { language = result.Language; break; }
        }

        if (language == null)
            throw new ProviderInitError(model.ProviderId, "No provider plugin returned a language model");

        await semaphore.WaitAsync();
        try
        {
            languageCache[key] = language;
        }
        finally
        {
            semaphore.Release();
        }

        return language;
    }

    static Dictionary<string, object> PrepareOptions(Schema.ModelInfo model, string package)
    {
        var options = new Dictionary<string, object>
        {
            ["name"] = model.ProviderId
        };
        if (model.Api is Schema.ModelApiAISDK aisdk && aisdk.Settings != null)
        {
            foreach (var (k, v) in aisdk.Settings)
                options[k] = v;
        }
        foreach (var (k, v) in model.Request.Body)
            options[k] = v;
        if (model.Api is Schema.ModelApiAISDK aisdk2 && aisdk2.Url != null)
            options["baseURL"] = aisdk2.Url;
        return options;
    }
}

public class ProviderInitError : Exception
{
    public string ProviderId { get; }
    public ProviderInitError(string providerId, string message) : base($"Provider {providerId}: {message}")
    {
        ProviderId = providerId;
    }
}

public class ProviderRegistry
{
    readonly List<IProviderPlugin> plugins = new();

    public void Register(IProviderPlugin plugin)
    {
        plugins.Add(plugin);
    }

    public IReadOnlyList<IProviderPlugin> Plugins => plugins.AsReadOnly();
}
