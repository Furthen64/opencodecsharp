using OpenCode.Core;

namespace OpenCode.Tests;

public sealed class SnapshotServiceTests
{
    [Fact]
    public async Task CaptureDiffSelectiveRestoreAndCheckoutRoundTripWorkingTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"opencode-snapshot-{Guid.NewGuid():N}");
        var worktree = Path.Combine(root, "worktree");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(worktree);
        var git = new GitService(new FsUtil());
        Assert.Equal(0, await git.RunAsync(worktree, ["init"]));
        Assert.Equal(0, await git.RunAsync(worktree, ["config", "user.name", "OpenCode Test"]));
        Assert.Equal(0, await git.RunAsync(worktree, ["config", "user.email", "test@opencode.local"]));
        await File.WriteAllTextAsync(Path.Combine(worktree, "tracked.txt"), "before\n");
        Assert.Equal(0, await git.RunAsync(worktree, ["add", "tracked.txt"]));
        Assert.Equal(0, await git.RunAsync(worktree, ["commit", "-m", "initial"]));

        var snapshots = new SnapshotService(git, worktree, worktree, "project", data);
        var before = await snapshots.CaptureAsync();
        Assert.NotNull(before);
        await File.WriteAllTextAsync(Path.Combine(worktree, "tracked.txt"), "after\n");
        await File.WriteAllTextAsync(Path.Combine(worktree, "added.txt"), "new\n");
        var after = await snapshots.CaptureAsync();
        Assert.NotNull(after);

        var diff = await snapshots.DiffAsync(before.Value, after.Value);
        Assert.Equal(["added.txt", "tracked.txt"], diff.Select(file => file.Path).Order());
        Assert.Equal("added", Assert.Single(diff, file => file.Path == "added.txt").Status);

        await snapshots.RestoreAsync(new Dictionary<string, SnapshotId>
        {
            ["tracked.txt"] = before.Value,
            ["added.txt"] = before.Value,
        });
        Assert.Equal("before\n", await File.ReadAllTextAsync(Path.Combine(worktree, "tracked.txt")));
        Assert.False(File.Exists(Path.Combine(worktree, "added.txt")));

        await snapshots.CheckoutAsync(after.Value);
        Assert.Equal("after\n", await File.ReadAllTextAsync(Path.Combine(worktree, "tracked.txt")));
        Assert.Equal("new\n", await File.ReadAllTextAsync(Path.Combine(worktree, "added.txt")));

        Directory.Delete(root, true);
    }
}
