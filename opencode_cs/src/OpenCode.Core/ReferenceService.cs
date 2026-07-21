namespace OpenCode.Core;

public interface IReferenceService
{
    Task<IReadOnlyList<Schema.ReferenceInfo>> ListAsync();
}

public class ReferenceService : IReferenceService
{
    readonly StateManager<ReferenceState, ReferenceDraft> stateManager;

    public ReferenceService()
    {
        stateManager = new StateManager<ReferenceState, ReferenceDraft>(
            initialFactory: () => new ReferenceState(),
            draftFactory: current => new ReferenceDraft(current),
            finalizer: _ => Task.CompletedTask
        );
    }

    public Task<IReadOnlyList<Schema.ReferenceInfo>> ListAsync()
    {
        var state = stateManager.Get();
        return Task.FromResult<IReadOnlyList<Schema.ReferenceInfo>>(
            state.Materialized.Values.ToList()
        );
    }

    public Task<IStateTransformable<ReferenceDraft>> TransformAsync()
    {
        return Task.FromResult<IStateTransformable<ReferenceDraft>>(stateManager);
    }

    public record ReferenceState
    {
        public Dictionary<string, Schema.ReferenceSource> Sources { get; init; } = new();
        public Dictionary<string, Schema.ReferenceInfo> Materialized { get; init; } = new();
    }

    public class ReferenceDraft
    {
        readonly ReferenceState state;

        public ReferenceDraft(ReferenceState state) => this.state = state;

        public void Add(string name, Schema.ReferenceSource source) => state.Sources[name] = source;
        public void Remove(string name) => state.Sources.Remove(name);
        public IReadOnlyList<KeyValuePair<string, Schema.ReferenceSource>> List() => state.Sources.ToList();
    }
}
