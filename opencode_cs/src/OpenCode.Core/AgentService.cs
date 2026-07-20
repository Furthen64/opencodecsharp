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
    List<PermissionRule>? Permissions
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
    void Update(string id, Action<AgentInfo> fn);
    void Remove(string id);
}

public class AgentService : IAgentService
{
    readonly Dictionary<string, AgentInfo> agents = new();
    string? defaultId;

    public Task<AgentInfo?> GetAsync(string id) => Task.FromResult(agents.TryGetValue(id, out var agent) ? agent : null);

    public Task<AgentInfo?> DefaultAsync()
    {
        var selected = SelectDefault();
        return Task.FromResult(selected);
    }

    public Task<AgentInfo?> ResolveAsync(string? id = null)
    {
        if (id != null && agents.TryGetValue(id, out var agent))
            return Task.FromResult(agent);
        return DefaultAsync();
    }

    public Task<AgentSelection> SelectAsync(string? id = null)
    {
        if (id != null)
        {
            agents.TryGetValue(id, out var info);
            return Task.FromResult(new AgentSelection(id, info));
        }
        var selected = SelectDefault();
        return Task.FromResult(new AgentSelection(selected?.Id ?? "build", selected));
    }

    public Task<List<AgentInfo>> AllAsync() => Task.FromResult(new List<AgentInfo>(agents.Values));

    public Task<IStateTransformable<AgentDraft>> TransformAsync() => Task.FromResult<IStateTransformable<AgentDraft>>(new AgentTransformWrapper(this));

    AgentInfo? SelectDefault()
    {
        if (defaultId != null && agents.TryGetValue(defaultId, out var configured) && IsSelectable(configured))
            return configured;
        if (agents.TryGetValue("build", out var build) && IsSelectable(build))
            return build;
        foreach (var agent in agents.Values)
        {
            if (IsSelectable(agent)) return agent;
        }
        return null;
    }

    static bool IsSelectable(AgentInfo agent) => agent.Mode != "subagent" && !agent.Hidden.GetValueOrDefault();

    public void AddAgent(AgentInfo agent) => agents[agent.Id] = agent;
    public void SetDefaultId(string? id) => defaultId = id;

    class AgentTransformWrapper : IStateTransformable<AgentDraft>
    {
        readonly AgentService service;
        public AgentTransformWrapper(AgentService service) => this.service = service;
        public Task<TransformRegistration> TransformAsync(TransformCallback<AgentDraft> callback) => Task.FromResult<TransformRegistration>(null!);
        public Task ReloadAsync() => Task.CompletedTask;
    }
}
