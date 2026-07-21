using System.Text.Json;

namespace OpenCode.Core;

public record InstructionFile(
    string Path,
    string Content
);

public static class InstructionContext
{
    static readonly SystemContextKey Key = SystemContextKey.Make("core/instructions");

    public static SystemContextBuilder CreateSource(IReadOnlyList<InstructionFile> files)
    {
        var source = SystemContextFactory.MakeJsonSource(
            Key,
            load: async () => await Task.FromResult(JsonSerializer.SerializeToElement(files)),
            baseline: element =>
            {
                var parsed = JsonSerializer.Deserialize<List<InstructionFile>>(element);
                if (parsed == null || parsed.Count == 0) return string.Empty;
                return Render(parsed);
            },
            update: (_, current) =>
            {
                var parsed = JsonSerializer.Deserialize<List<InstructionFile>>(current);
                if (parsed == null || parsed.Count == 0) return string.Empty;
                return $"These instructions replace all previously loaded ambient instructions.\n\n{Render(parsed)}";
            },
            removed: _ => "Previously loaded instructions no longer apply."
        );

        var builder = SystemContextBuilder.Empty().AddSource(source);
        return builder;
    }

    public static SystemContextBuilder CreateUnavailableSource()
    {
        var source = SystemContextFactory.MakeJsonSource(
            Key,
            load: async () => await Task.FromResult(default(JsonElement)),
            baseline: _ => string.Empty,
            update: (_, _) => string.Empty
        );
        return SystemContextBuilder.Empty().AddSource(source);
    }

    public static async Task<SystemContextBuilder> DiscoverAndLoadAsync(
        IFsUtil fs,
        string configDirectory,
        string workingDirectory,
        string projectDirectory,
        bool disableProjectConfig = false)
    {
        var discoveredPaths = new HashSet<string>();

        if (!disableProjectConfig && IsInsideProject(workingDirectory, projectDirectory))
        {
            var upResult = await fs.UpAsync(["AGENTS.md"], workingDirectory, projectDirectory);
            foreach (var path in upResult)
                discoveredPaths.Add(path);
        }

        var configAgentPath = Path.Combine(configDirectory, "AGENTS.md");
        discoveredPaths.Add(configAgentPath);

        var files = new List<InstructionFile>();
        foreach (var path in discoveredPaths)
        {
            var content = await fs.ReadFileStringSafeAsync(path);
            if (content != null)
                files.Add(new InstructionFile(path, content));
        }

        if (files.Count == 0)
            return SystemContextBuilder.Empty();

        return CreateSource(files);
    }

    static bool IsInsideProject(string workingDirectory, string projectDirectory)
    {
        var relative = Path.GetRelativePath(projectDirectory, workingDirectory);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}") && !Path.IsPathRooted(relative);
    }

    static string Render(IReadOnlyList<InstructionFile> files)
    {
        return string.Join("\n\n", files.Select(f => $"Instructions from: {f.Path}\n{f.Content}"));
    }
}
