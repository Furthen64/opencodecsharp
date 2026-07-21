using System.Collections.Concurrent;

namespace OpenCode.Core;

public record CorePermissionRequest(
    string Id,
    string SessionId,
    string Action,
    string[] Resources,
    string[]? Save,
    Dictionary<string, object>? Metadata,
    Schema.PermissionSource? Source
);

public record PermissionAssertInput(
    string? Id,
    string SessionId,
    string Action,
    string[] Resources,
    string[]? Save,
    Dictionary<string, object>? Metadata,
    Schema.PermissionSource? Source,
    string? Agent
);

public record PermissionAskResult(
    string Id,
    PermissionEffect Effect
);

public class PermissionDeclinedError : Exception
{
    public PermissionDeclinedError() : base("Permission declined") { }
}

public class PermissionCorrectedError : Exception
{
    public string Feedback { get; }
    public PermissionCorrectedError(string feedback) : base($"Permission corrected: {feedback}")
    {
        Feedback = feedback;
    }
}

public class PermissionBlockedError : Exception
{
    public Schema.PermissionRule[] Rules { get; }
    public PermissionBlockedError(Schema.PermissionRule[] rules) : base($"Permission blocked by rules")
    {
        Rules = rules;
    }
}

public class PermissionNotFoundException(string requestId) : Exception($"Permission request not found: {requestId}")
{
    public string RequestId { get; } = requestId;
}

static class Wildcard
{
    public static bool Match(string value, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern == value) return true;

        var parts = pattern.Split('*');
        if (parts.Length == 2)
        {
            return value.StartsWith(parts[0]) && value.EndsWith(parts[1]);
        }

        return false;
    }
}

public interface IPermissionService
{
    Task<PermissionAskResult> AskAsync(PermissionAssertInput input);
    Task AssertAsync(PermissionAssertInput input);
    Task ReplyAsync(string requestId, Schema.PermissionReply reply, string? message = null);
    Task<CorePermissionRequest?> GetAsync(string id);
    Task<CorePermissionRequest[]> ListAsync();
    Task<CorePermissionRequest[]> ForSessionAsync(string sessionId);
}

public class PermissionService : IPermissionService
{
    readonly IEventService events;
    readonly IAgentService agents;
    readonly SessionStore sessionStore;
    readonly ConcurrentDictionary<string, PendingPermission> pending = new();

    static readonly Schema.PermissionRule[] DefaultDeny = { new Schema.PermissionRule("*", "*", PermissionEffect.Deny) };

    public PermissionService(IEventService events, IAgentService agents, SessionStore sessionStore)
    {
        this.events = events;
        this.agents = agents;
        this.sessionStore = sessionStore;
    }

    class PendingPermission
    {
        public CorePermissionRequest Request { get; }
        public string? Agent { get; }
        public TaskCompletionSource<bool> Tcs { get; }

        public PendingPermission(CorePermissionRequest request, string? agent)
        {
            Request = request;
            Agent = agent;
            Tcs = new TaskCompletionSource<bool>();
        }
    }

    public async Task<PermissionAskResult> AskAsync(PermissionAssertInput input)
    {
        var result = await EvaluateAsync(input);
        var requestId = input.Id ?? Guid.NewGuid().ToString();
        var request = new CorePermissionRequest(
            Id: requestId,
            SessionId: input.SessionId,
            Action: input.Action,
            Resources: input.Resources,
            Save: input.Save,
            Metadata: input.Metadata,
            Source: input.Source
        );

        if (result.Effect == PermissionEffect.Ask)
        {
            pending[requestId] = new PendingPermission(request, input.Agent);
            await events.PublishAsync(EventDefinitions.PermissionAsked, request);
        }

        return new PermissionAskResult(requestId, result.Effect);
    }

