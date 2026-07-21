namespace OpenCode.Core;

public record SkillToolInput(
    string Name
);

public record SkillToolOutput(
    string Name,
    string Directory,
    string Output
);

public class SkillToolImpl : Tool
{
    readonly IFsUtil fs;
    readonly ISkillService skills;
    readonly IPermissionService permission;
    const int FileLimit = 10;

    public SkillToolImpl(IFsUtil fs, ISkillService skills, IPermissionService permission)
        : base("skill",
            "Load a specialized skill when the task at hand matches one of the available skills in the system context.\n\n" +
            "Use this tool to inject the skill's instructions and resources into the current conversation. The output may contain detailed workflow guidance as well as references to scripts, files, etc. in the same directory as the skill.\n\n" +
            "The skill name must match one of the available skills in the system context.")
    {
        this.fs = fs;
        this.skills = skills;
        this.permission = permission;
    }

    public override async Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
    {
        if (input is not SkillToolInput skillInput)
            throw new ToolFailure("Invalid input for skill tool");

        var current = await skills.ListAsync();
        var skill = current.FirstOrDefault(s => s.Name == skillInput.Name);
        if (skill == null)
            throw new ToolFailure($"Unable to load skill {skillInput.Name}");

        try
        {
            await permission.AssertAsync(new PermissionAssertInput(
                Id: null,
                SessionId: context.SessionId,
                Action: "skill",
                Resources: new[] { skill.Name },
                Save: new[] { skill.Name },
                Metadata: null,
                Source: new Schema.PermissionSource("tool", context.AssistantMessageId, context.ToolCallId),
                Agent: context.AgentId
            ));

            var directory = Path.GetDirectoryName(skill.Location) ?? "";
            string[] files = Array.Empty<string>();
            if (Path.GetFileName(skill.Location) == "SKILL.md" && await fs.ExistsAsync(directory))
            {
                var entries = await fs.ReadDirectoryEntriesAsync(directory);
                files = entries
                    .Where(e => e.Type == "file" && e.Name != "SKILL.md")
                    .Select(e => e.Name)
                    .OrderBy(n => n)
                    .Take(FileLimit)
                    .ToArray();
            }

            var output = FormatSkillOutput(skill, directory, files);
            return new ToolOutput(
                new List<ToolOutputContent> { new ToolTextContent { Text = output } },
                new SkillToolOutput(skill.Name, directory, output)
            );
        }
        catch (Exception ex) when (ex is not ToolFailure)
        {
            throw new ToolFailure($"Unable to load skill {skillInput.Name}: {ex.Message}");
        }
    }

    static string FormatSkillOutput(Schema.SkillInfo skill, string directory, string[] files)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"<skill_content name=\"{skill.Name}\">");
        sb.AppendLine($"# Skill: {skill.Name}");
        sb.AppendLine();
        sb.AppendLine(skill.Content?.Trim() ?? "");
        sb.AppendLine();
        sb.AppendLine($"Base directory for this skill: {directory}");
        sb.AppendLine("Relative paths in this skill (e.g., scripts/, reference/) are relative to this base directory.");
        sb.AppendLine("Note: file list is sampled.");
        sb.AppendLine();
        sb.AppendLine("<skill_files>");
        foreach (var file in files)
            sb.AppendLine($"<file>{file}</file>");
        sb.AppendLine("</skill_files>");
        sb.AppendLine("</skill_content>");
        return sb.ToString();
    }

    public override ToolDefinition ToDefinition(string name)
    {
        return new ToolDefinition(
            name,
            Description,
            new { name = "(string) The name of the skill from the available skills list" },
            null
        );
    }
}
