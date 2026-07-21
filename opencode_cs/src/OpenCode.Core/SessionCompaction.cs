using System.Text;

namespace OpenCode.Core;

public record SessionCompactionSettings(
    bool Auto,
    int Buffer,
    int KeepTokens
);

public record SessionCompactionInput(
    string SessionId,
    SessionCompactionEntry[] Entries,
    Schema.ModelRef Model,
    LLMRequest Request
);

public record SessionCompactionEntry(
    int Seq,
    Schema.SessionMessageBase Message
);

public record SessionCompactionResult(
    bool Compacted,
    string? Summary,
    string? Recent
);

public interface ISessionCompactionService
{
    Task<SessionCompactionResult> CompactIfNeededAsync(SessionCompactionInput input);
    Task<SessionCompactionResult> CompactAfterOverflowAsync(SessionCompactionInput input);
    Task<SessionCompactionResult> CompactManualAsync(
        string sessionId,
        Schema.ModelRef model,
        bool auto = false);
}

public class SessionCompactionService : ISessionCompactionService
{
    readonly ILLMClient llm;
    readonly IEventService events;
    readonly SessionStore store;

    const int DefaultBuffer = 20_000;
    const int DefaultKeepTokens = 8_000;
    const int ToolOutputMaxChars = 2_000;
    const int SummaryOutputTokens = 4_096;

    const string SummaryTemplate = @"Output exactly the Markdown structure shown inside <template> and keep the section order unchanged. Do not include the <template> tags in your response.
<template>
## Objective
- [one or two brief sentences describing what the user is trying to accomplish]

## Important Details
- [constraints/preferences, decisions and why, important facts/assumptions, exact context needed to continue, or ""(none)""]

## Work State
### Completed
- [finished work, verified facts, or changes made; otherwise ""(none)""]

### Active
- [current work, partial changes, or investigation state; otherwise ""(none)""]

### Blocked
- [blockers, failing commands, or unknowns; otherwise ""(none)""]

## Next Move
1. [immediate concrete action, or ""(none)""]
2. [next action if known, or ""(none)""]

## Relevant Files
- [file or directory path: why it matters, or ""(none)""]
</template>

Rules:
- Keep every section, even when empty.
- Use terse bullets, not prose paragraphs.
- Preserve exact file paths, symbols, commands, error strings, URLs, and identifiers when known.
- Do not mention the summary process or that context was compacted.";

    public SessionCompactionService(ILLMClient llm, IEventService events, SessionStore store)
    {
        this.llm = llm;
        this.events = events;
        this.store = store;
    }

    public async Task<SessionCompactionResult> CompactIfNeededAsync(SessionCompactionInput input)
    {
        var config = new SessionCompactionSettings(true, DefaultBuffer, DefaultKeepTokens);
        var contextLimit = GetContextLimit(input.Model);
        if (contextLimit <= 0) return new SessionCompactionResult(false, null, null);

        var output = SummaryOutputTokens;
        var selected = SelectEntries(input.Entries, config.KeepTokens);
        if (selected == null) return new SessionCompactionResult(false, null, null);

        var serialized = SerializeConversation(input.Entries);
        var totalTokens = EstimateTokens(serialized);
        if (totalTokens <= contextLimit - Math.Max(output, config.Buffer))
            return new SessionCompactionResult(false, null, null);

        return await CompactAsync(input, "auto", null);
    }

    public Task<SessionCompactionResult> CompactAfterOverflowAsync(SessionCompactionInput input) =>
        CompactAsync(input, "auto", null);

    public async Task<SessionCompactionResult> CompactManualAsync(
        string sessionId,
        Schema.ModelRef model,
        bool auto = false)
    {
        var messages = LatestContext(await store.MessagesAsync(sessionId));
        var entries = messages.Select((message, index) => new SessionCompactionEntry(index + 1, message)).ToArray();
        var request = new LLMRequest(
            $"{model.ProviderId}/{model.Id}",
            [],
            [],
            [],
            null);
        var result = await CompactAsync(
            new SessionCompactionInput(sessionId, entries, model, request),
            "manual",
            SelectAll(entries));
        return result;
    }

