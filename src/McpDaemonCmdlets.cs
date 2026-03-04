using System.Diagnostics;
using System.Management.Automation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace CopilotShell;

/// <summary>
/// Stops the MCP zombie daemon and all its managed MCP server processes.
/// Use this when authentication tokens become stale and you need to force
/// MCP servers to restart fresh (re-authenticate) on the next session.
/// </summary>
/// <example>
/// <code>Reset-CopilotMcpDaemon</code>
/// <para>Stops the daemon and all managed MCP servers. They restart automatically on next use.</para>
/// </example>
/// <example>
/// <code>Reset-CopilotMcpDaemon -Force</code>
/// <para>Force-kills the daemon process if the graceful shutdown signal fails.</para>
/// </example>
[Cmdlet(VerbsCommon.Reset, "CopilotMcpDaemon", SupportsShouldProcess = true)]
public sealed class ResetCopilotMcpDaemonCommand : PSCmdlet
{
    [Parameter(HelpMessage = "Force-kill the daemon process if graceful shutdown fails.")]
    public SwitchParameter Force { get; set; }

    private static string GetSocketDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "mcp-host");
        }
        return Path.Combine(Path.GetTempPath(), $"mcp-host-{Environment.UserName}");
    }

    protected override void ProcessRecord()
    {
        var socketDir = GetSocketDir();
        var socketPath = Path.Combine(socketDir, "ctrl.sock");
        var pidPath = Path.Combine(socketDir, "daemon.pid");
        var logPath = Path.Combine(socketDir, "daemon.log");

        // Check if daemon appears to be running
        bool socketExists = File.Exists(socketPath);
        bool pidExists = File.Exists(pidPath);

        if (!socketExists && !pidExists)
        {
            WriteVerbose("No daemon runtime files found — daemon is not running.");
            WriteObject("MCP daemon is not running.");
            return;
        }

        if (!ShouldProcess("MCP zombie daemon", "Stop daemon and all managed MCP servers"))
            return;

        // Try graceful shutdown via socket
        bool gracefulSuccess = false;
        if (socketExists)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(socketPath));
                var msg = Encoding.UTF8.GetBytes("{\"action\":\"shutdown\"}\n");
                socket.Send(msg);
                socket.Shutdown(SocketShutdown.Both);
                gracefulSuccess = true;
                WriteVerbose("Shutdown signal sent to daemon.");

                // Give it a moment to clean up
                System.Threading.Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                WriteVerbose($"Graceful shutdown failed: {ex.Message}");
            }
        }

        // If graceful failed (or Force), try killing by PID
        if ((!gracefulSuccess || Force.IsPresent) && pidExists)
        {
            try
            {
                var pidText = File.ReadAllText(pidPath).Trim();
                if (int.TryParse(pidText, out var pid))
                {
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        proc.Kill(entireProcessTree: true);
                        WriteVerbose($"Killed daemon process (PID {pid}).");
                        gracefulSuccess = true;
                    }
                    catch (ArgumentException)
                    {
                        WriteVerbose($"Daemon process (PID {pid}) is not running.");
                        gracefulSuccess = true; // Already dead
                    }
                    catch (Exception ex)
                    {
                        WriteWarning($"Failed to kill daemon (PID {pid}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                WriteVerbose($"Could not read PID file: {ex.Message}");
            }
        }

        // Clean up stale files
        foreach (var file in new[] { socketPath, pidPath })
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    WriteVerbose($"Removed: {file}");
                }
            }
            catch (Exception ex)
            {
                WriteVerbose($"Could not remove {file}: {ex.Message}");
            }
        }

        if (gracefulSuccess)
        {
            WriteObject("MCP daemon stopped. Servers will restart automatically on next session.");
        }
        else
        {
            WriteWarning("Could not stop daemon. Use -Force to kill the process, or manually clean up: " + socketDir);
        }

        // Show log tail if verbose
        if (MyInvocation.BoundParameters.ContainsKey("Verbose") && File.Exists(logPath))
        {
            WriteVerbose("--- Last 10 log lines ---");
            try
            {
                var lines = File.ReadAllLines(logPath);
                var tail = lines.Skip(Math.Max(0, lines.Length - 10));
                foreach (var line in tail)
                    WriteVerbose(line);
            }
            catch { }
        }
    }
}
