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

public sealed class ServerSessionRevertTests
{
    [Fact]
    public async Task DiffRevertAndUnrevertUseMessageSnapshotsAndPersistState()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"opencode-revert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var firstSnapshots = new RecordingSnapshotService();

        await using (var app = await StartServerAsync(directory, firstSnapshots))
        {
            var store = app.Services.GetRequiredService<SessionStore>();
            await store.SetAsync(Session("ses_revert", directory));
            await store.AddMessageAsync("ses_revert", User("msg_1", "first"));
            await store.AddMessageAsync("ses_revert", Assistant(
                "msg_a", "tree_before", "tree_after", ["one.txt"]));
            await store.AddMessageAsync("ses_revert", User("msg_2", "second"));
            await store.AddMessageAsync("ses_revert", Assistant(
                "msg_b", "tree_before_2", "tree_after_2", ["two.txt"]));

            using var client = new HttpClient { BaseAddress = ServerAddress(app) };
            using var diffResponse = await client.GetAsync("session/ses_revert/diff?messageID=msg_1");
            Assert.Equal(HttpStatusCode.OK, diffResponse.StatusCode);
            using var diff = await JsonDocument.ParseAsync(await diffResponse.Content.ReadAsStreamAsync());
            Assert.Equal("one.txt", Assert.Single(diff.RootElement.EnumerateArray()).GetProperty("path").GetString());
            Assert.Contains(firstSnapshots.Diffs, call => call == ("tree_before", "tree_after"));

            using var revertResponse = await client.PostAsJsonAsync(
                "session/ses_revert/revert",
                new { messageID = "msg_1" });
            Assert.Equal(HttpStatusCode.OK, revertResponse.StatusCode);
            using var reverted = await JsonDocument.ParseAsync(await revertResponse.Content.ReadAsStreamAsync());
            var state = reverted.RootElement.GetProperty("revert");
            Assert.Equal("msg_1", state.GetProperty("messageId").GetString());
            Assert.Equal("tree_current", state.GetProperty("snapshot").GetString());
            Assert.Equal("tree_before", firstSnapshots.Restored["one.txt"].Value);
            Assert.Equal("tree_before_2", firstSnapshots.Restored["two.txt"].Value);
        }

        var secondSnapshots = new RecordingSnapshotService();
        await using (var app = await StartServerAsync(directory, secondSnapshots))
        {
            using var client = new HttpClient { BaseAddress = ServerAddress(app) };
            using var persistedResponse = await client.GetAsync("session/ses_revert");
            Assert.Equal(HttpStatusCode.OK, persistedResponse.StatusCode);
            using var persisted = await JsonDocument.ParseAsync(await persistedResponse.Content.ReadAsStreamAsync());
            Assert.Equal(
                "tree_current",
                persisted.RootElement.GetProperty("revert").GetProperty("snapshot").GetString());

            using var unrevertResponse = await client.PostAsync("session/ses_revert/unrevert", null);
            Assert.Equal(HttpStatusCode.OK, unrevertResponse.StatusCode);
            using var unreverted = await JsonDocument.ParseAsync(await unrevertResponse.Content.ReadAsStreamAsync());
            Assert.False(unreverted.RootElement.TryGetProperty("revert", out _));
            Assert.Equal("tree_current", Assert.Single(secondSnapshots.CheckedOut).Value);
            Assert.Null((await app.Services.GetRequiredService<SessionStore>().GetAsync("ses_revert"))!.Revert);
        }

        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task NewPromptCommitsRevertByRemovingDiscardedHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"opencode-revert-commit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await using var app = await StartServerAsync(directory, new RecordingSnapshotService());
        var store = app.Services.GetRequiredService<SessionStore>();
        await store.SetAsync(Session("ses_commit", directory));
        await store.AddMessageAsync("ses_commit", User("msg_1", "keep"));
        await store.AddMessageAsync("ses_commit", Assistant(
            "msg_a", "tree_before", "tree_after", ["one.txt"]));
        await store.AddMessageAsync("ses_commit", User("msg_2", "discard"));

        using var client = new HttpClient { BaseAddress = ServerAddress(app) };
        using var revertResponse = await client.PostAsJsonAsync(
            "session/ses_commit/revert",
            new { messageID = "msg_2" });
        Assert.Equal(HttpStatusCode.OK, revertResponse.StatusCode);

        using var promptResponse = await client.PostAsJsonAsync(
            "session/ses_commit/prompt_async",
            new
            {
                id = "msg_new",
                prompt = new { text = "replacement" },
                resume = false,
            });
        Assert.Equal(HttpStatusCode.NoContent, promptResponse.StatusCode);
        Assert.Equal(
            ["msg_1", "msg_a", "msg_new"],
            (await store.MessagesAsync("ses_commit")).Select(message => message.Id));
        Assert.Null((await store.GetAsync("ses_commit"))!.Revert);

        await app.StopAsync();
        Directory.Delete(directory, true);
    }

    private static async Task<WebApplication> StartServerAsync(
        string directory,
        ISnapshotService snapshots)
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
        builder.Services.AddSingleton(snapshots);
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

    private static Schema.SessionInfo Session(string id, string directory) => new(
        id, null, "project", null, null, 0,
        new Schema.SessionTokens(0, 0, 0, new Schema.SessionCacheTokens(0, 0)),
        new Schema.SessionTime(100, 100, null), "Revert",
        new Schema.LocationRef(directory, null), null, null);

    private static Schema.SessionMessageUser User(string id, string text) =>
        new(id, null, 100, "user", text, null, null);

    private static Schema.SessionMessageAssistant Assistant(
        string id,
        string start,
        string end,
        string[] files) => new(
            id, null, 100, "assistant", "build",
            new Schema.ModelRef("model", "provider", null),
            [], new Schema.SessionMessageSnapshot(start, end, files), "stop", 0, null, null, 100);

    private sealed class RecordingSnapshotService : ISnapshotService
    {
        public Dictionary<string, SnapshotId> Restored { get; } = [];
        public List<SnapshotId> CheckedOut { get; } = [];
        public List<(string From, string To)> Diffs { get; } = [];
        private bool restored;

        public Task<SnapshotId?> CaptureAsync() => Task.FromResult<SnapshotId?>(
            SnapshotId.Make(restored ? "tree_reverted" : "tree_current"));

        public Task<string[]> FilesAsync(SnapshotId from, SnapshotId to) =>
            Task.FromResult(Array.Empty<string>());

        public Task<Schema.FileDiff[]> DiffAsync(
            SnapshotId from,
            SnapshotId to,
            int context = 3,
            string[]? paths = null)
        {
            Diffs.Add((from.Value, to.Value));
            var path = from.Value == "tree_before" ? "one.txt" : "reverted.txt";
            return Task.FromResult(new[]
            {
                new Schema.FileDiff(path, "modified", 1, 1, $"diff --git a/{path} b/{path}"),
            });
        }

        public Task<Schema.FileDiff[]> PreviewAsync(
            SnapshotId current,
            Dictionary<string, SnapshotId> files,
            int context = 3) => Task.FromResult(Array.Empty<Schema.FileDiff>());

        public Task RestoreAsync(Dictionary<string, SnapshotId> files)
        {
            foreach (var item in files) Restored[item.Key] = item.Value;
            restored = true;
            return Task.CompletedTask;
        }

        public Task CheckoutAsync(SnapshotId snapshot)
        {
            CheckedOut.Add(snapshot);
            return Task.CompletedTask;
        }
    }
}
