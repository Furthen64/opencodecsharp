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
