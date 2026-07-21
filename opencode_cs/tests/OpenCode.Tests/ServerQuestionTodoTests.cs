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
using OpenCode.Data;
using OpenCode.Server;
using Schema = OpenCode.Schema;

namespace OpenCode.Tests;

public sealed class ServerQuestionTodoTests
{
    [Fact]
    public async Task QuestionEndpointsListAndReplyToPendingRequest()
    {
        var directory = CreateDirectory();
        await using var app = await StartServerAsync(directory);
        using var client = new HttpClient { BaseAddress = ServerAddress(app) };
        var questions = app.Services.GetRequiredService<IQuestionService>();

        var answerTask = questions.AskAsync(new QuestionAskInput(
            "ses_question",
            [new Schema.QuestionInfo("Continue?", "Confirm", [new Schema.QuestionOption("Yes", "Continue")], false, false)],
            null));
        var requestId = await WaitForQuestionAsync(client);

        using var reply = await client.PostAsJsonAsync($"question/{requestId}/reply", new { answers = new[] { new[] { "Yes" } } });

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.True(await reply.Content.ReadFromJsonAsync<bool>());
        Assert.Equal(["Yes"], Assert.Single(await answerTask));
        using var list = await client.GetAsync("question");
        using var body = JsonDocument.Parse(await list.Content.ReadAsStreamAsync());
        Assert.Empty(body.RootElement.GetProperty("data").EnumerateArray());

        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task QuestionEndpointsRejectRequestsAndReturnNotFoundForUnknownIds()
    {
        var directory = CreateDirectory();
        await using var app = await StartServerAsync(directory);
        using var client = new HttpClient { BaseAddress = ServerAddress(app) };
        var questions = app.Services.GetRequiredService<IQuestionService>();

        var answerTask = questions.AskAsync(new QuestionAskInput(
            "ses_question",
            [new Schema.QuestionInfo("Continue?", "Confirm", [], false, false)],
            null));
        var requestId = await WaitForQuestionAsync(client);

        using var reject = await client.PostAsync($"question/{requestId}/reject", null);

        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        await Assert.ThrowsAsync<QuestionRejectedException>(() => answerTask);

        using var missingReply = await client.PostAsJsonAsync("question/que_missing/reply", new { answers = Array.Empty<string[]>() });
        Assert.Equal(HttpStatusCode.NotFound, missingReply.StatusCode);
        using var missingBody = JsonDocument.Parse(await missingReply.Content.ReadAsStreamAsync());
        Assert.Equal("QuestionNotFoundError", missingBody.RootElement.GetProperty("type").GetString());

        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task PermissionEndpointsListStructuredRequestsAndProcessReplies()
    {
        var directory = CreateDirectory();
        await using var app = await StartServerAsync(directory);
        using var client = new HttpClient { BaseAddress = ServerAddress(app) };
        var store = app.Services.GetRequiredService<SessionStore>();
        var permissions = app.Services.GetRequiredService<IPermissionService>();
        await store.SetAsync(Session("ses_permission", directory));
        await ConfigureAskPermissionsAsync(app);
        var result = await permissions.AskAsync(new PermissionAssertInput(
            "per_route", "ses_permission", "edit", ["src/file.cs"], ["src/*"],
            new Dictionary<string, object> { ["path"] = "src/file.cs" },
            new Schema.PermissionSource("tool", "msg_1", "call_1"), "build"));

        Assert.Equal(Schema.PermissionEffect.Ask, result.Effect);
        using var list = await client.GetAsync("permission");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listBody = JsonDocument.Parse(await list.Content.ReadAsStreamAsync());
        var request = Assert.Single(listBody.RootElement.EnumerateArray());
        Assert.Equal("per_route", request.GetProperty("id").GetString());
        Assert.Equal("edit", request.GetProperty("action").GetString());
        Assert.Equal("tool", request.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("msg_1", request.GetProperty("source").GetProperty("messageId").GetString());
        Assert.Equal("call_1", request.GetProperty("source").GetProperty("callId").GetString());

        using var reply = await client.PostAsJsonAsync("permission/per_route/reply", new { reply = "once" });
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.True(await reply.Content.ReadFromJsonAsync<bool>());
        Assert.Empty(await permissions.ListAsync());

        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task PermissionRejectionClearsSessionRequestsAndMissingIdsReturnNotFound()
    {
        var directory = CreateDirectory();
        await using var app = await StartServerAsync(directory);
        using var client = new HttpClient { BaseAddress = ServerAddress(app) };
        var store = app.Services.GetRequiredService<SessionStore>();
        var permissions = app.Services.GetRequiredService<IPermissionService>();
        await store.SetAsync(Session("ses_permission", directory));
        await ConfigureAskPermissionsAsync(app);
        foreach (var id in new[] { "per_first", "per_second" })
        {
            await permissions.AskAsync(new PermissionAssertInput(
                id, "ses_permission", "bash", [id], null, null, null, "build"));
        }

        using var reject = await client.PostAsJsonAsync(
            "permission/per_first/reply", new { reply = "reject", message = "Use a safer command" });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        Assert.Empty(await permissions.ListAsync());

        using var missing = await client.PostAsJsonAsync(
            "permission/per_missing/reply", new { reply = "once" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var missingBody = JsonDocument.Parse(await missing.Content.ReadAsStreamAsync());
        Assert.Equal("PermissionNotFoundError", missingBody.RootElement.GetProperty("type").GetString());
        Assert.Equal("per_missing", missingBody.RootElement.GetProperty("requestID").GetString());

        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task SessionTodoEndpointReturnsTodosAndRequiresAnExistingSession()
    {
        var directory = CreateDirectory();
        await using var app = await StartServerAsync(directory);
        using var client = new HttpClient { BaseAddress = ServerAddress(app) };
        var store = app.Services.GetRequiredService<SessionStore>();
        var todos = app.Services.GetRequiredService<ISessionTodoService>();
        await store.SetAsync(Session("ses_todo", directory));
        await todos.UpdateAsync("ses_todo", [new SessionTodoInfo("Port route", "in_progress", "high")]);

        using var response = await client.GetAsync("session/ses_todo/todo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var todo = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal("Port route", todo.GetProperty("content").GetString());

        using var missing = await client.GetAsync("session/ses_missing/todo");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task SessionLifecycleEndpointsUpdateListChildrenAndDelete()
    {
        var directory = CreateDirectory();
        await using var app = await StartServerAsync(directory);
        using var client = new HttpClient { BaseAddress = ServerAddress(app) };
        var store = app.Services.GetRequiredService<SessionStore>();
        await store.SetAsync(Session("ses_parent", directory));
        await store.SetAsync(Session("ses_child", directory) with { ParentId = "ses_parent" });
        await store.SetAsync(Session("ses_other", directory));
        await store.AddMessageAsync("ses_child", Message("msg_child"));

        using var children = await client.GetAsync("session/ses_parent/children");
        Assert.Equal(HttpStatusCode.OK, children.StatusCode);
        var childItems = await children.Content.ReadFromJsonAsync<Schema.SessionInfo[]>();
        Assert.Equal("ses_child", Assert.Single(childItems!).Id);

        using var update = await client.PatchAsJsonAsync("session/ses_child", new
        {
            title = "Port lifecycle routes",
            time = new { archived = 1234L },
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<Schema.SessionInfo>();
        Assert.Equal("Port lifecycle routes", updated!.Title);
        Assert.Equal(1234L, updated.Time.Archived);

        using var delete = await client.DeleteAsync("session/ses_child");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.True(await delete.Content.ReadFromJsonAsync<bool>());
        Assert.Null(await store.GetAsync("ses_child"));
        Assert.Empty(await store.MessagesAsync("ses_child"));

        using var missing = await client.DeleteAsync("session/ses_child");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task SessionsAndMessagesSurviveServerRestartAndDeletionPersists()
    {
        var directory = CreateDirectory();

        await using (var firstApp = await StartServerAsync(directory))
        {
            using var firstClient = new HttpClient { BaseAddress = ServerAddress(firstApp) };
            using var create = await firstClient.PostAsJsonAsync("session", new { id = "ses_durable" });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            using var prompt = await firstClient.PostAsJsonAsync("session/ses_durable/message", new
            {
                id = "msg_durable",
                prompt = new { text = "Persist this conversation" },
                resume = false,
            });
            Assert.Equal(HttpStatusCode.OK, prompt.StatusCode);

            await firstApp.Services.GetRequiredService<SessionStore>().AddMessageAsync(
                "ses_durable",
                new Schema.SessionMessageAssistant(
                    "msg_tool",
                    null,
                    2,
                    "assistant",
                    "build",
                    new Schema.ModelRef("model", "provider", null),
                    [new Schema.SessionMessageTool(
                        "tool",
                        "call_durable",
                        "lookup",
                        new Schema.SessionMessageProviderInfo(false, null, null),
                        new Schema.ToolStateCompleted(
                            "completed",
                            new Dictionary<string, object> { ["query"] = "answer" },
                            null,
                            [new Schema.ToolTextContent("text", "42")],
                            null,
                            new Dictionary<string, object> { ["answer"] = 42 },
                            new Dictionary<string, object> { ["answer"] = 42 }),
                        2,
                        2,
                        2,
                        null)],
                    null,
                    "tool-calls",
                    null,
                    null,
                    null,
                    2));

            using var update = await firstClient.PatchAsJsonAsync(
                "session/ses_durable", new { title = "Durable session" });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        await using (var secondApp = await StartServerAsync(directory))
        {
            using var secondClient = new HttpClient { BaseAddress = ServerAddress(secondApp) };
            var session = await secondClient.GetFromJsonAsync<Schema.SessionInfo>("session/ses_durable");
            Assert.Equal("Durable session", session!.Title);
            var storedMessages = await secondApp.Services
                .GetRequiredService<SessionStore>()
                .MessagesAsync("ses_durable");
            var storedTool = Assert.Single(storedMessages
                .OfType<Schema.SessionMessageAssistant>()
                .SelectMany(message => message.Content)
                .OfType<Schema.SessionMessageTool>());
            Assert.IsType<Schema.ToolStateCompleted>(storedTool.State);

            using var messages = await secondClient.GetAsync("session/ses_durable/message");
            Assert.Equal(HttpStatusCode.OK, messages.StatusCode);
            using var body = JsonDocument.Parse(await messages.Content.ReadAsStreamAsync());
            var message = Assert.Single(
                body.RootElement.GetProperty("data").EnumerateArray(),
                item => item.GetProperty("id").GetString() == "msg_durable");
            Assert.Equal("msg_durable", message.GetProperty("id").GetString());
            Assert.Equal("Persist this conversation", message.GetProperty("text").GetString());

            using var delete = await secondClient.DeleteAsync("session/ses_durable");
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        await using (var thirdApp = await StartServerAsync(directory))
        {
            using var thirdClient = new HttpClient { BaseAddress = ServerAddress(thirdApp) };
            using var missing = await thirdClient.GetAsync("session/ses_durable");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task DurableEventsSurviveServerRestartAndRemovalPersists()
    {
        var directory = CreateDirectory();
        var definition = new EventDefinition("test.durable", true, "SessionId", 2);

        await using (var firstApp = await StartServerAsync(directory))
        {
            var events = firstApp.Services.GetRequiredService<IEventService>();
            var published = await events.PublishAsync(
                definition,
                new { SessionId = "agg_durable", Value = 1 },
                new PublishOptions("evt_durable_1", null, null));
            Assert.Equal(1, published.Durable?.Seq);
            Assert.Equal(2, published.Durable?.Version);

            var concurrent = await Task.WhenAll(Enumerable.Range(2, 24).Select(value =>
                events.PublishAsync(
                    definition,
                    new { SessionId = "agg_durable", Value = value },
                    new PublishOptions($"evt_durable_{value}", null, null))));
            Assert.Equal(Enumerable.Range(2, 24), concurrent.Select(item => item.Durable!.Seq).Order());
        }

        await using (var secondApp = await StartServerAsync(directory))
        {
            var events = secondApp.Services.GetRequiredService<IEventService>();
            var replayed = await events.ReplayAsync("agg_durable");
            Assert.Equal(Enumerable.Range(1, 25), replayed.Select(item => item.Seq));
            Assert.Equal("evt_durable_1", replayed[0].Id);
            Assert.Equal("test.durable.2", replayed[0].Type);
            Assert.Equal(1, ((JsonElement)replayed[0].Data["Value"]).GetInt32());
            Assert.Equal([21, 22, 23],
                (await events.ReplayAsync("agg_durable", after: 20, limit: 3)).Select(item => item.Seq));

            var second = await events.PublishAsync(
                definition,
                new { SessionId = "agg_durable", Value = 26 },
                new PublishOptions("evt_durable_26", null, null));
            Assert.Equal(26, second.Durable?.Seq);
            await events.ClaimAsync("agg_durable", "server_1");
            Assert.Equal("server_1", await secondApp.Services
                .GetRequiredService<EventRepository>()
                .GetOwnerAsync("agg_durable"));
            await events.RemoveAsync("agg_durable");
        }

        await using (var thirdApp = await StartServerAsync(directory))
        {
            var events = thirdApp.Services.GetRequiredService<IEventService>();
            Assert.Empty(await events.ReplayAsync("agg_durable"));
        }

        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task ProjectEndpointsReturnStableCurrentProjectAndListIt()
    {
        var directory = CreateDirectory();
        await using var app = await StartServerAsync(directory);
        using var client = new HttpClient { BaseAddress = ServerAddress(app) };

        using var firstResponse = await client.GetAsync("project/current");
        using var secondResponse = await client.GetAsync("project/current");
        var first = await firstResponse.Content.ReadFromJsonAsync<Schema.ProjectInfo>();
        var second = await secondResponse.Content.ReadFromJsonAsync<Schema.ProjectInfo>();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(Path.GetFullPath(directory), first.Worktree);

        using var listResponse = await client.GetAsync("project");
        var projects = await listResponse.Content.ReadFromJsonAsync<Schema.ProjectInfo[]>();
        Assert.Equal(first.Id, Assert.Single(projects!).Id);

        Directory.Delete(directory, true);
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"opencode-route-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<string> WaitForQuestionAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var response = await client.GetAsync("question");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            var requests = body.RootElement.GetProperty("data").EnumerateArray().ToArray();
            if (requests.Length > 0)
                return requests[0].GetProperty("id").GetString()!;
            await Task.Delay(10);
        }

        throw new TimeoutException("The pending question was not exposed by the server.");
    }

    private static async Task ConfigureAskPermissionsAsync(WebApplication app)
    {
        var agents = app.Services.GetRequiredService<IAgentService>();
        var transformable = await agents.TransformAsync();
        await transformable.TransformAsync(draft =>
        {
            draft.Update("build", agent => agent with
            {
                Permissions = [new Schema.PermissionRule("*", "*", Schema.PermissionEffect.Ask)]
            });
            draft.SetDefault("build");
            return Task.CompletedTask;
        });
    }

    private static async Task<WebApplication> StartServerAsync(string directory)
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
        new Schema.SessionTime(100, 100, null), id,
        new Schema.LocationRef(directory, null), null, null);

    private static Schema.SessionMessageUser Message(string id) =>
        new(id, null, 100, "user", id, null, null);
}
