namespace OpenCode.Core;

public interface ISessionRevertService
{
    Task<Schema.FileDiff[]> DiffAsync(string sessionId, string? messageId = null);
    Task<Schema.SessionInfo> RevertAsync(string sessionId, string messageId, string? partId = null);
    Task<Schema.SessionInfo> UnrevertAsync(string sessionId);
    Task CommitAsync(string sessionId);
}

public sealed class SessionRevertService : ISessionRevertService
{
    private readonly ISnapshotService snapshots;
    private readonly SessionStore store;
    private readonly ISessionExecution execution;
    private readonly IEventService events;

    public SessionRevertService(
        ISnapshotService snapshots,
        SessionStore store,
        ISessionExecution execution,
        IEventService events)
    {
        this.snapshots = snapshots;
        this.store = store;
        this.execution = execution;
        this.events = events;
    }

    public async Task<Schema.FileDiff[]> DiffAsync(string sessionId, string? messageId = null)
    {
        await RequireSessionAsync(sessionId);
        if (messageId is null) return [];

        var messages = await store.MessagesAsync(sessionId);
        var target = messages.FindIndex(message => message.Id == messageId);
        if (target < 0) return [];

        var boundarySnapshots = new List<Schema.SessionMessageSnapshot>();
        for (var index = target; index < messages.Count; index++)
        {
            if (index > target && messages[index] is Schema.SessionMessageUser)
                break;
            if (messages[index] is Schema.SessionMessageAssistant { Snapshot: not null } assistant)
                boundarySnapshots.Add(assistant.Snapshot);
        }

        var from = boundarySnapshots.FirstOrDefault(item => item.Start is not null)?.Start;
        var to = boundarySnapshots.LastOrDefault(item => item.End is not null)?.End;
        return from is null || to is null
            ? []
            : await DiffSnapshotsAsync(from, to);
    }

