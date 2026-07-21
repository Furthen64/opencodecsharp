using OpenCode.Core;
using Schema = OpenCode.Schema;

namespace OpenCode.Tests;

public sealed class SessionRunnerTests
{
    [Fact]
    public async Task StaleRunningToolIsFailedAndIncludedInRecoveryContext()
    {
        var store = new SessionStore();
        await store.SetAsync(Session("ses_recovery"));
        await store.AddMessageAsync("ses_recovery", new Schema.SessionMessageUser(
            "msg_user", null, 1, "user", "Continue", null, null));
        await store.AddMessageAsync("ses_recovery", new Schema.SessionMessageAssistant(
            "msg_interrupted",
            null,
            2,
            "assistant",
            "build",
            new Schema.ModelRef("test-model", "test", null),
            [new Schema.SessionMessageTool(
                "tool",
                "call_interrupted",
                "lookup",
                new Schema.SessionMessageProviderInfo(false, null, null),
                new Schema.ToolStateRunning(
                    "running",
                    new Dictionary<string, object> { ["query"] = "answer" },
                    [],
                    []),
                2,
                2,
                null,
                null)],
            null,
            "tool-calls",
            null,
            null,
            null,
            null));
        var llm = new TextOnlyLLMClient();
        using var events = new EventService();
        var runner = new SessionRunner(
            events,
            llm,
            new AgentService(),
            new ToolRegistry(),
            new FixedModelResolver(),
            store);

        await runner.RunAsync("ses_recovery", true);

        Assert.True(llm.Request is not null,
            System.Text.Json.JsonSerializer.Serialize(await events.ReplayAsync("ses_recovery")));
        var recovered = Assert.IsType<LLMToolResultContent>(
            Assert.Single(llm.Request.Messages, message => message.Role == "tool").Content);
        Assert.True(recovered.IsError);
        var stored = Assert.Single((await store.MessagesAsync("ses_recovery"))
            .OfType<Schema.SessionMessageAssistant>()
            .SelectMany(message => message.Content)
            .OfType<Schema.SessionMessageTool>());
        var error = Assert.IsType<Schema.ToolStateError>(stored.State);
        Assert.Equal("interrupted", error.Error.Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolSettlementIsPersistedAndProvidedToTheContinuationTurn(bool failTool)
    {
        var store = new SessionStore();
        await store.SetAsync(Session("ses_runner"));
        await store.AddMessageAsync("ses_runner", new Schema.SessionMessageUser(
            "msg_user", null, 1, "user", "Look up the answer", null, null));

        var tools = new ToolRegistry();
        var lookup = new LookupTool(failTool);
        await tools.RegisterAsync("lookup", lookup);
        var llm = new ContinuationLLMClient();
        using var events = new EventService();
        var runner = new SessionRunner(
            events,
            llm,
            new AgentService(),
            tools,
            new FixedModelResolver(),
            store);

        await runner.RunAsync("ses_runner", true);

        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(1, lookup.Executions);
        var continuation = llm.Requests[1].Messages;
        var assistantContext = Assert.IsType<LLMAssistantContent>(
            Assert.Single(continuation, message => message.Role == "assistant").Content);
        var call = Assert.Single(assistantContext.ToolCalls);
        Assert.Equal("call_lookup", call.Id);
        Assert.Equal("lookup", call.Name);
        var result = Assert.IsType<LLMToolResultContent>(
            Assert.Single(continuation, message => message.Role == "tool").Content);
        Assert.Equal("call_lookup", result.CallId);
        Assert.Equal(failTool, result.IsError);

        var assistants = (await store.MessagesAsync("ses_runner"))
            .OfType<Schema.SessionMessageAssistant>()
            .ToArray();
        Assert.Equal(2, assistants.Length);
        Assert.Equal("tool-calls", assistants[0].Finish);
        var storedTool = Assert.Single(assistants[0].Content.OfType<Schema.SessionMessageTool>());
        if (failTool)
            Assert.IsType<Schema.ToolStateError>(storedTool.State);
        else
            Assert.IsType<Schema.ToolStateCompleted>(storedTool.State);
        Assert.Equal("stop", assistants[1].Finish);
        Assert.Equal("The answer is 42.",
            Assert.Single(assistants[1].Content.OfType<Schema.SessionMessageText>()).Text);
    }

    [Fact]
    public async Task AssistantStepPersistsSnapshotBoundaryAndChangedFiles()
    {
        var store = new SessionStore();
        await store.SetAsync(Session("ses_snapshot"));
        await store.AddMessageAsync("ses_snapshot", new Schema.SessionMessageUser(
            "msg_user", null, 1, "user", "Edit the file", null, null));
        var snapshots = new SequenceSnapshotService("tree_before", "tree_after");
        using var events = new EventService();
        var runner = new SessionRunner(
            events,
            new TextOnlyLLMClient(),
            new AgentService(),
            new ToolRegistry(),
            new FixedModelResolver(),
            store,
            snapshots);

        await runner.RunAsync("ses_snapshot", true);

        var assistant = Assert.Single((await store.MessagesAsync("ses_snapshot"))
            .OfType<Schema.SessionMessageAssistant>());
        Assert.NotNull(assistant.Snapshot);
        Assert.Equal("tree_before", assistant.Snapshot.Start);
        Assert.Equal("tree_after", assistant.Snapshot.End);
        Assert.Equal(["src/file.cs"], assistant.Snapshot.Files!);
    }

    [Fact]
    public async Task OversizedHistoryIsCompactedBeforeProviderTurnAndCheckpointReplacesOldContext()
    {
        var store = new SessionStore();
        await store.SetAsync(Session("ses_compaction"));
        var history = "Earlier context " + new string('x', 740_000);
        await store.AddMessageAsync("ses_compaction", new Schema.SessionMessageUser(
            "msg_large", null, 1, "user", history, null, null));
        var llm = new CompactionLLMClient();
        using var events = new EventService();
        var compaction = new SessionCompactionService(llm, events, store);
        var runner = new SessionRunner(
            events,
            llm,
            new AgentService(),
            new ToolRegistry(),
            new FixedModelResolver(),
            store,
            null,
            compaction);

        await runner.RunAsync("ses_compaction", true);

        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains("## Objective", Assert.Single(llm.Requests[0].System));
        var checkpoint = Assert.IsType<string>(Assert.Single(llm.Requests[1].Messages).Content);
        Assert.Contains("<conversation-checkpoint>", checkpoint);
        Assert.Contains("## Objective\n- Preserve the task", checkpoint);
        var stored = await store.MessagesAsync("ses_compaction");
        var compacted = Assert.Single(stored.OfType<Schema.SessionMessageCompaction>());
        Assert.Equal("auto", compacted.Reason);
        Assert.NotEmpty(compacted.Recent);
        Assert.Equal("Continued.", Assert.Single(stored
            .OfType<Schema.SessionMessageAssistant>()
            .SelectMany(message => message.Content)
            .OfType<Schema.SessionMessageText>()).Text);
    }

    private static Schema.SessionInfo Session(string id) => new(
        id,
        null,
        "project",
        null,
        new Schema.ModelRef("test-model", "test", null),
        0,
        new Schema.SessionTokens(0, 0, 0, new Schema.SessionCacheTokens(0, 0)),
        new Schema.SessionTime(1, 1, null),
        id,
        new Schema.LocationRef(Path.GetTempPath(), null),
        null,
        null);

    private sealed class FixedModelResolver : IModelResolver
    {
        public Task<string> ResolveAsync(Schema.SessionInfo session) => Task.FromResult("test/test-model");
    }

    private sealed class ContinuationLLMClient : ILLMClient
    {
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamEvent> StreamAsync(LLMRequest request)
        {
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count == 1)
            {
                yield return new LLMStreamEvent(
                    "tool_call",
                    null,
                    new LLMToolCall("call_lookup", "lookup", new Dictionary<string, object> { ["query"] = "answer" }),
                    null,
                    new LLMUsage(10, 2, null),
                    null);
                yield break;
            }

            yield return new LLMStreamEvent("text", "The answer is 42.", null, null, new LLMUsage(15, 5, null), null);
        }
    }

    private sealed class TextOnlyLLMClient : ILLMClient
    {
        public LLMRequest? Request { get; private set; }

        public async IAsyncEnumerable<LLMStreamEvent> StreamAsync(LLMRequest request)
        {
            Request = request;
            await Task.Yield();
            yield return new LLMStreamEvent("text", "Recovered.", null, null, null, null);
        }
    }

    private sealed class CompactionLLMClient : ILLMClient
    {
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamEvent> StreamAsync(LLMRequest request)
        {
            Requests.Add(request);
            await Task.Yield();
            var text = request.System.Any(part => part.Contains("<template>", StringComparison.Ordinal))
                ? "## Objective\n- Preserve the task"
                : "Continued.";
            yield return new LLMStreamEvent("text", text, null, null, null, null);
        }
    }

    private sealed class LookupTool(bool fail) : Tool("lookup", "Looks up an answer")
    {
        public int Executions { get; private set; }

        public override Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
        {
            Executions++;
            if (fail)
                throw new ToolFailure("Lookup failed");
            return Task.FromResult(new ToolOutput(
                [new ToolTextContent { Text = "42" }],
                new Dictionary<string, object> { ["answer"] = 42 }));
        }

        public override ToolDefinition ToDefinition(string name) =>
            new(name, Description, new { type = "object" }, null);
    }

    private sealed class SequenceSnapshotService(params string[] ids) : ISnapshotService
    {
        private int index;

        public Task<SnapshotId?> CaptureAsync() => Task.FromResult<SnapshotId?>(
            SnapshotId.Make(ids[Math.Min(index++, ids.Length - 1)]));
        public Task<string[]> FilesAsync(SnapshotId from, SnapshotId to) =>
            Task.FromResult(new[] { "src/file.cs" });
        public Task<Schema.FileDiff[]> DiffAsync(SnapshotId from, SnapshotId to, int context = 3, string[]? paths = null) =>
            Task.FromResult(Array.Empty<Schema.FileDiff>());
        public Task<Schema.FileDiff[]> PreviewAsync(SnapshotId current, Dictionary<string, SnapshotId> files, int context = 3) =>
            Task.FromResult(Array.Empty<Schema.FileDiff>());
        public Task RestoreAsync(Dictionary<string, SnapshotId> files) => Task.CompletedTask;
        public Task CheckoutAsync(SnapshotId snapshot) => Task.CompletedTask;
    }
}