    private async Task<SessionCompactionResult> CompactAsync(
        SessionCompactionInput input,
        string reason,
        SelectedEntries? selection)
    {
        var config = new SessionCompactionSettings(true, DefaultBuffer, DefaultKeepTokens);
        var contextLimit = GetContextLimit(input.Model);
        if (contextLimit <= 0) return new SessionCompactionResult(false, null, null);

        var output = SummaryOutputTokens;
        var selected = selection ?? SelectEntries(input.Entries, config.KeepTokens);
        var previousSummary = input.Entries
            .Where(e => e.Message is Schema.SessionMessageCompaction)
            .Select(e => (Schema.SessionMessageCompaction)e.Message)
            .LastOrDefault();

        if (selected == null && previousSummary == null)
            return new SessionCompactionResult(false, null, null);

        var summaryPrompt = BuildPrompt(previousSummary?.Summary, previousSummary?.Recent, selected);
        var summaryOutput = Math.Min(output > 0 ? output : SummaryOutputTokens, SummaryOutputTokens);
        if (EstimateTokens(summaryPrompt) > contextLimit - summaryOutput)
            return new SessionCompactionResult(false, null, null);

        var messageId = Schema.MessageId.Create();
        await events.PublishAsync(
            new EventDefinition("session.next.compaction.started", true, "SessionId", 1),
            new { SessionId = input.SessionId, MessageId = messageId, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Reason = reason }
        );

        var chunks = new List<string>();
        bool failed = false;
        try
        {
            var request = new LLMRequest(
                Model: input.Request.Model,
                System: new[] { summaryPrompt },
                Messages: Array.Empty<LLMMessage>(),
                Tools: null,
                ToolChoice: null
            );

            await foreach (var @event in llm.StreamAsync(request))
            {
                if (@event.Error != null) failed = true;
                if (@event.Text != null)
                {
                    chunks.Add(@event.Text);
                    await events.PublishAsync(
                        new EventDefinition("session.next.compaction.delta", false, "SessionId", 1),
                        new { SessionId = input.SessionId, MessageId = messageId, Text = @event.Text });
                }
            }
        }
        catch
        {
            failed = true;
        }

        var summary = string.Join("", chunks);
        if (failed || string.IsNullOrWhiteSpace(summary))
            return new SessionCompactionResult(false, null, null);

        var recent = selected?.Recent ?? "";

        await store.AddMessageAsync(input.SessionId, new Schema.SessionMessageCompaction(
            messageId,
            null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "compaction",
            reason,
            summary,
            recent));

        await events.PublishAsync(
            new EventDefinition("session.next.compaction.ended", true, "SessionId", 1),
            new
            {
                SessionId = input.SessionId,
                MessageId = messageId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Reason = reason,
                Text = summary,
                Recent = recent
            }
        );

        return new SessionCompactionResult(true, summary, recent);
    }

    static string BuildPrompt(string? previousSummary, string? previousRecent, SelectedEntries? selected)
    {
        var parts = new List<string>();
        if (previousSummary != null)
        {
            parts.Add($"Update the anchored summary below using the conversation history above.\nPreserve still-true details, remove stale details, and merge in the new facts.\n<previous-summary>\n{previousSummary}\n</previous-summary>");
        }
        else
        {
            parts.Add("Create a new anchored summary from the conversation history.");
        }
        parts.Add(SummaryTemplate);
        if (!string.IsNullOrEmpty(previousRecent)) parts.Add(previousRecent);
        if (selected != null)
        {
            if (!string.IsNullOrEmpty(selected.Head)) parts.Add(selected.Head);
        }
        return string.Join("\n\n", parts);
    }

    static SelectedEntries? SelectEntries(SessionCompactionEntry[] entries, int tokens)
    {
        var conversation = entries
            .Where(e => e.Message is not Schema.SessionMessageCompaction)
            .Select(e => SerializeMessage(e.Message))
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
        if (conversation.Length == 0) return null;

        int total = 0;
        int split = conversation.Length;
        string splitPrefix = "";
        string splitSuffix = "";

        for (int index = conversation.Length - 1; index >= 0; index--)
        {
            int next = total + EstimateTokens(conversation[index]);
            if (next > tokens)
            {
                int remaining = Math.Max(0, tokens - total) * 4;
                if (remaining > 0)
                {
                    var cut = Math.Max(0, conversation[index].Length - remaining);
                    splitPrefix = conversation[index][..cut];
                    splitSuffix = conversation[index][cut..];
                }
                split = index;
                break;
            }
            total = next;
            split = index;
        }

        var headParts = conversation[..split];
        if (!string.IsNullOrEmpty(splitPrefix)) headParts = headParts.Append(splitPrefix).ToArray();
        var recentParts = conversation[Math.Min(split + (splitSuffix.Length > 0 ? 1 : 0), conversation.Length)..];
        if (!string.IsNullOrEmpty(splitSuffix)) recentParts = recentParts.Prepend(splitSuffix).ToArray();

        return new SelectedEntries(
            Head: string.Join("\n\n", headParts.Where(s => !string.IsNullOrEmpty(s))),
            Recent: string.Join("\n\n", recentParts.Where(s => !string.IsNullOrEmpty(s)))
        );
    }

