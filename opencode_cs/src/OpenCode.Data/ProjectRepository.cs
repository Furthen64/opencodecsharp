using System.Text.Json;
using OpenCode.Schema;

namespace OpenCode.Data;

public class ProjectRepository
{
    readonly Database db;
    public ProjectRepository(Database db) => this.db = db;

    public async Task<ProjectInfo?> GetAsync(string id)
    {
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<ProjectInfo?>(
            "SELECT * FROM \"project\" WHERE \"id\" = @Id",
            new { Id = id });
    }

    public async Task<ProjectInfo?> GetByWorktreeAsync(string worktree)
    {
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<ProjectInfo?>(
            "SELECT * FROM \"project\" WHERE \"worktree\" = @Worktree",
            new { Worktree = worktree });
    }

    public async Task UpsertAsync(ProjectInfo project)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO ""project"" (
                ""id"", ""worktree"", ""vcs"", ""name"",
                ""icon_url"", ""icon_url_override"", ""icon_color"",
                ""time_created"", ""time_updated"", ""time_initialized"",
                ""sandboxes"", ""commands""
            ) VALUES (
                @Id, @Worktree, @Vcs, @Name,
                @IconUrl, @IconUrlOverride, @IconColor,
                @TimeCreated, @TimeUpdated, @TimeInitialized,
                @Sandboxes, @Commands
            )
            ON CONFLICT(""id"") DO UPDATE SET
                ""worktree"" = @Worktree,
                ""vcs"" = @Vcs,
                ""name"" = @Name,
                ""icon_url"" = @IconUrl,
                ""icon_url_override"" = @IconUrlOverride,
                ""icon_color"" = @IconColor,
                ""time_created"" = @TimeCreated,
                ""time_updated"" = @TimeUpdated,
                ""time_initialized"" = @TimeInitialized,
                ""sandboxes"" = @Sandboxes,
                ""commands"" = @Commands",
            new
            {
                project.Id,
                project.Worktree,
                Vcs = project.Vcs?.ToString(),
                project.Name,
                IconUrl = project.Icon?.Url,
                IconUrlOverride = project.Icon?.Override,
                IconColor = project.Icon?.Color,
                project.Time.Created,
                project.Time.Updated,
                project.Time.Initialized,
                Sandboxes = JsonSerializer.Serialize(project.Sandboxes),
                Commands = project.Commands != null ? JsonSerializer.Serialize(project.Commands) : null,
            });
    }

    public async Task<List<ProjectInfo>> ListAsync()
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<ProjectInfo>(
            "SELECT * FROM \"project\" ORDER BY \"time_created\" DESC");
        return rows.ToList();
    }
}
