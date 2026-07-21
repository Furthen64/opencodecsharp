using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Core;
using OpenCode.Core.AI;
using OpenCode.Core.AI.Providers;
using OpenCode.Data;
using OpenCode.Protocol;
using Schema = OpenCode.Schema;

namespace OpenCode.Server;

public sealed class OpenCodeServerOptions
{
    public string Directory { get; init; } = Environment.CurrentDirectory;
}

public static class OpenCodeServer
{
    public static IServiceCollection AddOpenCodeServer(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var directory = configuration["OpenCode:Directory"] ?? environment.ContentRootPath;
        directory = Path.GetFullPath(directory);
        services.AddSingleton(new OpenCodeServerOptions { Directory = directory });

        services.AddSingleton<IFsUtil, FsUtil>();
        services.AddSingleton<IFileBrowserService>(provider => new FileBrowserService(
            provider.GetRequiredService<IFsUtil>(),
            provider.GetRequiredService<OpenCodeServerOptions>().Directory));
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<IAgentService, AgentService>();
        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<IEventService, EventService>();
        services.AddSingleton<IQuestionService, QuestionService>();
        services.AddSingleton<ISessionTodoService, SessionTodoService>();
        services.AddSingleton<ISkillService, SkillService>();
        services.AddSingleton<IProviderPlugin, OpenAIProviderPlugin>();
        services.AddSingleton<IProviderPlugin, AnthropicProviderPlugin>();
        services.AddSingleton<IProviderPlugin, GoogleProviderPlugin>();
        services.AddSingleton<IProviderPlugin, OpenAICompatibleProviderPlugin>();
        services.AddSingleton<IAISDKService>(provider =>
        {
            var sdk = new AISDKService();
            foreach (var plugin in provider.GetServices<IProviderPlugin>())
            {
                sdk.RegisterSdkHook(plugin.CreateSdkAsync);
                sdk.RegisterLanguageHook(plugin.CreateLanguageAsync);
            }
            return sdk;
        });
        services.AddSingleton<ILLMClient, ModelLLMClient>();
        services.AddSingleton<IModelResolver, ServerModelResolver>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<ISessionRunner, SessionRunner>();
        services.AddSingleton<ISessionExecution, SessionExecution>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<Database>();
        return services;
    }

