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
    string SessionId,
    string MessageId,
    SessionPrompt Prompt,
    SessionInputDelivery Delivery
);

public class SessionStore
{
    readonly Dictionary<string, Schema.SessionInfo> sessions = new();

    public Task<Schema.SessionInfo?> GetAsync(string sessionId) => Task.FromResult(sessions.TryGetValue(sessionId, out var s) ? s : null);
    public Task SetAsync(Schema.SessionInfo session) { sessions[session.Id] = session; return Task.CompletedTask; }
    public Task<List<Schema.SessionInfo>> AllAsync() => Task.FromResult(new List<Schema.SessionInfo>(sessions.Values));
}
