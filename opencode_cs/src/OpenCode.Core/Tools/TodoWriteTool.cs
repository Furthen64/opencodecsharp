using System.Text.Json;

namespace OpenCode.Core;

public record TodoWriteToolInput(
    SessionTodoInfo[] Todos
);

public record SessionTodoInfo(
    string Content,
    string Status,
    string Priority
);

public record TodoWriteToolOutput(
    SessionTodoInfo[] Todos
);

public interface ISessionTodoService
{
    Task UpdateAsync(string sessionId, SessionTodoInfo[] todos);
    Task<SessionTodoInfo[]> GetAsync(string sessionId);
}

public class SessionTodoService : ISessionTodoService
{
    readonly IEventService events;
    readonly Dictionary<string, List<SessionTodoInfo>> store = new();
    readonly SemaphoreSlim semaphore = new(1, 1);

    public SessionTodoService(IEventService events)
    {
        this.events = events;
    }

    public async Task UpdateAsync(string sessionId, SessionTodoInfo[] todos)
    {
        await semaphore.WaitAsync();
        try
        {
            store[sessionId] = new List<SessionTodoInfo>(todos);
        }
        finally
        {
            semaphore.Release();
        }

        await events.PublishAsync(
            new EventDefinition("todo.updated", false, null, 1),
            new { SessionId = sessionId, Todos = todos }
        );
    }

    public async Task<SessionTodoInfo[]> GetAsync(string sessionId)
    {
        await semaphore.WaitAsync();
        try
        {
            return store.TryGetValue(sessionId, out var todos) ? todos.ToArray() : Array.Empty<SessionTodoInfo>();
        }
        finally
        {
            semaphore.Release();
        }
    }
}

public class TodoWriteToolImpl : Tool
{
    readonly ISessionTodoService todos;
    readonly IPermissionService permission;

    public TodoWriteToolImpl(ISessionTodoService todos, IPermissionService permission)
        : base("todowrite",
            "Create and maintain a structured task list for the current coding session. Use it to track progress during multi-step work and keep todo statuses current.")
    {
        this.todos = todos;
        this.permission = permission;
    }

    public override async Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
    {
        if (input is not TodoWriteToolInput todoInput)
            throw new ToolFailure("Invalid input for todowrite tool");

        await permission.AssertAsync(new PermissionAssertInput(
            Id: null,
            SessionId: context.SessionId,
            Action: "todowrite",
            Resources: new[] { "*" },
            Save: new[] { "*" },
            Metadata: null,
            Source: new Schema.PermissionSource("tool", context.AssistantMessageId, context.ToolCallId),
                Agent: context.AgentId
        ));

        try
        {
            await todos.UpdateAsync(context.SessionId, todoInput.Todos);
        }
        catch
        {
            throw new ToolFailure("Unable to update todos");
        }

        return new ToolOutput(
            new List<ToolOutputContent>
            {
                new ToolTextContent { Text = JsonSerializer.Serialize(todoInput.Todos, new JsonSerializerOptions { WriteIndented = true }) }
            },
            new TodoWriteToolOutput(todoInput.Todos)
        );
    }

    public override ToolDefinition ToDefinition(string name)
    {
        return new ToolDefinition(
            name,
            Description,
            new { todos = "(SessionTodoInfo[]) The updated todo list" },
            null
        );
    }
}
