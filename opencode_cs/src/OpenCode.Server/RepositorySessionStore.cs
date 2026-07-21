using System.Text.Json;
using OpenCode.Core;
using OpenCode.Data;
using Schema = OpenCode.Schema;

namespace OpenCode.Server;

public sealed class RepositorySessionStore : SessionStore
{
    private readonly SessionRepository sessions;
    private readonly MessageRepository messages;
    private readonly ProjectRepository projects;
    private readonly Lazy<Task> initialization;
    private readonly SemaphoreSlim messageWrites = new(1, 1);

    public RepositorySessionStore(
        Database database,
        SessionRepository sessions,
        MessageRepository messages,
        ProjectRepository projects)
    {
        this.sessions = sessions;
        this.messages = messages;
        this.projects = projects;
        initialization = new Lazy<Task>(database.InitializeAsync);
    }

    public override async Task<Schema.SessionInfo?> GetAsync(string sessionId)
    {
        await EnsureInitializedAsync();
        return await sessions.GetAsync(sessionId);
    }

    public override async Task SetAsync(Schema.SessionInfo session)
    {
        await EnsureInitializedAsync();
        await EnsureProjectAsync(session);
        await sessions.InsertAsync(session);
    }

    public override async Task<List<Schema.SessionInfo>> AllAsync()
    {
        await EnsureInitializedAsync();
        return await sessions.ListAllAsync();
    }

    public override async Task<List<Schema.SessionMessageBase>> MessagesAsync(string sessionId)
    {
        await EnsureInitializedAsync();
        var rows = await messages.GetBySessionAsync(sessionId);
        return rows.Select(row => row.Deserialize())
            .Where(message => message is not null)
            .Cast<Schema.SessionMessageBase>()
            .ToList();
    }

    public override async Task AddMessageAsync(string sessionId, Schema.SessionMessageBase message)
    {
        await EnsureInitializedAsync();
        await messageWrites.WaitAsync();
        try
        {
            var sequence = await messages.GetMaxSeqAsync(sessionId) + 1;
            await messages.UpsertAsync(ToRow(sessionId, message, sequence));
        }
        finally
        {
            messageWrites.Release();
        }
    }

    public override async Task ReplaceMessageAsync(
        string sessionId,
        string messageId,
        Schema.SessionMessageBase message)
    {
        await EnsureInitializedAsync();
        await messageWrites.WaitAsync();
        try
        {
            var existing = (await messages.GetBySessionAsync(sessionId))
                .FirstOrDefault(row => row.Id == messageId);
            if (existing is null)
                return;
            await messages.UpsertAsync(ToRow(sessionId, message, existing.Seq));
        }
        finally
        {
            messageWrites.Release();
        }
    }

    public override async Task RemoveMessageAsync(string sessionId, string messageId)
    {
        await EnsureInitializedAsync();
        await messages.DeleteAsync(sessionId, messageId);
    }

    public override async Task RemoveAsync(string sessionId)
    {
        await EnsureInitializedAsync();
        await messages.DeleteBySessionAsync(sessionId);
        await sessions.DeleteAsync(sessionId);
    }

    private Task EnsureInitializedAsync() => initialization.Value;

    private async Task EnsureProjectAsync(Schema.SessionInfo session)
    {
        var directory = session.Location.Directory;
        await projects.UpsertAsync(new Schema.ProjectInfo(
            session.ProjectId,
            directory,
            null,
            Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            null,
            null,
            new Schema.ProjectTime(session.Time.Created, session.Time.Updated, null),
            []));
    }

    private static SessionMessageInfo ToRow(
        string sessionId,
        Schema.SessionMessageBase message,
        long sequence)
    {
        var updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new SessionMessageInfo(
            message.Id,
            sessionId,
            MessageType(message),
            sequence,
            message.Created,
            updated,
            JsonSerializer.Serialize<Schema.SessionMessageBase>(message));
    }

    private static string MessageType(Schema.SessionMessageBase message) => message switch
    {
        Schema.SessionMessageAgentSwitched value => value.Type,
        Schema.SessionMessageModelSwitched value => value.Type,
        Schema.SessionMessageUser value => value.Type,
        Schema.SessionMessageSynthetic value => value.Type,
        Schema.SessionMessageSystem value => value.Type,
        Schema.SessionMessageShell value => value.Type,
        Schema.SessionMessageAssistant value => value.Type,
        Schema.SessionMessageCompaction value => value.Type,
        _ => message.GetType().Name,
    };
}
