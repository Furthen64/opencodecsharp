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

public sealed class ServerSessionCompactionTests
{
    [Fact]
    public async Task SummarizePersistsManualCheckpointAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"opencode-compaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var llm = new SummaryLLMClient();

        await using (var app = await StartServerAsync(directory, llm))
        {
            var store = app.Services.GetRequiredService<SessionStore>();
            await store.SetAsync(Session("ses_summary", directory));
            await store.AddMessageAsync("ses_summary", new Schema.SessionMessageUser(
                "msg_user", null, 100, "user", "Port the remaining endpoints", null, null));
            await store.AddMessageAsync("ses_summary", new Schema.SessionMessageAssistant(
                "msg_assistant", null, 101, "assistant", "build",
                new Schema.ModelRef("old-model", "old-provider", null),
                [new Schema.SessionMessageText("text", "txt_1", "Several routes are complete.")],
                null, "stop", 0, null, null, 102));

            using var client = new HttpClient { BaseAddress = ServerAddress(app) };
            using var response = await client.PostAsJsonAsync(
                "session/ses_summary/summarize",
                new { providerID = "summary-provider", modelID = "summary-model", auto = false });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(await response.Content.ReadFromJsonAsync<bool>());
            Assert.Equal("summary-provider/summary-model", Assert.Single(llm.Requests).Model);
            Assert.Contains("[User]: Port the remaining endpoints", Assert.Single(llm.Requests[0].System));
            Assert.Contains("[Assistant]: Several routes are complete.", Assert.Single(llm.Requests[0].System));

            var checkpoint = Assert.Single((await store.MessagesAsync("ses_summary"))
                .OfType<Schema.SessionMessageCompaction>());
            Assert.Equal("manual", checkpoint.Reason);
            Assert.Equal("## Objective\n- Finish route parity", checkpoint.Summary);
            Assert.Empty(checkpoint.Recent);
        }

        await using (var app = await StartServerAsync(directory, new SummaryLLMClient()))
        {
            var checkpoint = Assert.Single((await app.Services.GetRequiredService<SessionStore>()
                    .MessagesAsync("ses_summary"))
                .OfType<Schema.SessionMessageCompaction>());
            Assert.Equal("## Objective\n- Finish route parity", checkpoint.Summary);
        }

        Directory.Delete(directory, true);
    }

    private static async Task<WebApplication> StartServerAsync(string directory, ILLMClient llm)
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
        builder.Services.AddSingleton(llm);
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
        new Schema.SessionTime(100, 100, null), "Summarize",
        new Schema.LocationRef(directory, null), null, null);

    private sealed class SummaryLLMClient : ILLMClient
    {
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamEvent> StreamAsync(LLMRequest request)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new LLMStreamEvent(
                "text",
                "## Objective\n- Finish route parity",
                null,
                null,
                null,
                null);
        }
    }
}
