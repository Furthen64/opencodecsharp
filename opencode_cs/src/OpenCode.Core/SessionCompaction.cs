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
}

public class SessionCompactionService : ISessionCompactionService
{
    readonly ILLMClient llm;
    readonly IEventService events;

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

    public SessionCompactionService(ILLMClient llm, IEventService events)
    {
        this.llm = llm;
        this.events = events;
    }

    public async Task<SessionCompactionResult> CompactIfNeededAsync(SessionCompactionInput input)
    {
        var config = new SessionCompactionSettings(true, DefaultBuffer, DefaultKeepTokens);
        var contextLimit = GetContextLimit(input.Model);
        if (contextLimit <= 0) return new SessionCompactionResult(false, null, null);

        var output = input.Request.System?.Length ?? 0;
        var selected = SelectEntries(input.Entries, config.KeepTokens);
        if (selected == null) return new SessionCompactionResult(false, null, null);

        var serialized = SerializeConversation(input.Entries);
        var totalTokens = EstimateTokens(serialized);
        if (totalTokens <= contextLimit - Math.Max(output, config.Buffer))
            return new SessionCompactionResult(false, null, null);

        return await CompactAfterOverflowAsync(input);
    }

    public async Task<SessionCompactionResult> CompactAfterOverflowAsync(SessionCompactionInput input)
    {
        var config = new SessionCompactionSettings(true, DefaultBuffer, DefaultKeepTokens);
        var contextLimit = GetContextLimit(input.Model);
        if (contextLimit <= 0) return new SessionCompactionResult(false, null, null);

        var output = input.Request.System?.Length ?? 0;
        var selected = SelectEntries(input.Entries, config.KeepTokens);
        var previousSummary = input.Entries
            .Where(e => e.Message is Schema.SessionMessageCompaction)
            .Select(e => (Schema.SessionMessageCompaction)e.Message)
            .LastOrDefault();

        if (selected == null && previousSummary == null)
            return new SessionCompactionResult(false, null, null);

        var summaryPrompt = BuildPrompt(previousSummary?.Summary, selected);
        var summaryOutput = Math.Min(output > 0 ? output : SummaryOutputTokens, SummaryOutputTokens);
        if (EstimateTokens(summaryPrompt) > contextLimit - summaryOutput)
            return new SessionCompactionResult(false, null, null);

        var messageId = "msg_" + Guid.NewGuid().ToString("N")[..12];
        await events.PublishAsync(
            new EventDefinition("session.compaction.started", true, "SessionId", 1),
            new { SessionId = input.SessionId, MessageId = messageId, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Reason = "auto" }
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
                if (@event.Text != null) chunks.Add(@event.Text);
            }
        }
        catch
        {
            failed = true;
        }

        var summary = string.Join("", chunks);
        if (failed || string.IsNullOrWhiteSpace(summary))
            return new SessionCompactionResult(false, null, null);

        var recent = selected != null ? SerializeRecent(input.Entries, selected.SplitIndex) : "";

        await events.PublishAsync(
            new EventDefinition("session.compaction.ended", true, "SessionId", 1),
            new
            {
                SessionId = input.SessionId,
                MessageId = messageId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Reason = "auto",
                Text = summary,
                Recent = recent
            }
        );

        return new SessionCompactionResult(true, summary, recent);
    }

    static string BuildPrompt(string? previousSummary, SelectedEntries? selected)
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
                if (remaining > 0 && index < conversation.Length)
                {
                    splitPrefix = conversation[index][..Math.Min(remaining, conversation[index].Length)];
                    splitSuffix = conversation[index][Math.Min(remaining, conversation[index].Length)..];
                    split = index + 1;
                }
                break;
            }
            total = next;
            split = index;
        }

        var headParts = conversation[..split];
        if (!string.IsNullOrEmpty(splitPrefix)) headParts = headParts.Append(splitPrefix).ToArray();
        var recentParts = conversation[split..];
        if (!string.IsNullOrEmpty(splitSuffix)) recentParts = recentParts.Prepend(splitSuffix).ToArray();

        return new SelectedEntries(
            Head: string.Join("\n\n", headParts.Where(s => !string.IsNullOrEmpty(s))),
            Recent: string.Join("\n\n", recentParts.Where(s => !string.IsNullOrEmpty(s))),
            SplitIndex: split
        );
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

    static string SerializeRecent(SessionCompactionEntry[] entries, int splitIndex)
    {
        var conversation = entries
            .Where(e => e.Message is not Schema.SessionMessageCompaction)
            .Select(e => SerializeMessage(e.Message))
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
        return string.Join("\n\n", conversation.Skip(splitIndex));
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

    record SelectedEntries(string Head, string Recent, int SplitIndex);
}
