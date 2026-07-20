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
    readonly HashSet<string> active = new();
    readonly object lockObj = new();

    public Task WakeAsync(string sessionId)
    {
        lock (lockObj)
        {
            active.Add(sessionId);
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string sessionId)
    {
        lock (lockObj)
        {
            active.Add(sessionId);
        }
        return Task.CompletedTask;
    }

    public Task InterruptAsync(string sessionId)
    {
        lock (lockObj)
        {
            active.Remove(sessionId);
        }
        return Task.CompletedTask;
    }

    public Task<HashSet<string>> ActiveAsync()
    {
        lock (lockObj)
        {
            return Task.FromResult(new HashSet<string>(active));
        }
    }
}