    public static IEndpointRouteBuilder MapOpenCodeRoutes(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/global/health", () => Results.Ok(new HealthResponse(true)))
            .WithName("GlobalHealth");

        endpoints.MapGet("/path", (OpenCodeServerOptions options) =>
        {
            var paths = GlobalPathsBuilder.Create();
            return Results.Ok(new
            {
                home = paths.Home,
                state = paths.State,
                config = paths.Config,
                worktree = options.Directory,
                directory = options.Directory,
            });
        }).WithName("InstancePath");

        endpoints.MapGet("/project", async (IProjectService projects) =>
            Results.Ok((await projects.AllAsync()).Select(ToSchemaProject).ToList()))
            .WithName("ProjectList");

        endpoints.MapGet("/project/current", async (
            OpenCodeServerOptions options,
            IProjectService projects) =>
            Results.Ok(ToSchemaProject(await projects.ResolveAsync(options.Directory))))
            .WithName("ProjectCurrent");

        endpoints.MapGet("/agent", async (IAgentService agents) =>
        {
            var data = (await agents.AllAsync()).Select(ToSchemaAgent).ToList();
            return Results.Ok(new AgentListResponse(data));
        }).WithName("AgentList");

        endpoints.MapGet("/skill", async (ISkillService skills) =>
        {
            var data = (await skills.ListAsync()).ToList();
            return Results.Ok(new SkillListResponse(data));
        }).WithName("SkillList");

        endpoints.MapGet("/vcs", async (OpenCodeServerOptions options, IGitService git) =>
        {
            var repository = await git.DiscoverAsync(options.Directory);
            if (repository is null)
                return Results.Ok(new VcsInfoResponse(null, null));

            var branch = await git.GetBranchAsync(repository);
            var defaultBranch = await git.GetDefaultRemoteBranchAsync(repository);
            return Results.Ok(new VcsInfoResponse(branch, defaultBranch));
        }).WithName("VcsInfo");

        endpoints.MapGet("/vcs/diff/raw", async (OpenCodeServerOptions options, IGitService git) =>
        {
            var repository = await git.DiscoverAsync(options.Directory);
            if (repository is null)
                return Results.Text(string.Empty, "text/x-diff; charset=utf-8");

            var patch = await git.CapturePatchAsync(repository, repository.Worktree);
            return Results.Text(patch, "text/x-diff; charset=utf-8");
        }).WithName("VcsDiffRaw");

        endpoints.MapGet("/file", async (string path, IFileBrowserService files) =>
            Results.Ok((await files.ListAsync(path)).Select(item => new FileNode(
                item.Name, item.Path, item.Absolute, item.Type, item.Ignored))))
            .WithName("FileList");

        endpoints.MapGet("/file/content", async (string path, IFileBrowserService files) =>
        {
            var content = await files.ReadAsync(path);
            return Results.Ok(new FileContent(content.Type, content.Content, content.Encoding, content.MimeType));
        }).WithName("FileContent");

        endpoints.MapGet("/find/file", async (
            string query,
            string? dirs,
            string? type,
            int? limit,
            IFileBrowserService files) =>
        {
            var take = limit ?? 10;
            if (take is < 1 or > 200)
                return Results.BadRequest(new InvalidRequestError("Limit must be between 1 and 200.", "Query", "limit"));
            if (dirs is not null && dirs is not ("true" or "false"))
                return Results.BadRequest(new InvalidRequestError("Dirs must be 'true' or 'false'.", "Query", "dirs"));
            if (type is not null && type is not ("file" or "directory"))
                return Results.BadRequest(new InvalidRequestError("Type must be 'file' or 'directory'.", "Query", "type"));
            var requestedType = type ?? (dirs == "false" ? "file" : null);
            return Results.Ok(await files.FindAsync(query, requestedType, take));
        }).WithName("FileFind");

        endpoints.MapGet("/find", async (string pattern, IFileBrowserService files) =>
        {
            var matches = await files.FindTextAsync(pattern, 10);
            return Results.Ok(matches.Select(match => new FileTextMatch(
                new FileTextValue(match.Entry.Path),
                new FileTextValue(match.Text),
                match.Line,
                match.Offset,
                match.Submatches.Select(submatch => new FileTextSubmatch(
                    new FileTextValue(submatch.Text), submatch.Start, submatch.End)).ToArray())));
        }).WithName("FileFindText");

        endpoints.MapGet("/question", async (IQuestionService questions) =>
            Results.Ok(new QuestionRequestListResponse((await questions.ListAsync()).ToList())))
            .WithName("QuestionList");

        endpoints.MapPost("/question/{requestId}/reply", async (
            string requestId,
            QuestionReplyRequest request,
            IQuestionService questions) =>
        {
            await questions.ReplyAsync(new QuestionReplyInput(requestId, request.Answers));
            return Results.Ok(true);
        }).WithName("QuestionReply");

        endpoints.MapPost("/question/{requestId}/reject", async (
            string requestId,
            IQuestionService questions) =>
        {
            await questions.RejectAsync(requestId);
            return Results.Ok(true);
        }).WithName("QuestionReject");

        endpoints.MapGet("/event", StreamEventsAsync)
            .WithName("EventSubscribe");

        endpoints.MapGet("/session", async (
            string? directory,
            string? workspace,
            string? project,
            string? subpath,
            string? cursor,
            int? limit,
            string? order,
            string? search,
            ISessionService sessions) =>
        {
            if (order is not null && order is not ("asc" or "desc"))
                return Results.BadRequest(new InvalidRequestError("Order must be 'asc' or 'desc'.", "Query", "order"));
            var page = SessionCursorCodec.Decode(directory, workspace, project, subpath, cursor, order, search);
            var data = await sessions.ListAsync(new SessionListInput(
                page.Workspace,
                page.Search,
                limit ?? 50,
                page.Order,
                page.Anchor,
                page.Directory,
                page.Project,
                page.Subpath));
            return Results.Ok(new SessionsResponse(data, new PaginationCursor(
                data.Count == 0 ? null : SessionCursorCodec.Encode(page with
                {
                    Anchor = new Schema.SessionListAnchor(data[0].Id, data[0].Time.Created, "previous")
                }),
                data.Count == 0 ? null : SessionCursorCodec.Encode(page with
                {
                    Anchor = new Schema.SessionListAnchor(data[^1].Id, data[^1].Time.Created, "next")
                }))));
        }).WithName("SessionList");

        endpoints.MapGet("/session/status", async (ISessionService sessions) =>
        {
            var active = await sessions.ActiveAsync();
            return Results.Ok(new SessionActiveResponse(active.ToDictionary(id => id, _ => new SessionActiveStatus("active"))));
        }).WithName("SessionStatus");

        endpoints.MapPost("/session", async (SessionCreateRequest? request, OpenCodeServerOptions options, ISessionService sessions) =>
        {
            var input = request ?? new SessionCreateRequest(null, null, null, null);
            var location = input.Location ?? new Schema.LocationRef(options.Directory, null);
            var session = await sessions.CreateAsync(new SessionCreateInput(input.Id, input.Agent, input.Model,
                new OpenCode.Core.LocationRef(location.Directory, location.WorkspaceId)));
            return Results.Created($"/session/{session.Id}", session);
        }).WithName("SessionCreate");

        endpoints.MapGet("/session/{sessionId}", async (string sessionId, ISessionService sessions) =>
            Results.Ok(await sessions.GetAsync(sessionId)))
            .WithName("SessionGet");

        endpoints.MapGet("/session/{sessionId}/children", async (string sessionId, ISessionService sessions) =>
            Results.Ok(await sessions.ChildrenAsync(sessionId)))
            .WithName("SessionChildren");

        endpoints.MapPatch("/session/{sessionId}", async (
            string sessionId,
            SessionUpdateRequest request,
            ISessionService sessions) =>
            Results.Ok(await sessions.UpdateAsync(new SessionUpdateInput(
                sessionId,
                request.Title,
                request.Time?.Archived))))
            .WithName("SessionUpdate");

        endpoints.MapDelete("/session/{sessionId}", async (string sessionId, ISessionService sessions) =>
        {
            await sessions.RemoveAsync(sessionId);
            return Results.Ok(true);
        }).WithName("SessionDelete");

        endpoints.MapGet("/session/{sessionId}/todo", async (
            string sessionId,
            ISessionService sessions,
            ISessionTodoService todos) =>
        {
            await sessions.GetAsync(sessionId);
            return Results.Ok(await todos.GetAsync(sessionId));
        }).WithName("SessionTodo");

        endpoints.MapGet("/session/{sessionId}/message", async (
            string sessionId,
            int? limit,
            string? order,
            string? cursor,
            ISessionService sessions) =>
        {
            if (cursor is not null && order is not null)
                throw new InvalidCursorException("Cursor cannot be combined with order");
            var page = MessageCursorCodec.Decode(cursor, order);
            var data = await sessions.MessagesAsync(new SessionMessagesInput(
                sessionId,
                limit ?? 50,
                page.Order,
                page.Cursor));
            return Results.Ok(new SessionMessagesResponse(data, new PaginationCursor(
                data.Count == 0 ? null : MessageCursorCodec.Encode(((Schema.SessionMessageBase)data[0]).Id, page.Order, "previous"),
                data.Count == 0 ? null : MessageCursorCodec.Encode(((Schema.SessionMessageBase)data[^1]).Id, page.Order, "next"))));
        }).WithName("SessionMessages");

        endpoints.MapGet("/session/{sessionId}/message/{messageId}", async (
            string sessionId,
            string messageId,
            ISessionService sessions) =>
        {
            var message = await sessions.MessageAsync(sessionId, messageId);
            return message is null
                ? Results.NotFound(new MessageNotFoundError(sessionId, messageId, $"Message not found: {messageId}"))
                : Results.Ok(message);
        }).WithName("SessionMessageGet");

        endpoints.MapPost("/session/{sessionId}/message", async (
            string sessionId,
            SessionPromptRequest request,
            ISessionService sessions) =>
        {
            var admitted = await sessions.PromptAsync(new SessionPromptInput(
                request.Id,
                sessionId,
                request.Prompt,
                ToCoreDelivery(request.Delivery),
                request.Resume));
            var response = new Schema.SessionInputAdmitted(
                admitted.AdmittedSeq,
                admitted.MessageId,
                admitted.SessionId,
                new Schema.Prompt(
                    admitted.Prompt.Text ?? string.Empty,
                    admitted.Prompt.Files?.Select(file => new Schema.FileAttachment(file.Uri, file.Mime ?? "", file.Name, null, null)).ToArray(),
                    admitted.Prompt.Agents?.Select(agent => new Schema.AgentAttachment(agent, null)).ToArray()),
                admitted.Delivery == SessionInputDelivery.Queue ? Schema.SessionDelivery.Queue : Schema.SessionDelivery.Steer,
                admitted.TimeCreated,
                null);
            return Results.Ok(new SessionPromptResponse(response));
        }).WithName("SessionPrompt");

        endpoints.MapPost("/session/{sessionId}/abort", async (string sessionId, ISessionService sessions) =>
        {
            await sessions.InterruptAsync(sessionId);
            return Results.Ok(true);
        }).WithName("SessionAbort");

        endpoints.MapPost("/session/{sessionId}/interrupt", async (string sessionId, ISessionService sessions) =>
        {
            await sessions.InterruptAsync(sessionId);
            return Results.Ok(true);
        }).WithName("SessionInterrupt");

        endpoints.MapPost("/session/{sessionId}/resume", async (string sessionId, ISessionService sessions) =>
        {
            await sessions.ResumeAsync(sessionId);
            return Results.Ok(true);
        }).WithName("SessionResume");

        return endpoints;
    }

