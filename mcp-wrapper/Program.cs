// MCP Wrapper — transparent MCP server proxy with zombie daemon support.
//
// Modes:
//   Direct:  mcp-wrapper [--env KEY=VALUE]... [--cwd DIR] [--no-zombie] -- <command> [args...]
//     Transparent stdin/stdout/stderr proxy. Sets env vars and cwd, then launches
//     the MCP server process directly. Used for servers that don't match zombie
//     eligibility patterns, or when --no-zombie is specified.
//
//   Zombie:  mcp-wrapper [--env KEY=VALUE]... [--cwd DIR] -- <command> [args...]
//     When the command+args match zombie eligibility regex patterns, connects to
//     a background daemon (starting it if needed) that keeps the MCP server alive
//     across sessions. Proxies stdio through a Unix domain socket.
//
//   Daemon:  mcp-wrapper --daemon
//     Internal: runs the zombie daemon that manages persistent MCP servers.
//
//   Stop:    mcp-wrapper --stop
//     Sends shutdown signal to the running daemon.
//
// Zombie eligibility (regex patterns matched against command and args):
//   - .*@azure-devops.*
//   - .*microsoft-fabric-rti-mcp.*
//   - .*ev2.*
//   - .*grafana.*
//
// Benefits of zombie mode:
//   - MCP servers persist across Copilot sessions (no startup delay)
//   - Auth tokens survive session boundaries (no re-auth)
//   - Multiple clients can share one MCP server via JSON-RPC multiplexing
//   - JSON-RPC 2.0 supports concurrent requests; stdin writes are serialized
//     to prevent byte interleaving on the single stdio pipe

using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// ── Parse arguments ──
bool isDaemon = false;
bool isStop = false;
bool noZombie = false;
var envVars = new Dictionary<string, string>();
string? cwd = null;
string? command = null;
var childArgs = new List<string>();

int i = 0;
bool pastSeparator = false;

while (i < args.Length)
{
    if (!pastSeparator)
    {
        if (args[i] == "--daemon")
        {
            isDaemon = true;
            i++;
            continue;
        }
        else if (args[i] == "--stop")
        {
            isStop = true;
            i++;
            continue;
        }
        else if (args[i] == "--no-zombie")
        {
            noZombie = true;
            i++;
            continue;
        }
        else if (args[i] == "--")
        {
            pastSeparator = true;
            i++;
            continue;
        }
        else if (args[i] == "--env" && i + 1 < args.Length)
        {
            var eqIdx = args[i + 1].IndexOf('=');
            if (eqIdx > 0)
            {
                envVars[args[i + 1][..eqIdx]] = args[i + 1][(eqIdx + 1)..];
            }
            i += 2;
            continue;
        }
        else if (args[i] == "--cwd" && i + 1 < args.Length)
        {
            cwd = args[i + 1];
            i += 2;
            continue;
        }
        else
        {
            // No separator found; treat remaining args as command + child args
            pastSeparator = true;
            // fall through to command parsing below
        }
    }

    // Past separator (or no separator used): first arg is command, rest are child args
    if (command is null)
    {
        command = args[i];
    }
    else
    {
        childArgs.Add(args[i]);
    }
    i++;
}

// ── Daemon mode (internal) ──
if (isDaemon)
{
    var daemon = new McpWrapper.McpHostDaemon();
    await daemon.RunAsync(CancellationToken.None);
    return 0;
}

// ── Stop mode ──
if (isStop)
{
    var socketPath = McpWrapper.PathHelper.GetControlSocketPath();
    if (!File.Exists(socketPath))
    {
        await Console.Error.WriteLineAsync("mcp-wrapper: no daemon running (socket not found)");
        return 1;
    }

    try
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        var msg = Encoding.UTF8.GetBytes("{\"action\":\"shutdown\"}\n");
        await socket.SendAsync(msg, SocketFlags.None);
        socket.Shutdown(SocketShutdown.Both);
        await Console.Error.WriteLineAsync("mcp-wrapper: daemon shutdown signal sent");
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"mcp-wrapper: failed to stop daemon: {ex.Message}");
        try { File.Delete(socketPath); } catch { }
        return 1;
    }
    return 0;
}

