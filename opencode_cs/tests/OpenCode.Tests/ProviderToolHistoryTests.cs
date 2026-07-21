using System.Text.Json;
using OpenCode.Core;
using OpenCode.Core.AI;
using OpenCode.Core.AI.Providers;

namespace OpenCode.Tests;

public sealed class ProviderToolHistoryTests
{
    [Fact]
    public void OpenAIHistoryUsesAssistantToolCallsAndToolMessages()
    {
        using var body = Serialize(new TestOpenAI().Build(Request()));
        var messages = body.RootElement.GetProperty("messages");
        var call = messages[0].GetProperty("tool_calls")[0];
        Assert.Equal("call_1", call.GetProperty("id").GetString());
        Assert.Equal("lookup", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("tool", messages[1].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[1].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public void AnthropicHistoryUsesToolUseAndToolResultBlocks()
    {
        using var body = Serialize(new TestAnthropic().Build(Request()));
        var messages = body.RootElement.GetProperty("messages");
        var call = messages[0].GetProperty("content")[1];
        Assert.Equal("tool_use", call.GetProperty("type").GetString());
        Assert.Equal("call_1", call.GetProperty("id").GetString());
        var result = messages[1].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("call_1", result.GetProperty("tool_use_id").GetString());
        Assert.False(result.GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public void GoogleHistoryUsesFunctionCallAndFunctionResponseParts()
    {
        using var body = Serialize(new TestGoogle().Build(Request()));
        var contents = body.RootElement.GetProperty("contents");
        var call = contents[0].GetProperty("parts")[1].GetProperty("functionCall");
        Assert.Equal("lookup", call.GetProperty("name").GetString());
        var result = contents[1].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("lookup", result.GetProperty("name").GetString());
        Assert.Equal(42, result.GetProperty("response").GetProperty("answer").GetInt32());
    }

    private static LLMRequest Request() => new(
        "provider/model",
        [],
        [
            new LLMMessage("assistant", new LLMAssistantContent(
                "Checking.",
                [new LLMToolCall("call_1", "lookup", new Dictionary<string, object> { ["query"] = "answer" })])),
            new LLMMessage("tool", new LLMToolResultContent(
                "call_1",
                "lookup",
                new Dictionary<string, object> { ["answer"] = 42 },
                false)),
        ],
        [],
        null);

    private static JsonDocument Serialize(object value) => JsonDocument.Parse(
        JsonSerializer.Serialize(value, SerializerDefaults.JsonOptions));

    private static LanguageModelConfig Config() => new("test", null, [], []);

    private sealed class TestOpenAI() : OpenAILanguageModel(Config(), "model", "openai")
    {
        public object Build(LLMRequest request) => BuildRequestBody(request);
    }

    private sealed class TestAnthropic() : AnthropicLanguageModel(Config(), "model", "anthropic")
    {
        public object Build(LLMRequest request) => BuildRequestBody(request);
    }

    private sealed class TestGoogle() : GoogleLanguageModel(Config(), "model", "google")
    {
        public object Build(LLMRequest request) => BuildRequestBody(request);
    }
}
