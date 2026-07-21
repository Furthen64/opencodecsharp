using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Server;

namespace OpenCode.Tests;

public sealed class ServerFileTests
{
    [Fact]
    public async Task FileEndpointsListReadAndSearchWithinServerDirectory()
    {
        var directory = CreateDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        await File.WriteAllTextAsync(Path.Combine(directory, "src", "Example.cs"), "class Example {}\n");
        await File.WriteAllBytesAsync(Path.Combine(directory, "image.bin"), [0, 1, 2, 3]);
        await using var app = await StartServerAsync(directory);
        using var client = new HttpClient { BaseAddress = ServerAddress(app) };

        var nodes = await client.GetFromJsonAsync<FileNodeDto[]>("file?path=.");
        Assert.Equal(["src", "image.bin"], nodes!.Select(node => node.Name));

        var text = await client.GetFromJsonAsync<FileContentDto>("file/content?path=src/Example.cs");
        Assert.Equal("text", text!.Type);
        Assert.Equal("class Example {}", text.Content);

        var binary = await client.GetFromJsonAsync<FileContentDto>("file/content?path=image.bin");
        Assert.Equal("binary", binary!.Type);
        Assert.Equal("base64", binary.Encoding);

        var files = await client.GetFromJsonAsync<string[]>("find/file?query=example&type=file&limit=5");
        Assert.Equal(["src/Example.cs"], files!);

        using var matchesResponse = await client.GetAsync("find?pattern=class%20Example");
        using var matches = JsonDocument.Parse(await matchesResponse.Content.ReadAsStreamAsync());
        var match = Assert.Single(matches.RootElement.EnumerateArray());
        Assert.Equal("src/Example.cs", match.GetProperty("path").GetProperty("text").GetString());
        Assert.Equal(1, match.GetProperty("lineNumber").GetInt32());

        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task FileEndpointsRejectEscapesInvalidPatternsAndLimits()
    {
        var directory = CreateDirectory();
        var outside = Path.Combine(Path.GetTempPath(), $"opencode-outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(directory, "outside-link.txt"), outside);
        await using var app = await StartServerAsync(directory);
        using var client = new HttpClient { BaseAddress = ServerAddress(app) };

        using var escape = await client.GetAsync("file/content?path=../outside.txt");
        Assert.Equal(HttpStatusCode.BadRequest, escape.StatusCode);

        using var symlinkEscape = await client.GetAsync("file/content?path=outside-link.txt");
        Assert.Equal(HttpStatusCode.BadRequest, symlinkEscape.StatusCode);

        using var pattern = await client.GetAsync("find?pattern=%5B");
        Assert.Equal(HttpStatusCode.BadRequest, pattern.StatusCode);

        using var limit = await client.GetAsync("find/file?query=x&limit=201");
        Assert.Equal(HttpStatusCode.BadRequest, limit.StatusCode);

        using var type = await client.GetAsync("find/file?query=x&type=other");
        Assert.Equal(HttpStatusCode.BadRequest, type.StatusCode);

        Directory.Delete(directory, true);
        File.Delete(outside);
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"opencode-file-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
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

    private record FileNodeDto(string Name, string Path, string Absolute, string Type, bool Ignored);
    private record FileContentDto(string Type, string Content, string? Encoding, string? MimeType);
}
