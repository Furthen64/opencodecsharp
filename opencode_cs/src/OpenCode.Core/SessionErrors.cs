using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenCode.Core;

public class SessionNotFoundError : Exception
{
    public string SessionId { get; }
    public SessionNotFoundError(string sessionId) : base($"Session not found: {sessionId}")
    {
        SessionId = sessionId;
    }
}

public class SessionOperationUnavailableError : Exception
{
    public string Operation { get; }
    public SessionOperationUnavailableError(string operation) : base($"Operation unavailable: {operation}")
    {
        Operation = operation;
    }
}

public class SessionPromptConflictError : Exception
{
    public string SessionId { get; }
    public string MessageId { get; }
    public SessionPromptConflictError(string sessionId, string messageId) : base($"Prompt conflict: {sessionId}/{messageId}")
    {
        SessionId = sessionId;
        MessageId = messageId;
    }
}

public class SessionBusyError : Exception
{
    public string SessionId { get; }
    public SessionBusyError(string sessionId) : base($"Session is busy: {sessionId}")
    {
        SessionId = sessionId;
    }
}

public interface ISessionService
{
    Task<List<Schema.SessionInfo>> ListAsync(SessionListInput? input = null);
    Task<Schema.SessionInfo> CreateAsync(SessionCreateInput input);
    Task<Schema.SessionInfo> GetAsync(string sessionId);
    Task<List<Schema.SessionInfo>> ChildrenAsync(string sessionId);
    Task<Schema.SessionInfo> UpdateAsync(SessionUpdateInput input);
    Task<Schema.SessionInfo> ForkAsync(string sessionId, string? messageId = null);
    Task RemoveAsync(string sessionId);
    Task<List<object>> MessagesAsync(SessionMessagesInput input);
    Task<object?> MessageAsync(string sessionId, string messageId);
    Task RemoveMessageAsync(string sessionId, string messageId);
    Task<List<object>> ContextAsync(string sessionId);
    Task<SessionAdmitted> PromptAsync(SessionPromptInput input);
    Task SwitchAgentAsync(SessionSwitchAgentInput input);
    Task SwitchModelAsync(SessionSwitchModelInput input);
    Task<bool> CompactAsync(SessionCompactInput input);
    Task WaitAsync(string sessionId);
    Task<HashSet<string>> ActiveAsync();
    Task ResumeAsync(string sessionId);
    Task InterruptAsync(string sessionId);
}

public record SessionAdmitted(
    long AdmittedSeq,
    string SessionId,
    string MessageId,
    SessionPrompt Prompt,
    SessionInputDelivery Delivery,
    long TimeCreated
);

public class SessionStore
{
    readonly Dictionary<string, Schema.SessionInfo> sessions = new();
    readonly Dictionary<string, List<Schema.SessionMessageBase>> messages = new();

    public virtual Task<Schema.SessionInfo?> GetAsync(string sessionId) => Task.FromResult(sessions.TryGetValue(sessionId, out var s) ? s : null);
    public virtual Task SetAsync(Schema.SessionInfo session)
    {
        sessions[session.Id] = session;
        messages.TryAdd(session.Id, []);
        return Task.CompletedTask;
    }
    public virtual Task<List<Schema.SessionInfo>> AllAsync() => Task.FromResult(new List<Schema.SessionInfo>(sessions.Values));
    public virtual Task<List<Schema.SessionMessageBase>> MessagesAsync(string sessionId) =>
        Task.FromResult(messages.TryGetValue(sessionId, out var list) ? list : new List<Schema.SessionMessageBase>());
    public virtual Task AddMessageAsync(string sessionId, Schema.SessionMessageBase message)
    {
        messages.GetValueOrDefault(sessionId, []).Add(message);
        return Task.CompletedTask;
    }
    public virtual Task RemoveAsync(string sessionId)
    {
        sessions.Remove(sessionId);
        messages.Remove(sessionId);
        return Task.CompletedTask;
    }
    public virtual Task ReplaceMessageAsync(string sessionId, string messageId, Schema.SessionMessageBase message)
    {
        var list = messages.GetValueOrDefault(sessionId, []);
        var index = list.FindIndex(current => current.Id == messageId);
        if (index >= 0) list[index] = message;
        return Task.CompletedTask;
    }
    public virtual Task RemoveMessageAsync(string sessionId, string messageId)
    {
        if (messages.TryGetValue(sessionId, out var list))
            list.RemoveAll(message => message.Id == messageId);
        return Task.CompletedTask;
    }
}

public sealed class SessionService : ISessionService
{
    private readonly SessionStore store;
    private readonly ISessionExecution execution;
    private readonly IProjectService projects;
    private readonly IEventService events;
    private readonly ISessionRevertService reverts;
    private readonly ISessionCompactionService compaction;
    private long admittedSequence;

