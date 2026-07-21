using System.Diagnostics;
using System.Text;

namespace OpenCode.Core;

public record GitRepository(
    string Worktree,
    string GitDirectory,
    string CommonDirectory
);

public enum GitOperation
{
    Clone,
    Fetch,
    Checkout,
    Reset,
    Create,
    Refresh,
    WriteTree,
    ListFiles,
    Diff,
    Restore
}

public class GitOperationError : Exception
{
    public GitOperation Operation { get; }
    public string? Directory { get; }

    public GitOperationError(GitOperation operation, string message, string? directory = null, Exception? cause = null)
        : base($"Git {operation}: {message}", cause)
    {
        Operation = operation;
        Directory = directory;
    }
}

public class GitWorktreeError : Exception
{
    public string Operation { get; }
    public string? Directory { get; }
    public bool? ForceRequired { get; }

    public GitWorktreeError(string operation, string message, string? directory = null, bool? forceRequired = null)
        : base($"Git worktree {operation}: {message}")
    {
        Operation = operation;
        Directory = directory;
        ForceRequired = forceRequired;
    }
}

public class GitPatchError : Exception
{
    public string Operation { get; }
    public string Directory { get; }

    public GitPatchError(string operation, string directory, string message, Exception? cause = null)
        : base($"Git patch {operation}: {message}", cause)
    {
        Operation = operation;
        Directory = directory;
    }
}

public interface IGitService
{
    Task<GitRepository?> DiscoverAsync(string directory);
    Task<GitRepository> CloneAsync(string remote, string directory, string? branch = null, int depth = 100);
    Task<GitRepository> CreateAsync(string worktree, string gitDirectory, GitRepository? seed = null);
    Task<string?> GetRemoteAsync(GitRepository repository, string name = "origin");
    Task<string?> GetHeadAsync(GitRepository repository);
    Task<string?> GetBranchAsync(GitRepository repository);
    Task<string?> GetDefaultRemoteBranchAsync(GitRepository repository, string remoteName = "origin");
    Task<string[]> GetRootCommitsAsync(GitRepository repository);
    Task FetchRemotesAsync(GitRepository repository, bool prune = true);
    Task<string> WriteTreeAsync(GitRepository repository);
    Task<string[]> TreeFilesAsync(GitRepository repository, string from, string to);
    Task<FileDiff[]> TreeDiffAsync(GitRepository repository, string from, string to, int context = 3, string[]? paths = null);
    Task<string> CapturePatchAsync(GitRepository repository, string path);
    Task ApplyPatchAsync(string directory, string patch);
    Task<int> RunAsync(string cwd, string[] args, StringBuilder? stdout = null, StringBuilder? stderr = null);
}

public class GitService : IGitService
{
    readonly IFsUtil fs;

    public GitService(IFsUtil fs)
    {
        this.fs = fs;
    }

    public async Task<GitRepository?> DiscoverAsync(string directory)
    {
        var dotgitResults = await fs.UpAsync([".git"], directory);
        var dotgit = dotgitResults.FirstOrDefault();
        if (dotgit == null) return null;

        var cwd = Path.GetDirectoryName(dotgit)!;
        var topLevel = await GitAsync(cwd, "rev-parse", "--show-toplevel");
        var gitDir = await GitAsync(cwd, "rev-parse", "--git-dir");
        var commonDir = await GitAsync(cwd, "rev-parse", "--git-common-dir");

        if (gitDir.ExitCode != 0 || commonDir.ExitCode != 0) return null;

        return new GitRepository(
            Worktree: ResolvePath(cwd, topLevel.Stdout),
            GitDirectory: ResolvePath(cwd, gitDir.Stdout),
            CommonDirectory: ResolvePath(cwd, commonDir.Stdout)
        );
    }

    public async Task<GitRepository> CloneAsync(string remote, string directory, string? branch = null, int depth = 100)
    {
        var parentDir = Path.GetDirectoryName(directory)!;
        var args = new List<string> { "clone", "--depth", depth.ToString() };
        if (branch != null) { args.Add("--branch"); args.Add(branch); }
        args.AddRange(["--", remote, directory]);

        var result = await RunGitCommandAsync(parentDir, args.ToArray());
        if (result.ExitCode != 0)
            throw new GitOperationError(GitOperation.Clone, result.Stderr, directory);

        var repo = await DiscoverAsync(directory);
        if (repo != null) return repo;
        throw new GitOperationError(GitOperation.Clone, "Cloned repository could not be opened", directory);
    }

