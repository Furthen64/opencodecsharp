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

public interface ISessionService
{
    Task<List<Schema.SessionInfo>> ListAsync(SessionListInput? input = null);
    Task<Schema.SessionInfo> CreateAsync(SessionCreateInput input);
    Task<Schema.SessionInfo> GetAsync(string sessionId);
    Task<List<object>> MessagesAsync(SessionMessagesInput input);
    Task<object?> MessageAsync(string sessionId, string messageId);
    Task<List<object>> ContextAsync(string sessionId);
    Task<SessionAdmitted> PromptAsync(SessionPromptInput input);
    Task SwitchAgentAsync(SessionSwitchAgentInput input);
    Task SwitchModelAsync(SessionSwitchModelInput input);
    Task CompactAsync(SessionCompactInput input);
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

    public Task<Schema.SessionInfo?> GetAsync(string sessionId) => Task.FromResult(sessions.TryGetValue(sessionId, out var s) ? s : null);
    public Task SetAsync(Schema.SessionInfo session)
    {
        sessions[session.Id] = session;
        messages.TryAdd(session.Id, []);
        return Task.CompletedTask;
    }
    public Task<List<Schema.SessionInfo>> AllAsync() => Task.FromResult(new List<Schema.SessionInfo>(sessions.Values));
    public Task<List<Schema.SessionMessageBase>> MessagesAsync(string sessionId) =>
        Task.FromResult(messages.TryGetValue(sessionId, out var list) ? list : new List<Schema.SessionMessageBase>());
    public Task AddMessageAsync(string sessionId, Schema.SessionMessageBase message)
    {
        messages.GetValueOrDefault(sessionId, []).Add(message);
        return Task.CompletedTask;
    }
    public Task ReplaceMessageAsync(string sessionId, string messageId, Schema.SessionMessageBase message)
    {
        var list = messages.GetValueOrDefault(sessionId, []);
        var index = list.FindIndex(current => current.Id == messageId);
        if (index >= 0) list[index] = message;
        return Task.CompletedTask;
    }
}

public sealed class SessionService : ISessionService
{
    private readonly SessionStore store;
    private readonly ISessionExecution execution;
    private readonly IProjectService projects;
    private readonly IEventService events;
    private long admittedSequence;

    public SessionService(SessionStore store, ISessionExecution execution, IProjectService projects, IEventService events)
    {
        this.store = store;
        this.execution = execution;
        this.projects = projects;
        this.events = events;
    }

    public async Task<List<Schema.SessionInfo>> ListAsync(SessionListInput? input = null)
    {
        var sessions = await store.AllAsync();
        var filtered = sessions.Where(session =>
            (input?.Directory is null || session.Location.Directory == input.Directory) &&
            (input?.Project is null || session.ProjectId == input.Project) &&
            (input?.Subpath is null || session.Subpath == input.Subpath) &&
            (input?.Search is null || session.Title.Contains(input.Search, StringComparison.OrdinalIgnoreCase)));
        var ordered = input?.Order?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true
            ? filtered.OrderBy(session => session.Time.Updated)
            : filtered.OrderByDescending(session => session.Time.Updated);
        return ordered.Take(input?.Limit ?? int.MaxValue).ToList();
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

    public async Task<List<object>> MessagesAsync(SessionMessagesInput input)
    {
        await GetAsync(input.SessionId);
        var all = await store.MessagesAsync(input.SessionId);
        var ordered = input.Order?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true
            ? all.OrderBy(message => message.Created)
            : all.OrderByDescending(message => message.Created);
        return ordered.Take(input.Limit ?? int.MaxValue).Cast<object>().ToList();
    }

    public async Task<object?> MessageAsync(string sessionId, string messageId)
    {
        await GetAsync(sessionId);
        return (await store.MessagesAsync(sessionId)).FirstOrDefault(message => message.Id == messageId);
    }

    public async Task<List<object>> ContextAsync(string sessionId)
    {
        await GetAsync(sessionId);
        return [];
    }

    public async Task<SessionAdmitted> PromptAsync(SessionPromptInput input)
    {
        await GetAsync(input.SessionId);
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

    public async Task CompactAsync(SessionCompactInput input) => await GetAsync(input.SessionId);
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
}