    static SelectedEntries? SelectAll(SessionCompactionEntry[] entries)
    {
        var head = SerializeConversation(entries);
        return string.IsNullOrWhiteSpace(head) ? null : new SelectedEntries(head, "");
    }

    static string SerializeMessage(Schema.SessionMessageBase message)
    {
        return message switch
        {
            Schema.SessionMessageUser user => $"[User]: {user.Text}",
            Schema.SessionMessageAssistant assistant => SerializeAssistant(assistant),
            Schema.SessionMessageSystem system => $"[System update]: {system.Text}",
            Schema.SessionMessageSynthetic synthetic => $"[Synthetic context]: {synthetic.Text}",
            Schema.SessionMessageShell shell => $"[Shell]: {shell.Command}\n{Truncate(shell.Output)}",
            _ => ""
        };
    }

    static string SerializeAssistant(Schema.SessionMessageAssistant assistant)
    {
        var parts = new List<string>();
        foreach (var content in assistant.Content)
        {
            if (content is Schema.SessionMessageText text)
                parts.Add($"[Assistant]: {text.Text}");
            else if (content is Schema.SessionMessageReasoning reasoning && !string.IsNullOrEmpty(reasoning.Text))
                parts.Add($"[Assistant reasoning]: {reasoning.Text}");
            else if (content is Schema.SessionMessageTool tool)
            {
                var input = tool.State switch
                {
                    Schema.ToolStatePending pending => pending.Input,
                    Schema.ToolStateRunning running => System.Text.Json.JsonSerializer.Serialize(running.Input),
                    _ => ""
                };
                if (tool.State is Schema.ToolStateCompleted completed)
                {
                    parts.Add($"[Assistant tool call]: {tool.Name}({input})");
                    parts.Add($"[Tool result]: {Truncate(SerializeToolContent(completed.Content))}");
                }
                else if (tool.State is Schema.ToolStateError error)
                {
                    parts.Add($"[Assistant tool call]: {tool.Name}({input})");
                    parts.Add($"[Tool error]: {error.Error.Message}");
                }
                else
                {
                    parts.Add($"[Assistant tool call]: {tool.Name}({input})");
                }
            }
        }
        return string.Join("\n", parts);
    }

    static string SerializeToolContent(Schema.ToolContent[] content)
    {
        return string.Join("\n", content.Select(c =>
        {
            if (c is Schema.ToolTextContent text)
                return text.Text ?? "";
            if (c is Schema.ToolFileContent file)
                return $"[Attached {file.Mime}{(file.Name != null ? $": {file.Name}" : "")}]";
            return "";
        }));
    }

    static string SerializeConversation(SessionCompactionEntry[] entries)
    {
        return string.Join("\n\n", entries
            .Where(e => e.Message is not Schema.SessionMessageCompaction)
            .Select(e => SerializeMessage(e.Message))
            .Where(s => !string.IsNullOrEmpty(s)));
    }

    internal static List<Schema.SessionMessageBase> LatestContext(List<Schema.SessionMessageBase> messages)
    {
        var compaction = messages.FindLastIndex(message => message is Schema.SessionMessageCompaction);
        return compaction < 0 ? messages : messages.Skip(compaction).ToList();
    }

    static int GetContextLimit(Schema.ModelRef model)
    {
        return 200_000;
    }

    static int EstimateTokens(string text)
    {
        return (text.Length + 3) / 4;
    }

    static string Truncate(string value) =>
        value.Length <= ToolOutputMaxChars ? value : value[..ToolOutputMaxChars] + "\n[truncated]";

    record SelectedEntries(string Head, string Recent);
}