    public SessionService(
        SessionStore store,
        ISessionExecution execution,
        IProjectService projects,
        IEventService events,
        ISessionRevertService reverts,
        ISessionCompactionService compaction)
    {
        this.store = store;
        this.execution = execution;
        this.projects = projects;
        this.events = events;
        this.reverts = reverts;
        this.compaction = compaction;
    }

    public async Task<List<Schema.SessionInfo>> ListAsync(SessionListInput? input = null)
    {
        var sessions = await store.AllAsync();
        var filtered = sessions.Where(session =>
            (input?.Directory is null || session.Location.Directory == input.Directory) &&
            (input?.WorkspaceId is null || session.Location.WorkspaceId == input.WorkspaceId) &&
            (input?.Project is null || session.ProjectId == input.Project) &&
            (input?.Subpath is null || session.Subpath == input.Subpath) &&
            (input?.Search is null || session.Title.Contains(input.Search, StringComparison.OrdinalIgnoreCase)));
        var requestedAscending = input?.Order?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true;
        var previous = input?.Anchor?.Direction.Equals("previous", StringComparison.OrdinalIgnoreCase) == true;
        var ascending = previous ? !requestedAscending : requestedAscending;

        if (input?.Anchor is { } anchor)
        {
            filtered = filtered.Where(session => ascending
                ? session.Time.Created > anchor.Time || session.Time.Created == anchor.Time && string.CompareOrdinal(session.Id, anchor.Id) > 0
                : session.Time.Created < anchor.Time || session.Time.Created == anchor.Time && string.CompareOrdinal(session.Id, anchor.Id) < 0);
        }

        var ordered = ascending
            ? filtered.OrderBy(session => session.Time.Created).ThenBy(session => session.Id, StringComparer.Ordinal)
            : filtered.OrderByDescending(session => session.Time.Created).ThenByDescending(session => session.Id, StringComparer.Ordinal);
        var result = ordered.Take(input?.Limit ?? int.MaxValue).ToList();
        if (previous) result.Reverse();
        return result;
    }

    public async Task<Schema.SessionInfo> CreateAsync(SessionCreateInput input)
    {
        var project = await projects.ResolveAsync(input.Location.Directory);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var session = new Schema.SessionInfo(
            input.Id ?? Schema.SessionId.Create(),
            null,
            project.Id,
            input.Agent,
            input.Model,
            0,
            new Schema.SessionTokens(0, 0, 0, new Schema.SessionCacheTokens(0, 0)),
            new Schema.SessionTime(now, now, null),
            "New session",
            new Schema.LocationRef(input.Location.Directory, input.Location.WorkspaceId),
            null,
            null);
        await store.SetAsync(session);
        return session;
    }

    public async Task<Schema.SessionInfo> GetAsync(string sessionId) =>
        await store.GetAsync(sessionId) ?? throw new SessionNotFoundError(sessionId);

