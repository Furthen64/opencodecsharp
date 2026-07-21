using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Core;
using OpenCode.Server;
using Schema = OpenCode.Schema;

namespace OpenCode.Tests;

public sealed class ServerSessionHistoryTests
{
    [Fact]
    public async Task ForkDeleteAndAsyncPromptPersistAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"opencode-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string forkId;
        string retainedId;

        await using (var app = await StartServerAsync(directory))
        {
            var store = app.Services.GetRequiredService<SessionStore>();
            await store.SetAsync(Session("ses_original", directory, "Planning"));
            await store.AddMessageAsync("ses_original", Message("msg_001", "one"));
            await store.AddMessageAsync("ses_original", Message("msg_002", "two"));
            await store.AddMessageAsync("ses_original", Message("msg_003", "three"));

            using var client = new HttpClient { BaseAddress = ServerAddress(app) };
            using var forkResponse = await client.PostAsJsonAsync(
                "session/ses_original/fork",
                new { messageID = "msg_003" });
            Assert.Equal(HttpStatusCode.OK, forkResponse.StatusCode);
            using var fork = await JsonDocument.ParseAsync(await forkResponse.Content.ReadAsStreamAsync());
            forkId = fork.RootElement.GetProperty("id").GetString()!;
            Assert.Equal("Planning (fork #1)", fork.RootElement.GetProperty("title").GetString());
            Assert.False(fork.RootElement.TryGetProperty("parentId", out _));

            var forkedMessages = await store.MessagesAsync(forkId);
            Assert.Equal(["one", "two"], forkedMessages.Cast<Schema.SessionMessageUser>().Select(message => message.Text));
            Assert.DoesNotContain(forkedMessages, message => message.Id is "msg_001" or "msg_002");
            retainedId = forkedMessages[1].Id;

            using var nestedForkResponse = await client.PostAsync($"session/{forkId}/fork", null);
            Assert.Equal(HttpStatusCode.OK, nestedForkResponse.StatusCode);
            using var nestedFork = await JsonDocument.ParseAsync(await nestedForkResponse.Content.ReadAsStreamAsync());
            Assert.Equal("Planning (fork #2)", nestedFork.RootElement.GetProperty("title").GetString());

            using var deleteResponse = await client.DeleteAsync($"session/{forkId}/message/{forkedMessages[0].Id}");
            Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
            Assert.True(await deleteResponse.Content.ReadFromJsonAsync<bool>());

            using var promptResponse = await client.PostAsJsonAsync(
                $"session/{forkId}/prompt_async",
                new
                {
                    id = "msg_async",
                    prompt = new { text = "four" },
                    resume = false,
                });
            Assert.Equal(HttpStatusCode.NoContent, promptResponse.StatusCode);
        }

        await using (var app = await StartServerAsync(directory))
        {
            var store = app.Services.GetRequiredService<SessionStore>();
            var fork = await store.GetAsync(forkId);
            Assert.NotNull(fork);
            var messages = await store.MessagesAsync(forkId);
            Assert.Equal([retainedId, "msg_async"], messages.Select(message => message.Id));
            Assert.Equal(["two", "four"], messages.Cast<Schema.SessionMessageUser>().Select(message => message.Text));
        }

        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task DeleteMessageReturnsConflictWhileSessionIsActive()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"opencode-busy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await using var app = await StartServerAsync(directory, new ActiveExecution("ses_busy"));
        var store = app.Services.GetRequiredService<SessionStore>();
        await store.SetAsync(Session("ses_busy", directory, "Busy"));
        await store.AddMessageAsync("ses_busy", Message("msg_busy", "wait"));

        using var client = new HttpClient { BaseAddress = ServerAddress(app) };
        using var response = await client.DeleteAsync("session/ses_busy/message/msg_busy");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("SessionBusyError", body.RootElement.GetProperty("type").GetString());
        Assert.Single(await store.MessagesAsync("ses_busy"));

        await app.StopAsync();
        Directory.Delete(directory, true);
    }

    private static async Task<WebApplication> StartServerAsync(
        string directory,
        ISessionExecution? execution = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["OpenCode:Directory"] = directory;
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        builder.Services.AddOpenCodeServer(builder.Configuration, builder.Environment);
        if (execution is not null)
            builder.Services.AddSingleton(execution);
        var app = builder.Build();
        app.UseExceptionHandler(exceptionApp => exceptionApp.Run(ServerErrors.WriteAsync));
        app.MapOpenCodeRoutes();
        await app.StartAsync();
        return app;
    }

    private static Uri ServerAddress(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new Uri(addresses!.Addresses.Single());
    }

    private static Schema.SessionInfo Session(string id, string directory, string title) => new(
        id, null, "project", null, null, 0,
        new Schema.SessionTokens(0, 0, 0, new Schema.SessionCacheTokens(0, 0)),
        new Schema.SessionTime(100, 100, null), title,
        new Schema.LocationRef(directory, null), null, null);

    private static Schema.SessionMessageUser Message(string id, string text) =>
        new(id, null, 100, "user", text, null, null);

    private sealed class ActiveExecution(string sessionId) : ISessionExecution
    {
        public Task WakeAsync(string id) => Task.CompletedTask;
        public Task ResumeAsync(string id) => Task.CompletedTask;
        public Task InterruptAsync(string id) => Task.CompletedTask;
        public Task<HashSet<string>> ActiveAsync() => Task.FromResult(new HashSet<string> { sessionId });
    }
}
