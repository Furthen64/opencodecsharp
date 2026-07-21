using System.Text;

namespace OpenCode.Core;

public record PatchToolInput(
    string PatchText
);

public record PatchToolApplied(
    string Type,
    string Resource,
    string Target
);

public record PatchToolOutput(
    PatchToolApplied[] Applied,
    Schema.FileDiff[] Files
);

public class ApplyPatchTool : Tool
{
    readonly IFsUtil fs;
    readonly ILocationMutationService locationMutation;
    readonly IFileMutationService fileMutation;
    readonly IPermissionService permission;
    readonly string locationDirectory;

    public ApplyPatchTool(
        IFsUtil fs,
        ILocationMutationService locationMutation,
        IFileMutationService fileMutation,
        IPermissionService permission,
        string locationDirectory
    )
        : base("apply_patch", "Apply one patch containing add, update, and delete file operations. All targets are resolved and approved before target contents are read. Operations apply sequentially; if a later operation fails, earlier operations remain applied and the failure reports them explicitly. Moves and atomic rollback are not supported yet.", "edit")
    {
        this.fs = fs;
        this.locationMutation = locationMutation;
        this.fileMutation = fileMutation;
        this.permission = permission;
        this.locationDirectory = locationDirectory;
    }

    public override async Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
    {
        if (input is not PatchToolInput patchInput)
            throw new ToolFailure("Invalid input for apply_patch tool");

        var applied = new List<PatchToolApplied>();
        string FailPrefix(string path) =>
            applied.Count == 0
                ? $"Unable to apply patch at {path}"
                : $"Patch partially applied before failing at {path}. Applied: {string.Join(", ", applied.Select(a => a.Resource))}";

        if (string.IsNullOrWhiteSpace(patchInput.PatchText))
            throw new ToolFailure("patchText is required");

        PatchHunk[] hunks;
        try
        {
            hunks = PatchParser.Parse(patchInput.PatchText);
        }
        catch (Exception ex)
        {
            throw new ToolFailure($"apply_patch verification failed: {ex.Message}");
        }

        if (hunks.Length == 0)
            throw new ToolFailure("patch rejected: empty patch");

        if (hunks.OfType<PatchUpdateHunk>().Any(h => h.MovePath != null))
            throw new ToolFailure("apply_patch moves are not supported yet");

        var targets = new List<(PatchHunk hunk, LocationMutationTarget target)>();
        foreach (var hunk in hunks)
        {
            targets.Add((hunk, await locationMutation.ResolveAsync(new LocationMutationResolveInput(hunk.Path, "file"))));
        }

        foreach (var target in targets.Select(t => t.target).Where(t => t.ExternalDirectory != null).Select(t => t.ExternalDirectory!).DistinctBy(e => e.Resource))
        {
            await permission.AssertAsync(new PermissionAssertInput(
                Id: null,
                SessionId: context.SessionId,
                Action: target.Action,
                Resources: new[] { target.Resource },
                Save: new[] { target.Save },
                Metadata: null,
                Source: new Schema.PermissionSource("tool", context.AssistantMessageId, context.ToolCallId),
                Agent: context.AgentId
            ));
        }

        var resourceList = targets.Select(t => t.target.Resource).Distinct().ToArray();
        await permission.AssertAsync(new PermissionAssertInput(
            Id: null,
            SessionId: context.SessionId,
            Action: "edit",
            Resources: resourceList,
            Save: new[] { "*" },
            Metadata: null,
            Source: new Schema.PermissionSource("tool", context.AssistantMessageId, context.ToolCallId),
            Agent: context.AgentId
        ));

        var prepared = new List<PreparedHunk>();
        foreach (var (hunk, target) in targets)
        {
            try
            {
                if (hunk is PatchAddHunk addHunk)
                {
                    prepared.Add(new PreparedHunk
                    {
                        Hunk = addHunk,
                        Target = target,
                        Before = "",
                        After = EnsureNewline(addHunk.Contents),
                        Type = "add"
                    });
                    continue;
                }
                if (hunk is PatchDeleteHunk deleteHunk)
                {
                    var existing = await fs.ReadFileBytesAsync(target.Canonical);
                    var original = Encoding.UTF8.GetString(existing);
                    var before = StripBom(original);
                    prepared.Add(new PreparedHunk
                    {
                        Hunk = deleteHunk,
                        Target = target,
                        Before = before,
                        After = "",
                        Type = "delete"
                    });
                    continue;
                }
                if (hunk is PatchUpdateHunk updateHunk)
                {
                    var existing = await fs.ReadFileBytesAsync(target.Canonical);
                    var original = Encoding.UTF8.GetString(existing);
                    var before = StripBom(original);
                    var result = PatchParser.Derive(updateHunk.Path, updateHunk.Chunks, original);
                    prepared.Add(new PreparedHunk
                    {
                        Hunk = updateHunk,
                        Target = target,
                        Source = existing,
                        Content = PatchParser.JoinBom(result.Content, result.Bom),
                        Before = before,
                        After = result.Content,
                        Type = "update"
                    });
                    continue;
                }
            }
            catch
            {
                throw new ToolFailure(FailPrefix(hunk.Path));
            }
        }

        var patchFiles = prepared.Select(p => ComputeFileDiff(p)).ToList();

        foreach (var change in prepared)
        {
            try
            {
                if (change.Type == "add")
                {
                    var result = await fileMutation.CreateAsync(new FileMutationWriteInput(
                        Target: new FileMutationTarget(change.Target.Canonical, change.Target.Resource),
                        Content: EnsureNewline(change.Hunk is PatchAddHunk add ? add.Contents : "")
                    ));
                    applied.Add(new PatchToolApplied("add", result.Resource, result.Target));
                }
                else if (change.Type == "delete")
                {
                    var result = await fileMutation.RemoveAsync(new FileMutationRemoveInput(
                        Target: new FileMutationTarget(change.Target.Canonical, change.Target.Resource)
                    ));
                    applied.Add(new PatchToolApplied("delete", result.Resource, result.Target));
                }
                else
                {
                    var result = await fileMutation.WriteIfUnchangedAsync(new FileMutationConditionalWriteInput(
                        Target: new FileMutationTarget(change.Target.Canonical, change.Target.Resource),
                        Expected: change.Source!,
                        Content: change.Content!
                    ));
                    applied.Add(new PatchToolApplied("update", result.Resource, result.Target));
                }
            }
            catch
            {
                throw new ToolFailure(FailPrefix(change.Hunk.Path));
            }
        }

        return new ToolOutput(
            new List<ToolOutputContent>
            {
                new ToolTextContent { Text = FormatModelOutput(applied) }
            },
            new PatchToolOutput(applied.ToArray(), patchFiles.ToArray())
        );
    }

