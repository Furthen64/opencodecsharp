namespace OpenCode.Core;

public record SessionRevertBoundaryInput(
    string SessionId,
    string MessageId
);

public record SessionRevertStagedResult(
    string MessageId,
    string? Snapshot,
    string Diff,
    Schema.FileDiff[] Files
);

public record SessionRevertPlanEntry(
    string FilePath,
    string SnapshotId
);

public interface ISessionRevertService
{
    Task<SessionRevertStagedResult> StageAsync(string sessionId, string messageId, bool files = true);
    Task ClearAsync(string sessionId);
    Task CommitAsync(string sessionId);
}

public class SessionRevertService : ISessionRevertService
{
    readonly ISnapshotService snapshot;
    readonly IEventService events;

    public SessionRevertService(ISnapshotService snapshot, IEventService events)
    {
        this.snapshot = snapshot;
        this.events = events;
    }

    public async Task<SessionRevertStagedResult> StageAsync(string sessionId, string messageId, bool files = true)
    {
        var original = await snapshot.CaptureAsync();
        var plan = await PlanAsync(sessionId, messageId);

        var restore = new Dictionary<string, SnapshotId>();
        if (original != null && plan.Count > 0)
        {
            foreach (var entry in plan)
                restore[entry.FilePath] = original.Value;
        }

        if (restore.Count > 0)
            await snapshot.RestoreAsync(restore);

        var paths = files ? plan.Select(p => p.FilePath).ToArray() : Array.Empty<string>();
        Schema.FileDiff[] diffFiles = Array.Empty<Schema.FileDiff>();
        if (original != null)
        {
            var current = await snapshot.CaptureAsync();
            if (current != null)
                diffFiles = await snapshot.DiffAsync(original.Value, current.Value, 3, paths);
        }

        var diffText = string.Join("\n", diffFiles.Select(f => f.Patch)).Trim();

        var revert = new SessionRevertStagedResult(
            MessageId: messageId,
            Snapshot: original?.Value,
            Diff: diffText,
            Files: diffFiles
        );

        await events.PublishAsync(
            new EventDefinition("session.revert.staged", true, "SessionId", 1),
            new { SessionId = sessionId, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Revert = revert }
        );

        return revert;
    }

    public async Task ClearAsync(string sessionId)
    {
        await events.PublishAsync(
            new EventDefinition("session.revert.cleared", true, "SessionId", 1),
            new { SessionId = sessionId, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
        );
    }

    public async Task CommitAsync(string sessionId)
    {
        await events.PublishAsync(
            new EventDefinition("session.revert.committed", true, "SessionId", 1),
            new { SessionId = sessionId, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
        );
    }

    async Task<List<SessionRevertPlanEntry>> PlanAsync(string sessionId, string messageId)
    {
        var entries = new List<SessionRevertPlanEntry>();
        return entries;
    }
}