    public async Task<GitRepository> CreateAsync(string worktree, string gitDirectory, GitRepository? seed = null)
    {
        await fs.EnsureDirAsync(gitDirectory);
        var repository = new GitRepository(worktree, gitDirectory, gitDirectory);

        await RunRepositoryCommandAsync(GitOperation.Create, repository, ["init"]);

        var configs = new[]
        {
            ("core.autocrlf", "false"),
            ("core.longpaths", "true"),
            ("core.symlinks", "true"),
            ("core.fsmonitor", "false"),
            ("feature.manyFiles", "true"),
            ("index.version", "4"),
            ("index.threads", "true"),
            ("core.untrackedCache", "true"),
        };

        foreach (var (key, value) in configs)
            await RunRepositoryCommandAsync(GitOperation.Create, repository, ["config", key, value]);

        if (seed == null) return repository;

        var objectsInfoDir = Path.Combine(gitDirectory, "objects", "info");
        await fs.EnsureDirAsync(objectsInfoDir);

        var alternatesPath = Path.Combine(objectsInfoDir, "alternates");
        await fs.WriteWithDirsAsync(alternatesPath, Path.Combine(seed.CommonDirectory, "objects") + "\n");

        var seedIndexPath = Path.Combine(seed.GitDirectory, "index");
        var targetIndexPath = Path.Combine(gitDirectory, "index");
        if (File.Exists(seedIndexPath))
            File.Copy(seedIndexPath, targetIndexPath, overwrite: true);

        return repository;
    }

    public async Task<string?> GetRemoteAsync(GitRepository repository, string name = "origin")
    {
        var result = await RunGitCommandAsync(repository.Worktree, ["remote", "get-url", name]);
        if (result.ExitCode != 0) return null;
        return result.Stdout.Trim() == "" ? null : result.Stdout.Trim();
    }

    public async Task<string?> GetHeadAsync(GitRepository repository)
    {
        var result = await RunGitCommandAsync(repository.Worktree, ["rev-parse", "HEAD"]);
        if (result.ExitCode != 0) return null;
        return result.Stdout.Trim() == "" ? null : result.Stdout.Trim();
    }

    public async Task<string?> GetBranchAsync(GitRepository repository)
    {
        var result = await RunGitCommandAsync(repository.Worktree, ["symbolic-ref", "--quiet", "--short", "HEAD"]);
        if (result.ExitCode != 0) return null;
        return result.Stdout.Trim() == "" ? null : result.Stdout.Trim();
    }

    public async Task<string?> GetDefaultRemoteBranchAsync(GitRepository repository, string remoteName = "origin")
    {
        var result = await RunGitCommandAsync(repository.Worktree, ["symbolic-ref", $"refs/remotes/{remoteName}/HEAD"]);
        if (result.ExitCode != 0) return null;
        return result.Stdout.Trim().Replace($"refs/remotes/{remoteName}/", "").Trim() == ""
            ? null
            : result.Stdout.Trim().Replace($"refs/remotes/{remoteName}/", "").Trim();
    }