    public static async Task InitializeOpenCodeAsync(this WebApplication app)
    {
        var paths = GlobalPathsBuilder.Create();
        await GlobalPathsBuilder.EnsureDirectoriesAsync(paths);
        await app.Services.GetRequiredService<Database>().InitializeAsync();

        var directory = app.Services.GetRequiredService<OpenCodeServerOptions>().Directory;
        var fs = app.Services.GetRequiredService<IFsUtil>();
        var tools = app.Services.GetRequiredService<IToolRegistry>();
        await tools.RegisterAsync("read", new ReadTool(fs, directory));
        await tools.RegisterAsync("write", new WriteTool(fs, directory));
        await tools.RegisterAsync("edit", new EditTool(fs, directory));
        await tools.RegisterAsync("glob", new GlobTool(fs, directory));
        await tools.RegisterAsync("grep", new GrepTool(fs, directory));
        await tools.RegisterAsync("bash", new BashTool(fs, directory));
    }

    private static Schema.AgentInfo ToSchemaAgent(OpenCode.Core.AgentInfo agent)
    {
        Schema.ModelRef? model = null;
        if (!string.IsNullOrWhiteSpace(agent.Model) && ModelService.TryParseModelRef(agent.Model, out var providerId, out var modelId))
            model = new Schema.ModelRef(modelId, providerId, agent.Variant);

        return new Schema.AgentInfo(
            agent.Id,
            model,
            new Schema.ProviderRequest(new Dictionary<string, string>(), new Dictionary<string, object>()),
            agent.System,
            agent.Description,
            ParseAgentMode(agent.Mode),
            agent.Hidden.GetValueOrDefault(),
            agent.Color,
            agent.Steps,
            agent.Permissions?.ToArray() ?? []);
    }

