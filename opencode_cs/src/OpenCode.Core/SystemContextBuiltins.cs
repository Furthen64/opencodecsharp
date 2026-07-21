namespace OpenCode.Core;

public static class SystemContextBuiltins
{
    public static async Task RegisterAsync(ISystemContextRegistryService registry, string workingDirectory, string projectDirectory, bool isGitRepo, string platform)
    {
        var envSource = SystemContextFactory.MakeStringSource(
            SystemContextKey.Make("core/environment"),
            load: async () =>
            {
                var env = $"""
                    <env>
                      Working directory: {workingDirectory}
                      Workspace root folder: {projectDirectory}
                      Is directory a git repo: {(isGitRepo ? "yes" : "no")}
                      Platform: {platform}
                    </env>
                    """;
                return await Task.FromResult(env);
            },
            baseline: env => $"Here is some useful information about the environment you are running in:\n{env}",
            update: (_, env) => $"The environment you are running in is now:\n{env}"
        );

        var dateSource = SystemContextFactory.MakeStringSource(
            SystemContextKey.Make("core/date"),
            load: async () => await Task.FromResult(DateTime.Now.ToString("ddd MMM dd yyyy")),
            baseline: date => $"Today's date: {date}",
            update: (_, date) => $"Today's date is now: {date}"
        );

        var builder = SystemContextBuilder.Empty()
            .AddSource(envSource)
            .AddSource(dateSource);

        await registry.RegisterAsync(new SystemContextRegistryEntry(
            SystemContextKey.Make("core/builtins"),
            () => Task.FromResult(builder)
        ));
    }
}