    public async Task<string[]> GetRootCommitsAsync(GitRepository repository)
    {
        var result = await RunGitCommandAsync(repository.Worktree, ["rev-list", "--max-parents=0", "HEAD"]);
        if (result.ExitCode != 0) return [];
        return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).OrderBy(x => x).ToArray();
    }

    public async Task FetchRemotesAsync(GitRepository repository, bool prune = true)
    {
        var args = new List<string> { "fetch", "--all" };
        if (prune) args.Add("--prune");
        await RunGitCommandAsync(repository.Worktree, args.ToArray());
    }

    public async Task<string> WriteTreeAsync(GitRepository repository)
    {
        var result = await RunRepositoryCommandAsync(GitOperation.WriteTree, repository, ["write-tree"]);
        return result.Stdout.Trim();
    }

    public async Task<string[]> TreeFilesAsync(GitRepository repository, string from, string to)
    {
        var result = await RunRepositoryCommandAsync(GitOperation.ListFiles, repository,
            ["diff", "--name-only", "-z", from, to]);
        return result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim()).Where(f => f.Length > 0).ToArray();
    }

    public async Task<FileDiff[]> TreeDiffAsync(GitRepository repository, string from, string to, int context = 3, string[]? paths = null)
    {
        var files = paths ?? await TreeFilesAsync(repository, from, to);
        var diffs = new List<FileDiff>();

        foreach (var file in files)
        {
            var statusResult = await RunRepositoryCommandAsync(GitOperation.Diff, repository,
                ["diff", "--name-status", "--no-renames", from, to, "--", file]);
            var statusText = statusResult.Stdout.Trim();
            var status = statusText.StartsWith("A") ? "added" : statusText.StartsWith("D") ? "deleted" : "modified";

            var statsResult = await RunRepositoryCommandAsync(GitOperation.Diff, repository,
                ["diff", "--numstat", "--no-renames", from, to, "--", file]);
            var stats = statsResult.Stdout.Split('\t');
            var binary = stats.Length < 2 || stats[0] == "-" || stats[1] == "-";

            var patch = "";
            if (!binary)
            {
                var patchResult = await RunRepositoryCommandAsync(GitOperation.Diff, repository,
                    ["diff", $"--unified={context}", "--no-renames", from, to, "--", file]);
                patch = patchResult.Stdout;
            }

            diffs.Add(new FileDiff(
                Path: file,
                Status: status,
                Additions: binary ? 0 : int.TryParse(stats.ElementAtOrDefault(0), out var a) ? a : 0,
                Deletions: binary ? 0 : int.TryParse(stats.ElementAtOrDefault(1), out var d) ? d : 0,
                Patch: patch
            ));
        }

        return diffs.ToArray();
    }

    public async Task<string> CapturePatchAsync(GitRepository repository, string path)
    {
        var scope = Path.GetRelativePath(repository.Worktree, path).Replace('\\', '/');
        if (string.IsNullOrEmpty(scope)) scope = ".";

        var tracked = await RunGitCommandAsync(repository.Worktree, ["diff", "--binary", "HEAD", "--", scope]);

        var untracked = await RunGitCommandAsync(repository.Worktree,
            ["ls-files", "--others", "--exclude-standard", "-z", "--", scope]);

        var patches = new List<string>();
        if (tracked.ExitCode == 0 && !string.IsNullOrEmpty(tracked.Stdout))
            patches.Add(tracked.Stdout);

        var untrackedFiles = untracked.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var file in untrackedFiles)
        {
            var result = await RunGitCommandAsync(repository.Worktree,
                ["diff", "--binary", "--no-index", "--", "/dev/null", file]);
            if (result.ExitCode == 0 || result.ExitCode == 1)
                patches.Add(result.Stdout);
        }

        return string.Join("\n", patches.Where(p => !string.IsNullOrEmpty(p)));
    }

    public async Task ApplyPatchAsync(string directory, string patch)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "apply -",
                WorkingDirectory = directory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.StandardInput.WriteAsync(patch);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new GitPatchError("apply", directory, stderr.Trim());
        }
    }

    public async Task<int> RunAsync(string cwd, string[] args, StringBuilder? stdout = null, StringBuilder? stderr = null)
    {
        var result = await RunGitCommandAsync(cwd, args);
        stdout?.Append(result.Stdout);
        stderr?.Append(result.Stderr);
        return result.ExitCode;
    }

    record GitCommandResult(int ExitCode, string Stdout, string Stderr);

    async Task<GitCommandResult> RunGitCommandAsync(string cwd, string[] args)
    {
        return await RunGitCommandInternalAsync(cwd, args);
    }

    async Task<GitCommandResult> RunRepositoryCommandAsync(GitOperation operation, GitRepository repository, string[] args, string? stdin = null)
    {
        var fullArgs = new List<string>
        {
            "--git-dir", repository.GitDirectory,
            "--work-tree", repository.Worktree
        };
        fullArgs.AddRange(args);

        var result = await RunGitCommandInternalAsync(repository.Worktree, fullArgs.ToArray(), stdin);

        if (result.ExitCode == 0) return result;

        var message = string.IsNullOrEmpty(result.Stderr) ? result.Stdout : result.Stderr;
        throw new GitOperationError(operation, message.Trim(), repository.Worktree);
    }

    async Task<GitCommandResult> GitAsync(string cwd, params string[] args)
    {
        return await RunGitCommandInternalAsync(cwd, args);
    }

    static async Task<GitCommandResult> RunGitCommandInternalAsync(string cwd, string[] args, string? stdin = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(" ", args.Select(QuoteArg)),
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = startInfo };
        process.Start();

        if (stdin != null)
        {
            await process.StandardInput.WriteAsync(stdin);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new GitCommandResult(process.ExitCode, stdout, stderr);
    }

    static string QuoteArg(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"'))
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        return arg;
    }

    static string ResolvePath(string cwd, string value)
    {
        var trimmed = value.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(trimmed)) return cwd;
        if (Path.IsPathRooted(trimmed)) return Path.GetFullPath(trimmed);
        return Path.GetFullPath(Path.Combine(cwd, trimmed));
    }
}