// ── Validate command ──
if (string.IsNullOrEmpty(command))
{
    await Console.Error.WriteLineAsync(
        "mcp-wrapper: MCP server proxy with zombie daemon support\n" +
        "\n" +
        "Usage:\n" +
        "  mcp-wrapper [--env KEY=VALUE]... [--cwd DIR] [--no-zombie] -- <command> [args...]\n" +
        "  mcp-wrapper --daemon\n" +
        "  mcp-wrapper --stop\n" +
        "\n" +
        "Options:\n" +
        "  --env KEY=VALUE   Set environment variable for the MCP server (repeatable)\n" +
        "  --cwd DIR         Set working directory for the MCP server\n" +
        "  --no-zombie       Force direct proxy mode (don't use zombie daemon)\n" +
        "  --                Separator between wrapper options and MCP server command\n" +
        "\n" +
        "When command/args match zombie eligibility patterns, the server is managed\n" +
        "by a background daemon for persistent connections across sessions.");
    return 1;
}

// ── Decide: zombie or direct proxy mode ──
bool useZombie = !noZombie && McpWrapper.ZombieEligibility.IsEligible(command, childArgs);

if (useZombie)
{
    return await RunZombieModeAsync(command, childArgs, envVars, cwd);
}
else
{
    return await RunDirectModeAsync(command, childArgs, envVars, cwd);
}

// ═══════════════════════════════════════════════════════════════════════
// Direct proxy mode — transparent stdin/stdout/stderr pass-through
// ═══════════════════════════════════════════════════════════════════════