    private static Schema.ProjectInfo ToSchemaProject(OpenCode.Core.ProjectInfo project) => new(
        project.Id,
        project.Directory,
        project.Vcs?.Type.Equals("git", StringComparison.OrdinalIgnoreCase) == true ? Schema.ProjectVcs.Git : null,
        Path.GetFileName(project.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
        null,
        null,
        new Schema.ProjectTime(project.Created, project.Updated, null),
        []);

    private static Schema.AgentMode ParseAgentMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        "subagent" => Schema.AgentMode.Subagent,
        "all" => Schema.AgentMode.All,
        _ => Schema.AgentMode.Primary,
    };

    private static SessionInputDelivery? ToCoreDelivery(Schema.SessionDelivery? delivery) => delivery switch
    {
        Schema.SessionDelivery.Steer => SessionInputDelivery.Steer,
        Schema.SessionDelivery.Queue => SessionInputDelivery.Queue,
        _ => null,
    };

    private static async Task StreamEventsAsync(HttpContext context, IEventService events)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");

        var channel = Channel.CreateUnbounded<OpenCode.Core.EventPayload>();
        using var subscription = events.Subscribe(payload => channel.Writer.TryWrite(payload));

        await WriteSseAsync(context, new { id = Guid.NewGuid().ToString("N"), type = "server.connected", properties = new { } });
        try
        {
            await foreach (var payload in channel.Reader.ReadAllAsync(context.RequestAborted))
            {
                await WriteSseAsync(context, new
                {
                    id = payload.Id,
                    type = payload.Type,
                    properties = payload.Data,
                });
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
    }

    private static async Task WriteSseAsync(HttpContext context, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await context.Response.WriteAsync($"event: message\nid: {Guid.NewGuid():N}\ndata: {json}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}

public record VcsInfoResponse(string? Branch, string? DefaultBranch);

public sealed class ServerModelResolver(IConfiguration configuration) : IModelResolver
{
    public Task<string> ResolveAsync(Schema.SessionInfo session)
    {
        if (session.Model is not null)
            return Task.FromResult($"{session.Model.ProviderId}/{session.Model.Id}");

        var configured = configuration["OpenCode:Model"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Task.FromResult(configured);

        throw new InvalidOperationException(
            "No model is configured. Set OpenCode:Model (for example openai/gpt-4o-mini) or provide model when creating the session.");
    }
}

public static class ServerErrors
{
    public static async Task WriteAsync(HttpContext context)
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        (int Status, ApiError Error) result = exception switch
        {
            OpenCode.Core.SessionNotFoundError notFound => (StatusCodes.Status404NotFound, new OpenCode.Protocol.SessionNotFoundError(notFound.SessionId, notFound.Message)),
            QuestionNotFoundException notFound => (StatusCodes.Status404NotFound, new OpenCode.Protocol.QuestionNotFoundError(notFound.RequestId, notFound.Message)),
            PermissionBlockedError blocked => (StatusCodes.Status403Forbidden, new ForbiddenError(blocked.Message)),
            InvalidCursorException invalid => (StatusCodes.Status400BadRequest, new InvalidCursorError(invalid.Message)),
            InvalidFilePathException invalid => (StatusCodes.Status400BadRequest, new InvalidRequestError(invalid.Message, "Query", "path")),
            InvalidSearchPatternException invalid => (StatusCodes.Status400BadRequest, new InvalidRequestError(invalid.Message, "Query", "pattern")),
            _ => (StatusCodes.Status500InternalServerError, new UnknownError("An unexpected server error occurred.")),
        };

        context.Response.StatusCode = result.Status;
        await context.Response.WriteAsJsonAsync(result.Error);
    }
}

internal sealed class InvalidCursorException(string message) : Exception(message);

internal record SessionCursorState(
    string? Directory,
    string? Workspace,
    string? Project,
    string? Subpath,
    string Order,
    string? Search,
    Schema.SessionListAnchor? Anchor);

internal static class SessionCursorCodec
{
    public static SessionCursorState Decode(
        string? directory,
        string? workspace,
        string? project,
        string? subpath,
        string? cursor,
        string? order,
        string? search)
    {
        if (cursor is null)
            return new SessionCursorState(directory, workspace, project, subpath, order ?? "desc", search, null);
        var state = OpaqueCursor.Decode<SessionCursorState>(cursor);
        if (state.Order is not ("asc" or "desc") || state.Anchor is null ||
            state.Anchor.Direction is not ("previous" or "next") || string.IsNullOrWhiteSpace(state.Anchor.Id))
            throw new InvalidCursorException("Invalid cursor");
        return state;
    }

    public static string Encode(SessionCursorState state) => OpaqueCursor.Encode(state);
}

internal record MessageCursorState(string Id, string Order, string Direction);

internal static class MessageCursorCodec
{
    public static (string Order, SessionMessageCursor? Cursor) Decode(string? cursor, string? order)
    {
        if (cursor is null) return (order?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true ? "asc" : "desc", null);
        var state = OpaqueCursor.Decode<MessageCursorState>(cursor);
        if (state.Order is not ("asc" or "desc") || state.Direction is not ("previous" or "next") || string.IsNullOrWhiteSpace(state.Id))
            throw new InvalidCursorException("Invalid cursor");
        return (state.Order, new SessionMessageCursor(state.Id, state.Direction));
    }

    public static string Encode(string id, string order, string direction) =>
        OpaqueCursor.Encode(new MessageCursorState(id, order, direction));
}

internal static class OpaqueCursor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Encode<T>(T value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static T Decode<T>(string value)
    {
        try
        {
            var encoded = value.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(encoded), JsonOptions)
                ?? throw new InvalidCursorException("Invalid cursor");
        }
        catch (InvalidCursorException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or NotSupportedException)
        {
            throw new InvalidCursorException("Invalid cursor");
        }
    }
}