    static string FormatModelOutput(List<PatchToolApplied> applied)
    {
        var sb = new StringBuilder("Applied patch sequentially:\n");
        foreach (var item in applied)
        {
            var prefix = item.Type == "add" ? "A" : item.Type == "delete" ? "D" : "M";
            sb.AppendLine($"{prefix} {item.Resource}");
        }
        return sb.ToString();
    }

    static Schema.FileDiff ComputeFileDiff(PreparedHunk change)
    {
        var before = change.Before ?? "";
        var after = change.After ?? "";
        var beforeLines = before.Split('\n');
        var afterLines = after.Split('\n');
        int additions = 0, deletions = 0;
        int maxLen = Math.Max(beforeLines.Length, afterLines.Length);
        for (int i = 0; i < maxLen; i++)
        {
            var old = i < beforeLines.Length ? beforeLines[i] : null;
            var @new = i < afterLines.Length ? afterLines[i] : null;
            if (old == @new) continue;
            if (old != null) deletions++;
            if (@new != null) additions++;
        }
        return new Schema.FileDiff(
            Path: change.Target.Resource,
            Status: change.Type == "add" ? "added" : change.Type == "delete" ? "deleted" : "modified",
            Additions: additions,
            Deletions: deletions,
            Patch: ""
        );
    }

    static string EnsureNewline(string content) =>
        string.IsNullOrEmpty(content) || content.EndsWith('\n') ? content : content + "\n";

    static string StripBom(string text) =>
        text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

    public override ToolDefinition ToDefinition(string name)
    {
        return new ToolDefinition(
            name,
            Description,
            new { patchText = "(string) The full patch text" },
            null
        );
    }

    class PreparedHunk
    {
        public PatchHunk Hunk { get; set; } = null!;
        public LocationMutationTarget Target { get; set; } = null!;
        public byte[]? Source { get; set; }
        public string? Content { get; set; }
        public string Before { get; set; } = "";
        public string After { get; set; } = "";
        public string Type { get; set; } = "";
    }
}
