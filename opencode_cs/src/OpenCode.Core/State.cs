using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenCode.Core;

public delegate Task TransformCallback<Draft>(Draft draft);

public interface IStateTransformable<Draft>
{
    Task<TransformRegistration> TransformAsync(TransformCallback<Draft> callback);
    Task ReloadAsync();
}

public record TransformRegistration(Func<Task> Dispose);

public interface IState<State, Draft> : IStateTransformable<Draft>
{
    State Get();
}

public class StateManager<StateData, Draft> : IState<StateData, Draft>
    where StateData : class
    where Draft : class
{
    readonly Func<StateData> initialFactory;
    readonly Func<StateData, Draft> draftFactory;
    readonly Func<Draft, Task>? finalizer;

    StateData state;
    List<TransformCallback<Draft>> transforms = new();
    readonly SemaphoreSlim semaphore = new(1, 1);

    public StateManager(Func<StateData> initialFactory, Func<StateData, Draft> draftFactory, Func<Draft, Task>? finalizer = null)
    {
        this.initialFactory = initialFactory;
        this.draftFactory = draftFactory;
        this.finalizer = finalizer;
        state = initialFactory();
    }

    public StateData Get() => state;

    public async Task<TransformRegistration> TransformAsync(TransformCallback<Draft> callback)
    {
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            transforms.Add(callback);
        }
        finally
        {
            semaphore.Release();
        }

        await ReloadInternal().ConfigureAwait(false);

        var dispose = new Func<Task>(async () =>
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                transforms.Remove(callback);
            }
            finally
            {
                semaphore.Release();
            }
            await ReloadInternal().ConfigureAwait(false);
        });

        return new TransformRegistration(dispose);
    }

    public Task ReloadAsync() => ReloadInternal();

    async Task ReloadInternal()
    {
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var next = initialFactory();
            var draft = draftFactory(next);
            foreach (var transform in transforms)
                await transform(draft).ConfigureAwait(false);
            if (finalizer != null)
                await finalizer(draft).ConfigureAwait(false);
            state = next;
        }
        finally
        {
            semaphore.Release();
        }
    }
}
