using System.Net;
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

public sealed class ServerPaginationTests
{
    [Fact]
    public async Task SessionAndMessageEndpointsFollowOpaqueCursorsInBothDirections()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"opencode-pagination-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await using var app = await StartServerAsync(directory);
        var store = app.Services.GetRequiredService<SessionStore>();
        await store.SetAsync(Session("ses_1", directory, 100));
        await store.SetAsync(Session("ses_2", directory, 200));
        await store.SetAsync(Session("ses_3", directory, 300));
        await store.AddMessageAsync("ses_1", Message("msg_1", 100));
        await store.AddMessageAsync("ses_1", Message("msg_2", 100));
        await store.AddMessageAsync("ses_1", Message("msg_3", 100));

        using var client = new HttpClient { BaseAddress = ServerAddress(app) };

        using var sessionsFirst = await GetPageAsync(client, "session?limit=2&order=asc");
        Assert.Equal(["ses_1", "ses_2"], Ids(sessionsFirst));
        using var sessionsNext = await GetPageAsync(client, $"session?limit=2&cursor={Cursor(sessionsFirst, "next")}");
        Assert.Equal(["ses_3"], Ids(sessionsNext));
        using var sessionsPrevious = await GetPageAsync(client, $"session?limit=2&cursor={Cursor(sessionsNext, "previous")}");
        Assert.Equal(["ses_1", "ses_2"], Ids(sessionsPrevious));

        using var messagesFirst = await GetPageAsync(client, "session/ses_1/message?limit=2");
        Assert.Equal(["msg_3", "msg_2"], Ids(messagesFirst));
        using var messagesNext = await GetPageAsync(client, $"session/ses_1/message?limit=2&cursor={Cursor(messagesFirst, "next")}");
        Assert.Equal(["msg_1"], Ids(messagesNext));
        using var messagesPrevious = await GetPageAsync(client, $"session/ses_1/message?limit=2&cursor={Cursor(messagesNext, "previous")}");
        Assert.Equal(["msg_3", "msg_2"], Ids(messagesPrevious));

        var invalid = await client.GetAsync("session/ses_1/message?cursor=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidBody = JsonDocument.Parse(await invalid.Content.ReadAsStreamAsync());
        Assert.Equal("InvalidCursorError", invalidBody.RootElement.GetProperty("type").GetString());

        var cursorWithOrder = await client.GetAsync(
            $"session/ses_1/message?cursor={Cursor(messagesFirst, "next")}&order=asc");
        Assert.Equal(HttpStatusCode.BadRequest, cursorWithOrder.StatusCode);
        using var conflictBody = JsonDocument.Parse(await cursorWithOrder.Content.ReadAsStreamAsync());
        Assert.Equal("Cursor cannot be combined with order", conflictBody.RootElement.GetProperty("message").GetString());

        Directory.Delete(directory, true);
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

    private static async Task<JsonDocument> GetPageAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    private static string[] Ids(JsonDocument page) => page.RootElement.GetProperty("data")
        .EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();

    private static string Cursor(JsonDocument page, string direction) =>
        Uri.EscapeDataString(page.RootElement.GetProperty("cursor").GetProperty(direction).GetString()!);

    private static Schema.SessionInfo Session(string id, string directory, long time) => new(
        id, null, "project", null, null, 0,
        new Schema.SessionTokens(0, 0, 0, new Schema.SessionCacheTokens(0, 0)),
        new Schema.SessionTime(time, time, null), id,
        new Schema.LocationRef(directory, null), null, null);

    private static Schema.SessionMessageUser Message(string id, long time) =>
        new(id, null, time, "user", id, null, null);
}
