namespace OpenCode.Core;

public interface ISkillService
{
    Task<IReadOnlyList<Schema.SkillInfo>> ListAsync();
    Task<Schema.SkillInfo[]> GetSourcesAsync();
}

public class SkillService : ISkillService
{
    readonly IAgentService agents;
    readonly SkillState state;

    public SkillService(IAgentService agents)
    {
        this.agents = agents;
        this.state = new SkillState();
    }

    public Task<IReadOnlyList<Schema.SkillInfo>> ListAsync()
    {
        return Task.FromResult<IReadOnlyList<Schema.SkillInfo>>(state.GetSkills().ToList());
    }

    public Task<Schema.SkillInfo[]> GetSourcesAsync()
    {
        return Task.FromResult(state.GetSkills().ToArray());
    }

    public void AddSource(Schema.SkillSource source)
    {
        state.AddSource(source);
    }

    static bool IsAvailable(Schema.SkillInfo skill, Schema.PermissionRule[] agentPermissions)
    {
        return PermissionEffectEvaluator.Evaluate("skill", skill.Name, agentPermissions) != PermissionEffect.Deny;
    }

    class SkillState
    {
        readonly List<Schema.SkillSource> sources = new();
        readonly List<Schema.SkillInfo> skills = new();
        readonly SemaphoreSlim semaphore = new(1, 1);

        public void AddSource(Schema.SkillSource source)
        {
            semaphore.Wait();
            try
            {
                if (sources.Any(s => SourceEquals(s, source))) return;
                sources.Add(source);
            }
            finally
            {
                semaphore.Release();
            }
        }

        public IReadOnlyList<Schema.SkillInfo> GetSkills()
        {
            semaphore.Wait();
            try
            {
                return skills.ToList();
            }
            finally
            {
                semaphore.Release();
            }
        }

        static bool SourceEquals(Schema.SkillSource a, Schema.SkillSource b)
        {
            return a switch
            {
                Schema.SkillDirectorySource da when b is Schema.SkillDirectorySource db => da.Path == db.Path,
                Schema.SkillUrlSource ua when b is Schema.SkillUrlSource ub => ua.Url == ub.Url,
                Schema.SkillEmbeddedSource ea when b is Schema.SkillEmbeddedSource eb => ea.Skill.Name == eb.Skill.Name,
                _ => false
            };
        }
    }
}

static class PermissionEffectEvaluator
{
    public static PermissionEffect Evaluate(string action, string resource, Schema.PermissionRule[] rules)
    {
        var matching = rules
            .Where(r => Wildcard.Match(action, r.Action) && Wildcard.Match(resource, r.Resource))
            .LastOrDefault();
        return matching?.Effect ?? PermissionEffect.Ask;
    }
}