    public async Task<Schema.SessionInfo> RevertAsync(
        string sessionId,
        string messageId,
        string? partId = null)
    {
        var session = await RequireSessionAsync(sessionId);
        await AssertNotBusyAsync(sessionId);
        var messages = await store.MessagesAsync(sessionId);
        var target = FindTarget(messages, messageId, partId);
        if (target < 0) return session;

        SnapshotId? original;
        if (session.Revert?.Snapshot is { } existing)
        {
            original = SnapshotId.Make(existing);
            await snapshots.CheckoutAsync(original.Value);
        }
        else
        {
            original = await snapshots.CaptureAsync();
        }

        var restore = new Dictionary<string, SnapshotId>(StringComparer.Ordinal);
        for (var index = target; index < messages.Count; index++)
        {
            if (messages[index] is not Schema.SessionMessageAssistant { Snapshot: { Start: not null } state })
                continue;
            var files = state.Files;
            if (files is null && state.End is not null)
                files = await snapshots.FilesAsync(SnapshotId.Make(state.Start), SnapshotId.Make(state.End));
            foreach (var file in files ?? [])
                restore.TryAdd(file, SnapshotId.Make(state.Start));
        }

        if (restore.Count > 0)
            await snapshots.RestoreAsync(restore);

        Schema.FileDiff[] filesDiff = [];
        string? patch = null;
        if (original is not null)
        {
            var current = await snapshots.CaptureAsync();
            if (current is not null)
            {
                filesDiff = await snapshots.DiffAsync(original.Value, current.Value);
                patch = string.Join('\n', filesDiff.Select(file => file.Patch)).Trim();
            }
        }

        var boundaryMessageId = partId is null && messages[target] is Schema.SessionMessageAssistant
            ? messages.Take(target).OfType<Schema.SessionMessageUser>().LastOrDefault()?.Id ?? messageId
            : messageId;
        var updated = session with
        {
            Revert = new Schema.RevertState(boundaryMessageId, partId, original?.Value, patch, filesDiff),
            Time = session.Time with { Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        };
        await store.SetAsync(updated);
        await PublishUpdatedAsync(updated, filesDiff);
        return updated;
    }

    public async Task<Schema.SessionInfo> UnrevertAsync(string sessionId)
    {
        var session = await RequireSessionAsync(sessionId);
        await AssertNotBusyAsync(sessionId);
        if (session.Revert is null) return session;

        if (session.Revert.Snapshot is { } snapshot)
            await snapshots.CheckoutAsync(SnapshotId.Make(snapshot));
        var updated = session with
        {
            Revert = null,
            Time = session.Time with { Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        };
        await store.SetAsync(updated);
        await PublishUpdatedAsync(updated, []);
        return updated;
    }

    public async Task CommitAsync(string sessionId)
    {
        var session = await RequireSessionAsync(sessionId);
        if (session.Revert is not { } revert) return;

        var messages = await store.MessagesAsync(sessionId);
        var boundary = messages.FindIndex(message => message.Id == revert.MessageId);
        if (boundary >= 0 && revert.PartId is not null &&
            messages[boundary] is Schema.SessionMessageAssistant assistant)
        {
            var part = Array.FindIndex(assistant.Content, content => content switch
            {
                Schema.SessionMessageTool tool => tool.Id == revert.PartId,
                Schema.SessionMessageText text => text.Id == revert.PartId,
                Schema.SessionMessageReasoning reasoning => reasoning.Id == revert.PartId,
                _ => false,
            });
            if (part >= 0)
                await store.ReplaceMessageAsync(
                    sessionId,
                    assistant.Id,
                    assistant with { Content = assistant.Content.Take(part).ToArray() });
            boundary++;
        }

        if (boundary >= 0)
        {
            foreach (var message in messages.Skip(boundary))
            {
                await store.RemoveMessageAsync(sessionId, message.Id);
                await events.PublishAsync(
                    new EventDefinition("session.message.removed", true, "SessionId", 1),
                    new { SessionId = sessionId, MessageId = message.Id });
            }
        }

        var updated = session with
        {
            Revert = null,
            Time = session.Time with { Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        };
        await store.SetAsync(updated);
        await PublishUpdatedAsync(updated, []);
    }

    private async Task<Schema.FileDiff[]> DiffSnapshotsAsync(string from, string to) =>
        await snapshots.DiffAsync(SnapshotId.Make(from), SnapshotId.Make(to));

    private async Task<Schema.SessionInfo> RequireSessionAsync(string sessionId) =>
        await store.GetAsync(sessionId) ?? throw new SessionNotFoundError(sessionId);

    private async Task AssertNotBusyAsync(string sessionId)
    {
        if ((await execution.ActiveAsync()).Contains(sessionId))
            throw new SessionBusyError(sessionId);
    }

    private async Task PublishUpdatedAsync(Schema.SessionInfo session, Schema.FileDiff[] diff)
    {
        await events.PublishAsync(
            new EventDefinition("session.updated", true, "SessionId", 1),
            new { SessionId = session.Id, Info = session });
        await events.PublishAsync(
            new EventDefinition("session.diff", true, "SessionId", 1),
            new { SessionId = session.Id, Diff = diff });
    }

    private static int FindTarget(
        IReadOnlyList<Schema.SessionMessageBase> messages,
        string messageId,
        string? partId)
    {
        if (partId is null)
            return messages.ToList().FindIndex(message => message.Id == messageId);
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index].Id != messageId || messages[index] is not Schema.SessionMessageAssistant assistant)
                continue;
            if (assistant.Content.Any(content => content switch
                {
                    Schema.SessionMessageTool tool => tool.Id == partId,
                    Schema.SessionMessageText text => text.Id == partId,
                    Schema.SessionMessageReasoning reasoning => reasoning.Id == partId,
                    _ => false,
                }))
                return index;
        }
        return -1;
    }
}