    public async Task AssertAsync(PermissionAssertInput input)
    {
        var result = await EvaluateAsync(input);

        if (result.Effect == PermissionEffect.Deny)
        {
            var relevantRules = result.Rules.Where(r => Wildcard.Match(input.Action, r.Action)).ToArray();
            throw new PermissionBlockedError(relevantRules);
        }

        if (result.Effect == PermissionEffect.Allow) return;

        var requestId = input.Id ?? Guid.NewGuid().ToString();
        var request = new CorePermissionRequest(
            Id: requestId,
            SessionId: input.SessionId,
            Action: input.Action,
            Resources: input.Resources,
            Save: input.Save,
            Metadata: input.Metadata,
            Source: input.Source
        );

        pending[requestId] = new PendingPermission(request, input.Agent);
        await events.PublishAsync(EventDefinitions.PermissionAsked, request);

        try
        {
            await pending[requestId].Tcs.Task;
        }
        catch (PermissionDeclinedError)
        {
            throw;
        }
        finally
        {
            pending.TryRemove(requestId, out _);
        }
    }

    public async Task ReplyAsync(string requestId, Schema.PermissionReply reply, string? message = null)
    {
        if (!pending.TryRemove(requestId, out var pendingItem))
            throw new PermissionNotFoundException(requestId);

        await events.PublishAsync(EventDefinitions.PermissionReplied, new
        {
            SessionId = pendingItem.Request.SessionId,
            RequestId = requestId,
            Reply = reply
        });

        if (reply == Schema.PermissionReply.Reject)
        {
            pendingItem.Tcs.SetException(message != null ? (Exception)new PermissionCorrectedError(message) : new PermissionDeclinedError());

            foreach (var entry in pending.Where(p => p.Value.Request.SessionId == pendingItem.Request.SessionId).ToList())
            {
                entry.Value.Tcs.SetException(new PermissionDeclinedError());
                this.pending.TryRemove(entry.Key, out _);
            }
        }
        else
        {
            pendingItem.Tcs.SetResult(true);
        }
    }

    public Task<CorePermissionRequest?> GetAsync(string id)
    {
        return Task.FromResult(pending.TryGetValue(id, out var p) ? p.Request : null);
    }

    public Task<CorePermissionRequest[]> ListAsync()
    {
        return Task.FromResult(pending.Values.Select(p => p.Request).ToArray());
    }

    public Task<CorePermissionRequest[]> ForSessionAsync(string sessionId)
    {
        return Task.FromResult(pending.Values.Where(p => p.Request.SessionId == sessionId).Select(p => p.Request).ToArray());
    }

    async Task<PermissionEvaluation> EvaluateAsync(PermissionAssertInput input)
    {
        var session = await sessionStore.GetAsync(input.SessionId);
        if (session == null)
            throw new SessionNotFoundError(input.SessionId);

        var agent = await agents.ResolveAsync(input.Agent ?? session.Agent);
        var agentRulesList = agent?.Permissions ?? DefaultDeny.ToList();

        var denied = input.Resources.Any(r => EvaluateRule(input.Action, r, agentRulesList.ToArray()) == PermissionEffect.Deny);
        if (denied)
            return new PermissionEvaluation(PermissionEffect.Deny, agentRulesList);

        var allRules = agentRulesList;
        var effects = input.Resources.Select(r => EvaluateRule(input.Action, r, allRules.ToArray())).ToArray();
        var effect = effects.Contains(PermissionEffect.Deny) ? PermissionEffect.Deny
            : effects.Contains(PermissionEffect.Ask) ? PermissionEffect.Ask
            : PermissionEffect.Allow;

        return new PermissionEvaluation(effect, allRules);
    }

    static PermissionEffect EvaluateRule(string action, string resource, PermissionRule[] rules)
    {
        return rules.Where(r => Wildcard.Match(action, r.Action) && Wildcard.Match(resource, r.Resource))
            .LastOrDefault()?.Effect ?? PermissionEffect.Ask;
    }

    record PermissionEvaluation(PermissionEffect Effect, List<PermissionRule> Rules);
}

static class EventDefinitions
{
    public static EventDefinition PermissionAsked => new("permission.asked", false, null, 1);
    public static EventDefinition PermissionReplied => new("permission.replied", false, null, 1);
}