    public async Task<List<Schema.SessionInfo>> ChildrenAsync(string sessionId)
    {
        await GetAsync(sessionId);
        return (await store.AllAsync())
            .Where(session => session.ParentId == sessionId)
            .OrderByDescending(session => session.Time.Created)
            .ThenByDescending(session => session.Id, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<Schema.SessionInfo> UpdateAsync(SessionUpdateInput input)
    {
        var current = await GetAsync(input.SessionId);
        var updated = current with
        {
            Title = input.Title ?? current.Title,
            Time = current.Time with
            {
                Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Archived = input.Archived ?? current.Time.Archived,
            },
        };
        await store.SetAsync(updated);
        await events.PublishAsync(
            new EventDefinition("session.updated", true, "SessionId", 1),
            new { SessionId = updated.Id, Info = updated });
        return updated;
    }

    public async Task<Schema.SessionInfo> ForkAsync(string sessionId, string? messageId = null)
    {
        var original = await GetAsync(sessionId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fork = new Schema.SessionInfo(
            Schema.SessionId.Create(),
            null,
            original.ProjectId,
            null,
            null,
            0,
            new Schema.SessionTokens(0, 0, 0, new Schema.SessionCacheTokens(0, 0)),
            new Schema.SessionTime(now, now, null),
            ForkedTitle(original.Title),
            original.Location,
            original.Subpath,
            null);
        await store.SetAsync(fork);

        foreach (var message in await store.MessagesAsync(sessionId))
        {
            if (messageId is not null && string.CompareOrdinal(message.Id, messageId) >= 0)
                break;
            await store.AddMessageAsync(fork.Id, message with { Id = Schema.MessageId.Create() });
        }

        return fork;
    }

    public async Task RemoveAsync(string sessionId)
    {
        await GetAsync(sessionId);
        await execution.InterruptAsync(sessionId);
        await store.RemoveAsync(sessionId);
        await events.PublishAsync(
            new EventDefinition("session.deleted", true, "SessionId", 1),
            new { SessionId = sessionId, Info = new { Id = sessionId } });
    }

    public async Task<List<object>> MessagesAsync(SessionMessagesInput input)
    {
        await GetAsync(input.SessionId);
        var all = await store.MessagesAsync(input.SessionId);
        var requestedAscending = input.Order?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true;
        var previous = input.Cursor?.Direction.Equals("previous", StringComparison.OrdinalIgnoreCase) == true;
        var ascending = previous ? !requestedAscending : requestedAscending;
        var indexed = all.Select((message, index) => (Message: message, Index: index));

        if (input.Cursor is { } cursor)
        {
            var anchor = all.FindIndex(message => message.Id == cursor.Id);
            if (anchor < 0) return [];
            indexed = indexed.Where(item => ascending ? item.Index > anchor : item.Index < anchor);
        }

        var ordered = ascending ? indexed.OrderBy(item => item.Index) : indexed.OrderByDescending(item => item.Index);
        var result = ordered.Take(input.Limit ?? int.MaxValue).Select(item => (object)item.Message).ToList();
        if (previous) result.Reverse();
        return result;
    }

    public async Task<object?> MessageAsync(string sessionId, string messageId)
    {
        await GetAsync(sessionId);
        return (await store.MessagesAsync(sessionId)).FirstOrDefault(message => message.Id == messageId);
    }

    public async Task RemoveMessageAsync(string sessionId, string messageId)
    {
        await GetAsync(sessionId);
        if ((await execution.ActiveAsync()).Contains(sessionId))
            throw new SessionBusyError(sessionId);
        await store.RemoveMessageAsync(sessionId, messageId);
        await events.PublishAsync(
            new EventDefinition("session.message.removed", true, "SessionId", 1),
            new { SessionId = sessionId, MessageId = messageId });
    }

    public async Task<List<object>> ContextAsync(string sessionId)
    {
        await GetAsync(sessionId);
        return [];
    }

    public async Task<SessionAdmitted> PromptAsync(SessionPromptInput input)
    {
        await GetAsync(input.SessionId);
        await reverts.CommitAsync(input.SessionId);
        var messageId = input.Id ?? Schema.MessageId.Create();
        var prompt = new SessionPrompt(input.Prompt.Text, null, null);
        var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await store.AddMessageAsync(input.SessionId, new Schema.SessionMessageUser(
            messageId, null, created, "user", input.Prompt.Text, null, null));
        await events.PublishAsync(new EventDefinition("session.next.prompted", true, "SessionId", 1), new
        {
            SessionId = input.SessionId,
            MessageID = messageId,
            Prompt = new { Text = input.Prompt.Text },
        });
        if (input.Resume != false)
            await execution.WakeAsync(input.SessionId);
        return new SessionAdmitted(
            Interlocked.Increment(ref admittedSequence),
            input.SessionId,
            messageId,
            prompt,
            input.Delivery ?? SessionInputDelivery.Steer,
            created);
    }

    public async Task SwitchAgentAsync(SessionSwitchAgentInput input)
    {
        var session = await GetAsync(input.SessionId);
        await store.SetAsync(session with { Agent = input.Agent, Time = session.Time with { Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } });
    }

    public async Task SwitchModelAsync(SessionSwitchModelInput input)
    {
        var session = await GetAsync(input.SessionId);
        await store.SetAsync(session with { Model = input.Model, Time = session.Time with { Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } });
    }

    public async Task<bool> CompactAsync(SessionCompactInput input)
    {
        await GetAsync(input.SessionId);
        await reverts.CommitAsync(input.SessionId);
        var result = await compaction.CompactManualAsync(input.SessionId, input.Model, input.Auto);
        if (result.Compacted && input.Auto)
            await execution.WakeAsync(input.SessionId);
        return result.Compacted;
    }
    public async Task WaitAsync(string sessionId) => await GetAsync(sessionId);
    public Task<HashSet<string>> ActiveAsync() => execution.ActiveAsync();

    public async Task ResumeAsync(string sessionId)
    {
        await GetAsync(sessionId);
        await execution.ResumeAsync(sessionId);
    }

    public async Task InterruptAsync(string sessionId)
    {
        await GetAsync(sessionId);
        await execution.InterruptAsync(sessionId);
    }

    private static string ForkedTitle(string title)
    {
        var match = System.Text.RegularExpressions.Regex.Match(title, @"^(.+) \(fork #(\d+)\)$");
        return match.Success
            ? $"{match.Groups[1].Value} (fork #{long.Parse(match.Groups[2].Value) + 1})"
            : $"{title} (fork #1)";
    }
}
