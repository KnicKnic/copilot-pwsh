using System.Management.Automation;
using GitHub.Copilot.SDK;

namespace CopilotShell;

/// <summary>
/// Creates a new CopilotClient. The client manages the connection to the
/// Copilot CLI server process.
/// </summary>
/// <example>
/// <code>$client = New-CopilotClient</code>
/// <code>$client = New-CopilotClient -Model gpt-5 -LogLevel debug</code>
/// <code>$client = New-CopilotClient -GitHubToken $env:GITHUB_TOKEN</code>
/// </example>
[Cmdlet(VerbsCommon.New, "CopilotClient")]
[OutputType(typeof(CopilotClient))]
public sealed class NewCopilotClientCommand : AsyncPSCmdlet
{
    [Parameter(HelpMessage = "Path to the Copilot CLI executable.")]
    public string? CliPath { get; set; }

    [Parameter(HelpMessage = "Extra CLI arguments prepended before SDK-managed flags.")]
    public string[]? CliArgs { get; set; }

    [Parameter(HelpMessage = "URL of an existing CLI server to connect to (e.g. localhost:8080). Skips spawning a process.")]
    public string? CliUrl { get; set; }

    [Parameter(HelpMessage = "TCP port for the server (0 = random).")]
    public int Port { get; set; } = 0;

    [Parameter(HelpMessage = "Use stdio transport instead of TCP.")]
    public SwitchParameter UseStdio { get; set; }

    [Parameter(HelpMessage = "Log level: none, error, warning, info, debug.")]
    public string? LogLevel { get; set; }

    [Parameter(HelpMessage = "Auto-start the server on creation.")]
    public bool AutoStart { get; set; } = true;


    [Parameter(HelpMessage = "Working directory for the CLI process.")]
    public string? Cwd { get; set; }

    [Parameter(HelpMessage = "GitHub token for authentication.")]
    public string? GitHubToken { get; set; }

    [Parameter(HelpMessage = "Whether to use the logged-in GitHub user.")]
    public SwitchParameter UseLoggedInUser { get; set; }

    protected override async Task ProcessRecordAsync()
    {
        var opts = new CopilotClientOptions();

        // Auto-detect bundled CLI or download from npm if not explicitly provided
        if (CliPath is null)
        {
            var resolved = await CliPathResolver.ResolveOrDownloadAsync(
                msg => WriteVerbose(msg));
            if (resolved is not null) opts.CliPath = resolved;
        }

        if (CliPath is not null) opts.CliPath = CliPath;
        if (CliArgs is not null) opts.CliArgs = CliArgs;
        if (CliUrl is not null) opts.CliUrl = CliUrl;
        if (Port != 0) opts.Port = Port;
        if (UseStdio.IsPresent) opts.UseStdio = true;
        if (LogLevel is not null) opts.LogLevel = LogLevel;
        opts.AutoStart = AutoStart;
        if (Cwd is not null) opts.Cwd = Cwd;
        if (GitHubToken is not null) opts.GitHubToken = GitHubToken;
        if (UseLoggedInUser.IsPresent) opts.UseLoggedInUser = true;

        var client = new CopilotClient(opts);

        if (AutoStart)
        {
            // Run StartAsync without our PipelineSyncContext so the SDK's internal
            // reader threads don't capture it (it will be disposed after this cmdlet returns).
            // Use Task.Run to ensure StartAsync runs on the thread pool with no SyncContext.
            await Task.Run(() => client.StartAsync());
        }

        WriteObject(client);
    }
}

/// <summary>
/// Start a CopilotClient that was created with -AutoStart $false.
/// </summary>
[Cmdlet(VerbsLifecycle.Start, "CopilotClient")]
public sealed class StartCopilotClientCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
        HelpMessage = "The CopilotClient to start.")]
    public CopilotClient Client { get; set; } = null!;

    protected override async Task ProcessRecordAsync()
    {
        await Client.StartAsync();
    }
}

/// <summary>
/// Gracefully stop a CopilotClient.
/// </summary>
[Cmdlet(VerbsLifecycle.Stop, "CopilotClient")]
public sealed class StopCopilotClientCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
        HelpMessage = "The CopilotClient to stop.")]
    public CopilotClient Client { get; set; } = null!;

    [Parameter(HelpMessage = "Force stop without graceful cleanup.")]
    public SwitchParameter Force { get; set; }

    protected override async Task ProcessRecordAsync()
    {
        if (Force.IsPresent)
            await Client.ForceStopAsync();
        else
            await Client.StopAsync();
    }
}

/// <summary>
/// Dispose and remove a CopilotClient.
/// </summary>
[Cmdlet(VerbsCommon.Remove, "CopilotClient")]
public sealed class RemoveCopilotClientCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
        HelpMessage = "The CopilotClient to dispose.")]
    public CopilotClient Client { get; set; } = null!;

    protected override async Task ProcessRecordAsync()
    {
        await Client.DisposeAsync();
    }
}

/// <summary>
/// Ping the Copilot server to test connectivity.
/// </summary>
[Cmdlet(VerbsDiagnostic.Test, "CopilotClient")]
[OutputType(typeof(PingResponse))]
public sealed class TestCopilotClientCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
        HelpMessage = "The CopilotClient to ping.")]
    public CopilotClient Client { get; set; } = null!;

    [Parameter(HelpMessage = "Optional message to include in the ping.")]
    public string? Message { get; set; }

    protected override async Task ProcessRecordAsync()
    {
        var response = await Client.PingAsync(Message);
        WriteObject(response);
    }
}
