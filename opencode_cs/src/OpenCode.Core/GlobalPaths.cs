using System;
using System.IO;
using System.Threading.Tasks;

namespace OpenCode.Core;

public record GlobalPaths(
    string Home,
    string Data,
    string Cache,
    string Config,
    string State,
    string Tmp,
    string Bin,
    string Log,
    string Repos
);

public static class GlobalPathsBuilder
{
    const string App = "opencode";

    public static GlobalPaths Create(GlobalPaths? overrides = null)
    {
        var home = overrides?.Home ?? Environment.GetEnvironmentVariable("OPENCODE_TEST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = overrides?.Data ?? GetXdgData();
        var cache = overrides?.Cache ?? GetXdgCache();
        var config = overrides?.Config ?? Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR") ?? GetXdgConfig();
        var state = overrides?.State ?? GetXdgState();
        var tmp = overrides?.Tmp ?? Path.Combine(Path.GetTempPath(), App);
        var bin = overrides?.Bin ?? Path.Combine(cache, "bin");
        var log = overrides?.Log ?? Path.Combine(data, "log");
        var repos = overrides?.Repos ?? Path.Combine(data, "repos");

        return new GlobalPaths(home, data, cache, config, state, tmp, bin, log, repos);
    }

    static string GetXdgData() => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is string d && !string.IsNullOrEmpty(d) ? Path.Combine(d, App) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), App);
    static string GetXdgCache() => Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is string c && !string.IsNullOrEmpty(c) ? Path.Combine(c, App) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), App);
    static string GetXdgConfig() => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is string c && !string.IsNullOrEmpty(c) ? Path.Combine(c, App) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), App);
    static string GetXdgState() => Environment.GetEnvironmentVariable("XDG_STATE_HOME") is string s && !string.IsNullOrEmpty(s) ? Path.Combine(s, App) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), App);

    public static Task EnsureDirectoriesAsync(GlobalPaths paths)
    {
        var dirs = new[] { paths.Data, paths.Config, paths.State, paths.Tmp, paths.Log, paths.Bin, paths.Repos };
        foreach (var d in dirs) Directory.CreateDirectory(d);
        return Task.CompletedTask;
    }
}