static async Task<int> RunDirectModeAsync(
    string command,
    List<string> childArgs,
    Dictionary<string, string> envVars,
    string? cwd)
{
    var psi = new ProcessStartInfo
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    // On Windows, commands without extensions (npx, uvx, etc.) are .cmd files
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        && !Path.HasExtension(command)
        && !Path.IsPathRooted(command))
    {
        psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(command);
    }
    else
    {
        psi.FileName = command;
    }

    foreach (var arg in childArgs)
        psi.ArgumentList.Add(arg);

    foreach (var (key, value) in envVars)
        psi.Environment[key] = value;

    if (!string.IsNullOrEmpty(cwd))
        psi.WorkingDirectory = cwd;

    Process process;
    try
    {
        process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {command}");
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"mcp-wrapper: failed to start '{command}': {ex.Message}");
        return 1;
    }

    using (process)
    {
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        };

        var stdinTask = Task.Run(async () =>
        {
            try
            {
                using var input = Console.OpenStandardInput();
                await input.CopyToAsync(process.StandardInput.BaseStream, cts.Token);
                process.StandardInput.Close();
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });

        var stdoutTask = Task.Run(async () =>
        {
            try
            {
                using var output = Console.OpenStandardOutput();
                await process.StandardOutput.BaseStream.CopyToAsync(output, cts.Token);
                await output.FlushAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });

        var stderrTask = Task.Run(async () =>
        {
            try
            {
                using var error = Console.OpenStandardError();
                await process.StandardError.BaseStream.CopyToAsync(error, cts.Token);
                await error.FlushAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });

        await process.WaitForExitAsync();

        cts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }

        return process.ExitCode;
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Zombie mode — connect to daemon, proxy stdio through socket
// ═══════════════════════════════════════════════════════════════════════

static async Task<int> RunZombieModeAsync(
    string command,
    List<string> childArgs,
    Dictionary<string, string> envVars,
    string? cwd)
{
    var controlSocketPath = McpWrapper.PathHelper.GetControlSocketPath();
    Socket? clientSocket = await TryConnectAsync(controlSocketPath);

    if (clientSocket is null)
    {
        SpawnDaemon();

        for (int retry = 0; retry < 100; retry++) // 10 seconds
        {
            await Task.Delay(100);
            clientSocket = await TryConnectAsync(controlSocketPath);
            if (clientSocket is not null) break;
        }

        if (clientSocket is null)
        {
            await Console.Error.WriteLineAsync("mcp-wrapper: daemon failed to start within 10 seconds, falling back to direct mode");
            return await RunDirectModeAsync(command, childArgs, envVars, cwd);
        }
    }

    // ── Handshake ──
    // Use the current process environment (which the SDK set from original.Env)
    // so the daemon spawns the child with the correct env vars.
    var processEnv = Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty);
    // Overlay any explicit --env args on top
    foreach (var (k, v) in envVars) processEnv[k] = v;

    var connectRequest = JsonSerializer.Serialize(new
    {
        action = "connect",
        command,
        args = childArgs,
        env = processEnv,
        cwd = cwd ?? ""
    });

    using var networkStream = new NetworkStream(clientSocket, ownsSocket: true);

    var requestBytes = Encoding.UTF8.GetBytes(connectRequest + "\n");
    await networkStream.WriteAsync(requestBytes);
    await networkStream.FlushAsync();

    var responseLine = await ReadLineRawAsync(networkStream, CancellationToken.None);
    if (responseLine is null)
    {
        await Console.Error.WriteLineAsync("mcp-wrapper: daemon closed connection during handshake, falling back to direct mode");
        return await RunDirectModeAsync(command, childArgs, envVars, cwd);
    }

    try
    {
        var response = JsonDocument.Parse(responseLine);
        var status = response.RootElement.GetProperty("status").GetString();
        if (status != "ok")
        {
            var error = response.RootElement.TryGetProperty("error", out var e)
                ? e.GetString() : "unknown error";
            await Console.Error.WriteLineAsync($"mcp-wrapper: daemon error: {error}, falling back to direct mode");
            return await RunDirectModeAsync(command, childArgs, envVars, cwd);
        }

        bool reused = response.RootElement.TryGetProperty("reused", out var r) && r.GetBoolean();
        if (reused)
            await Console.Error.WriteLineAsync("mcp-wrapper: reusing persistent MCP server (instant connect)");
        else
            await Console.Error.WriteLineAsync("mcp-wrapper: started MCP server via daemon (will persist)");
    }
    catch (JsonException ex)
    {
        await Console.Error.WriteLineAsync($"mcp-wrapper: invalid handshake: {ex.Message}, falling back to direct mode");
        return await RunDirectModeAsync(command, childArgs, envVars, cwd);
    }

    // ── Proxy stdin/stdout through socket ──
    using var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
        try { clientSocket.Shutdown(SocketShutdown.Both); } catch { }
    };

    var stdinToSocket = Task.Run(async () =>
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[8192];
            while (!cts.Token.IsCancellationRequested)
            {
                var read = await stdin.ReadAsync(buffer, cts.Token);
                if (read == 0)
                {
                    try { clientSocket.Shutdown(SocketShutdown.Send); } catch { }
                    break;
                }
                await networkStream.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                await networkStream.FlushAsync(cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    });

    var socketToStdout = Task.Run(async () =>
    {
        try
        {
            using var stdout = Console.OpenStandardOutput();
            var buffer = new byte[8192];
            while (!cts.Token.IsCancellationRequested)
            {
                var read = await networkStream.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                await stdout.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                await stdout.FlushAsync(cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    });

    await Task.WhenAny(stdinToSocket, socketToStdout);
    cts.CancelAfter(TimeSpan.FromSeconds(2));

    try
    {
        await Task.WhenAll(stdinToSocket, socketToStdout)
            .WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
    }
    catch (TimeoutException) { }
    catch (OperationCanceledException) { }

    return 0;
}

// ═══════════════════════════════════════════════════════════════════════
// Helper methods
// ═══════════════════════════════════════════════════════════════════════

static async Task<Socket?> TryConnectAsync(string socketPath)
{
    if (!File.Exists(socketPath)) return null;

    try
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        return socket;
    }
    catch
    {
        return null;
    }
}

static void SpawnDaemon()
{
    var selfPath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine own executable path");

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var psi = new ProcessStartInfo
        {
            FileName = selfPath,
            ArgumentList = { "--daemon" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        Process.Start(psi);
    }
    else
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { "-c", $"nohup setsid \"{selfPath}\" --daemon >/dev/null 2>&1 &" },
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(5000);
    }
}

static async Task<string?> ReadLineRawAsync(NetworkStream stream, CancellationToken ct)
{
    var buffer = new List<byte>(512);
    var oneByte = new byte[1];

    while (!ct.IsCancellationRequested)
    {
        int n;
        try { n = await stream.ReadAsync(oneByte, ct); }
        catch (OperationCanceledException) { return null; }

        if (n == 0)
            return buffer.Count > 0 ? Encoding.UTF8.GetString(buffer.ToArray()) : null;

        if (oneByte[0] == (byte)'\n')
        {
            if (buffer.Count > 0 && buffer[^1] == (byte)'\r')
                buffer.RemoveAt(buffer.Count - 1);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        buffer.Add(oneByte[0]);
    }

    return null;
}
