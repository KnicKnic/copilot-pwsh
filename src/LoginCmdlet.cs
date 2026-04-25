using System.Diagnostics;
using System.Management.Automation;

namespace CopilotShell;

/// <summary>
/// Authenticates with GitHub Copilot via the CLI's OAuth device flow.
/// Ensures the correct CLI version is downloaded, then runs <c>copilot login</c>
/// interactively so the user can complete the browser-based authentication.
/// </summary>
/// <example>
/// <code>Connect-Copilot</code>
/// <code>Connect-Copilot -Host "https://example.ghe.com"</code>
/// <code>Connect-Copilot -CliPath C:\path\to\copilot.exe</code>
/// </example>
[Cmdlet(VerbsCommunications.Connect, "Copilot")]
public sealed class ConnectCopilotCommand : AsyncPSCmdlet
{
    [Parameter(HelpMessage = "Path to the Copilot CLI executable. If not specified, the bundled or cached CLI is used (downloading if necessary).")]
    public string? CliPath { get; set; }

    [Parameter(HelpMessage = "GitHub host URL for GitHub Enterprise Cloud with data residency (e.g. https://example.ghe.com). Defaults to github.com.")]
    [Alias("Host")]
    public string? GitHubHost { get; set; }

    protected override async Task ProcessRecordAsync()
    {
        // Resolve CLI path (download if needed)
        string? cliPath = CliPath;
        if (cliPath is null)
        {
            cliPath = await CliPathResolver.ResolveOrDownloadAsync(
                msg => WriteVerbose(msg));
            if (cliPath is null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new FileNotFoundException("Copilot CLI not found and could not be downloaded."),
                    "CliNotFound", ErrorCategory.ObjectNotFound, null));
                return;
            }
        }

        WriteVerbose($"Using CLI: {cliPath}");

        var args = new List<string> { "login" };
        if (GitHubHost is not null)
        {
            args.Add("--host");
            args.Add(GitHubHost);
        }

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        WriteVerbose($"Running: {cliPath} {string.Join(' ', args)}");

        using var process = Process.Start(psi);
        if (process is null)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException("Failed to start Copilot CLI process."),
                "ProcessStartFailed", ErrorCategory.InvalidOperation, null));
            return;
        }

        // Stream stdout and stderr to the host in real time.
        // Do NOT use Task.Run — that escapes the AsyncPSCmdlet SynchronizationContext,
        // causing WriteObject/WriteWarning to be called from thread pool threads
        // where they silently fail. Starting the async methods directly ensures
        // continuations are posted back to the pipeline thread.
        async Task ReadStdout()
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                WriteObject(line);
        }

        async Task ReadStderr()
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                WriteWarning(line);
        }

        var stdoutTask = ReadStdout();
        var stderrTask = ReadStderr();

        await process.WaitForExitAsync();
        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
        {
            WriteWarning($"Copilot login exited with code {process.ExitCode}.");
        }
        else
        {
            WriteObject("Successfully authenticated with GitHub Copilot.");
        }
    }
}
