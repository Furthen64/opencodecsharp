using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Core;
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
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<IAgentService, AgentService>();
        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<IEventService, EventService>();
        services.AddSingleton<IQuestionService, QuestionService>();
        services.AddSingleton<ISkillService, SkillService>();
        services.AddSingleton<SessionStore>();
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

        endpoints.MapGet("/question", async (IQuestionService questions) =>
            Results.Ok(new QuestionRequestListResponse((await questions.ListAsync()).ToList())))
            .WithName("QuestionList");

        endpoints.MapGet("/session", async ([AsParameters] SessionsQuery query, ISessionService sessions) =>
        {
            var data = await sessions.ListAsync(new SessionListInput(
                query.Workspace,
                query.Search,
                query.Limit,
                query.Order?.ToString(),
                null,
                query.Directory,
                query.Project,
                query.Subpath));
            return Results.Ok(new SessionsResponse(data, new PaginationCursor(null, null)));
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

    private static Schema.AgentMode ParseAgentMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        "subagent" => Schema.AgentMode.Subagent,
        "all" => Schema.AgentMode.All,
        _ => Schema.AgentMode.Primary,
    };
}

public record VcsInfoResponse(string? Branch, string? DefaultBranch);

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
            _ => (StatusCodes.Status500InternalServerError, new UnknownError("An unexpected server error occurred.")),
        };

        context.Response.StatusCode = result.Status;
        await context.Response.WriteAsJsonAsync(result.Error);
    }
}
