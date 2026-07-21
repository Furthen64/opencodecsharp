using System.Text.Json;
using OpenCode.Client;

var serverUrl = ParseServerUrl(args);
using var client = new OpenCodeHttpClient(serverUrl);

if (!await client.IsHealthyAsync())
{
    Console.Error.WriteLine($"OpenCode server is unavailable at {serverUrl}.");
    Console.Error.WriteLine("Start OpenCode.Server first, or pass --server http://host:port.");
    return 1;
}

Console.WriteLine("OpenCode TUI (minimal)");
Console.WriteLine($"Connected to {serverUrl}");
Console.WriteLine("Commands: /new, /quit");

var session = await client.CreateSessionAsync();
Console.WriteLine($"Session: {session.Id}\n");

while (true)
{
    Console.Write("you> ");
    var input = Console.ReadLine();
    if (input is null || input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        break;
    if (string.IsNullOrWhiteSpace(input))
        continue;
    if (input.Equals("/new", StringComparison.OrdinalIgnoreCase))
    {
        session = await client.CreateSessionAsync();
        Console.WriteLine($"Started session: {session.Id}\n");
        continue;
    }

    try
    {
        await client.PromptAsync(session.Id, input);
        Console.Write("assistant> ");
        var response = await WaitForAssistantAsync(client, session.Id);
        Console.WriteLine(response);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Request failed: {ex.Message}");
    }
}

return 0;

static string ParseServerUrl(string[] args)
{
    var index = Array.FindIndex(args, argument => argument.Equals("--server", StringComparison.OrdinalIgnoreCase));
    if (index >= 0 && index + 1 < args.Length)
        return args[index + 1];
    return Environment.GetEnvironmentVariable("OPENCODE_SERVER_URL") ?? "http://127.0.0.1:5179";
}

static async Task<string> WaitForAssistantAsync(OpenCodeHttpClient client, string sessionId)
{
    const int attempts = 150;
    for (var attempt = 0; attempt < attempts; attempt++)
    {
        var messages = await client.MessagesAsync(sessionId);
        if (TryGetCompletedAssistant(messages, out var text, out var error))
            return error ?? text ?? "(assistant completed without text)";

        await Task.Delay(200);
    }

    return "(timed out waiting for the assistant; use the server event stream for live progress)";
}

static bool TryGetCompletedAssistant(JsonElement response, out string? text, out string? error)
{
    text = null;
    error = null;
    if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        return false;

    JsonElement? assistant = null;
    foreach (var message in data.EnumerateArray())
    {
        if (message.TryGetProperty("type", out var type) && type.GetString() == "assistant")
            assistant = message;
    }
    if (assistant is null || !assistant.Value.TryGetProperty("completed", out _))
        return false;

    if (assistant.Value.TryGetProperty("error", out var errorElement) &&
        errorElement.TryGetProperty("message", out var errorMessage))
    {
        error = $"Error: {errorMessage.GetString()}";
        return true;
    }

    if (assistant.Value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
    {
        var assembled = string.Concat(content.EnumerateArray()
            .Where(part => part.TryGetProperty("text", out _))
            .Select(part => part.GetProperty("text").GetString()));
        text = string.IsNullOrWhiteSpace(assembled) ? null : assembled;
    }
    return true;
}
