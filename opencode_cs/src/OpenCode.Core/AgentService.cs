using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenCode.Core;

public record AgentInfo(
    string Id,
    string? Model,
    string? Variant,
    object? Request,
    string? System,
    string? Description,
    string? Mode,
    bool? Hidden,
    string? Color,
    int? Steps,
    bool? Disabled,
    List<Schema.PermissionRule>? Permissions
);

public static class AgentInfoDefaults
{
    public static AgentInfo Empty(string id) => new(id, null, null, null, null, null, null, null, null, null, null, null);
}

public record AgentSelection(string Id, AgentInfo? Info);

public interface IAgentService
{
    Task<AgentInfo?> GetAsync(string id);
    Task<AgentInfo?> DefaultAsync();
    Task<AgentInfo?> ResolveAsync(string? id = null);
    Task<AgentSelection> SelectAsync(string? id = null);
    Task<List<AgentInfo>> AllAsync();
    Task<IStateTransformable<AgentDraft>> TransformAsync();
}

public interface AgentDraft
{
    List<AgentInfo> List();
    AgentInfo? Get(string id);
    void SetDefault(string? id);
    void Update(string id, Func<AgentInfo, AgentInfo> update);
    void Remove(string id);
}

public class AgentService : IAgentService
{
    readonly StateManager<AgentState, AgentDraft> state;

    public AgentService()
    {
        state = new StateManager<AgentState, AgentDraft>(
            initialFactory: () => new AgentState(),
            draftFactory: current => new AgentDraftImplementation(current));
    }

    public Task<AgentInfo?> GetAsync(string id) =>
        Task.FromResult(state.Get().Agents.TryGetValue(id, out var agent) ? agent : null);

    public Task<AgentInfo?> DefaultAsync()
    {
        var selected = SelectDefault();
        return Task.FromResult(selected);
    }

    public Task<AgentInfo?> ResolveAsync(string? id = null)
    {
        if (id != null && state.Get().Agents.TryGetValue(id, out var agent))
            return Task.FromResult<AgentInfo?>(agent);
        return DefaultAsync();
    }

    public Task<AgentSelection> SelectAsync(string? id = null)
    {
        if (id != null)
        {
            state.Get().Agents.TryGetValue(id, out var info);
            return Task.FromResult(new AgentSelection(id, info));
        }
        var selected = SelectDefault();
        return Task.FromResult(new AgentSelection(selected?.Id ?? "build", selected));
    }

    public Task<List<AgentInfo>> AllAsync() => Task.FromResult(new List<AgentInfo>(state.Get().Agents.Values));

    public Task<IStateTransformable<AgentDraft>> TransformAsync() =>
        Task.FromResult<IStateTransformable<AgentDraft>>(state);

    AgentInfo? SelectDefault()
    {
        var current = state.Get();
        if (current.DefaultId != null && current.Agents.TryGetValue(current.DefaultId, out var configured) && IsSelectable(configured))
            return configured;
        if (current.Agents.TryGetValue("build", out var build) && IsSelectable(build))
            return build;
        foreach (var agent in current.Agents.Values)
        {
            if (IsSelectable(agent)) return agent;
        }
        return null;
    }

    static bool IsSelectable(AgentInfo agent) => agent.Mode != "subagent" && !agent.Hidden.GetValueOrDefault();

    sealed class AgentState
    {
        public Dictionary<string, AgentInfo> Agents { get; } = new();
        public string? DefaultId { get; set; }
    }

    sealed class AgentDraftImplementation(AgentState state) : AgentDraft
    {
        public List<AgentInfo> List() => state.Agents.Values.ToList();

        public AgentInfo? Get(string id) => state.Agents.GetValueOrDefault(id);

        public void SetDefault(string? id) => state.DefaultId = id;

        public void Update(string id, Func<AgentInfo, AgentInfo> update)
        {
            var current = state.Agents.GetValueOrDefault(id) ?? AgentInfoDefaults.Empty(id);
            state.Agents[id] = update(current) with { Id = id };
        }

        public void Remove(string id) => state.Agents.Remove(id);
    }
}
