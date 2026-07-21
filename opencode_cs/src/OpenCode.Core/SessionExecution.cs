using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenCode.Core;

public interface ISessionExecution
{
    Task WakeAsync(string sessionId);
    Task ResumeAsync(string sessionId);
    Task InterruptAsync(string sessionId);
    Task<HashSet<string>> ActiveAsync();
}

public class SessionExecution : ISessionExecution
{
    readonly ISessionRunner runner;
    readonly HashSet<string> active = new();
    readonly object lockObj = new();

    public SessionExecution(ISessionRunner runner) => this.runner = runner;

    public Task WakeAsync(string sessionId)
    {
        lock (lockObj)
        {
            active.Add(sessionId);
        }
        _ = RunAsync(sessionId, false);
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string sessionId)
    {
        lock (lockObj)
        {
            active.Add(sessionId);
        }
        _ = RunAsync(sessionId, true);
        return Task.CompletedTask;
    }

    public Task InterruptAsync(string sessionId)
    {
        lock (lockObj)
        {
            active.Remove(sessionId);
        }
        return runner.InterruptAsync(sessionId);
    }

    public Task<HashSet<string>> ActiveAsync()
    {
        lock (lockObj)
        {
            return Task.FromResult(new HashSet<string>(active));
        }
    }

    async Task RunAsync(string sessionId, bool force)
    {
        try
        {
            await runner.RunAsync(sessionId, force);
        }
        finally
        {
            lock (lockObj)
            {
                active.Remove(sessionId);
            }
        }
    }
}
