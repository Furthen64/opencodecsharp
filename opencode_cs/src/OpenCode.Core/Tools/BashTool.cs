using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace OpenCode.Core;

public record BashToolInput(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("workdir")] string? Workdir,
    [property: JsonPropertyName("timeout")] int? Timeout
);

public record BashToolOutput(
    [property: JsonPropertyName("exit")] int? ExitCode,
    [property: JsonPropertyName("output")] string OutputText,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("timeout")] bool TimedOut,
    [property: JsonPropertyName("warnings")] List<string>? Warnings
);

public class BashTool : Tool
{
    const int DefaultTimeoutMs = 2 * 60 * 1000;
    const int MaxTimeoutMs = 10 * 60 * 1000;
    const int MaxCaptureBytes = 1024 * 1024;

    readonly IFsUtil fs;
    readonly string locationDirectory;

    public BashTool(IFsUtil fs, string locationDirectory)
        : base("bash", $"Execute one shell command string with the host user's filesystem, process, and network authority. The active Location is the default working directory. Timeout values are milliseconds (default: {DefaultTimeoutMs}; maximum: {MaxTimeoutMs}).")
    {
        this.fs = fs;
        this.locationDirectory = locationDirectory;
    }

    public override async Task<ToolOutput> ExecuteAsync(object input, ToolContext context)
    {
        if (input is not BashToolInput bashInput)
            throw new ToolFailure("Invalid input for bash tool");

        var workdir = string.IsNullOrEmpty(bashInput.Workdir)
            ? locationDirectory
            : Path.IsPathRooted(bashInput.Workdir)
                ? bashInput.Workdir
                : Path.Combine(locationDirectory, bashInput.Workdir);

        workdir = await fs.ResolveAsync(workdir);

        if (!await fs.IsDirAsync(workdir))
            throw new ToolFailure($"Working directory is not a directory: {workdir}");

        var timeout = bashInput.Timeout ?? DefaultTimeoutMs;
        if (timeout > MaxTimeoutMs)
            timeout = MaxTimeoutMs;

        var shell = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? (Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
            : "/bin/sh";

        var shellArgs = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? $"/C {bashInput.Command}"
            : $"-c {QuoteShellArgument(bashInput.Command)}";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = shellArgs,
                WorkingDirectory = workdir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            var waitTask = WaitForExitAsync(process, cts.Token);
            await Task.WhenAll(outputTask, errorTask, waitTask);

            if (cts.IsCancellationRequested)
            {
                try { process.Kill(true); } catch { }

                var timeoutOutput = new BashToolOutput(null, $"Command exceeded timeout of {timeout} ms.", false, true, null);
                return new ToolOutput(
                    new List<ToolOutputContent>
                    {
                        new ToolTextContent { Text = $"Command exceeded timeout of {timeout} ms. Retry with a larger timeout if the command is expected to take longer." },
                        new ToolTextContent { Text = "Command timed out before completion." }
                    },
                    timeoutOutput
                );
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (!string.IsNullOrEmpty(error))
                output = string.IsNullOrEmpty(output) ? error : $"{output}{Environment.NewLine}{error}";
            var exitCode = process.ExitCode;

            if (output.Length > MaxCaptureBytes)
            {
                output = output[..MaxCaptureBytes] + "\n\n[output capture truncated at the in-memory safety limit]";
            }

            if (string.IsNullOrEmpty(output))
                output = "(no output)";

            var resultOutput = new BashToolOutput(exitCode, output, false, false, null);

            return new ToolOutput(
                new List<ToolOutputContent>
                {
                    new ToolTextContent { Text = output },
                    new ToolTextContent { Text = $"Command exited with code {exitCode}." }
                },
                resultOutput
            );
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }

            var timeoutOutput = new BashToolOutput(null, $"Command exceeded timeout of {timeout} ms.", false, true, null);
            return new ToolOutput(
                new List<ToolOutputContent>
                {
                    new ToolTextContent { Text = $"Command exceeded timeout of {timeout} ms." },
                    new ToolTextContent { Text = "Command timed out before completion." }
                },
                timeoutOutput
            );
        }
    }

    public override ToolDefinition ToDefinition(string name)
    {
        return new ToolDefinition(
            name,
            Description,
            new { command = "(string) Shell command", workdir = "(string?) Working directory", timeout = "(int?) Timeout in ms" },
            null
        );
    }

    static async Task WaitForExitAsync(Process process, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<bool>();
        var exitedHandler = new EventHandler((s, e) => tcs.TrySetResult(true));
        process.Exited += exitedHandler;
        process.EnableRaisingEvents = true;

        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, token));
        }
        finally
        {
            process.Exited -= exitedHandler;
        }
    }

    static string QuoteShellArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "''";
        if (!arg.Contains(' ') && !arg.Contains('\'') && !arg.Contains('"') && !arg.Contains('|')
            && !arg.Contains('&') && !arg.Contains(';') && !arg.Contains('<') && !arg.Contains('>'))
            return arg;

        return "'" + arg.Replace("'", "'\\''") + "'";
    }
}
