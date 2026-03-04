using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace McpWrapper;

/// <summary>
/// The zombie daemon process. Listens on a Unix domain socket for client
/// connections. Manages long-lived MCP server child processes and multiplexes
/// JSON-RPC messages between multiple clients and shared servers.
/// </summary>
/// <remarks>
/// <para>Protocol overview:</para>
/// <list type="number">
///   <item>Client connects to the control socket</item>
///   <item>Client sends a JSON connect request identifying the MCP server config</item>
///   <item>Daemon finds/creates the child MCP server process</item>
///   <item>Daemon responds with status</item>
///   <item>Connection becomes a bidirectional JSON-RPC proxy with ID remapping</item>
///   <item>On client disconnect, the MCP server stays alive for future clients</item>
/// </list>
///
/// <para>MCP protocol concurrency: JSON-RPC 2.0 supports concurrent requests
/// (each has a unique ID, responses may arrive out of order). However, writes to
/// the child's stdin are serialized via <see cref="ManagedChild"/> to prevent
/// interleaved bytes on the single stdio pipe. Multiple clients can send requests
/// concurrently — the daemon handles ID remapping to prevent collisions.</para>
/// </remarks>
internal sealed class McpHostDaemon
{
    private readonly ConcurrentDictionary<string, ManagedChild> _children = new();
    private int _nextClientId;
    private readonly string _socketDir;
    private readonly string _socketPath;
    private readonly string _pidPath;
    private readonly string _logPath;

    public McpHostDaemon()
    {
        _socketDir = PathHelper.GetSocketDir();
        _socketPath = PathHelper.GetControlSocketPath();
        _pidPath = PathHelper.GetPidFilePath();
        _logPath = PathHelper.GetLogFilePath();
    }

    /// <summary>
    /// Run the daemon — listen for connections until cancelled or shutdown.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;

        // Wire up graceful shutdown
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        Directory.CreateDirectory(_socketDir);

        // Clean up stale socket file from a previous crashed daemon
        if (File.Exists(_socketPath))
        {
            Log("Cleaning up stale socket file");
            try { File.Delete(_socketPath); } catch { }
        }

        // Write PID file
        await File.WriteAllTextAsync(_pidPath, Environment.ProcessId.ToString(), token);

        // Create and bind the Unix domain socket
        using var listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listenSocket.Bind(new UnixDomainSocketEndPoint(_socketPath));
        listenSocket.Listen(backlog: 16);

        Log($"Daemon started (PID {Environment.ProcessId}), listening on {_socketPath}");

        // Accept connections until shutdown
        var activeTasks = new List<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket clientSocket;
                try
                {
                    clientSocket = await listenSocket.AcceptAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var clientId = Interlocked.Increment(ref _nextClientId);
                var task = HandleClientAsync(clientSocket, clientId, token);
                activeTasks.Add(task);

                // Clean up completed tasks periodically
                activeTasks.RemoveAll(t => t.IsCompleted);
            }
        }
        finally
        {
            Log("Daemon shutting down...");

            // Wait briefly for active tasks to finish
            if (activeTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(activeTasks).WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch { }
            }

            // Kill all child MCP servers
            foreach (var (key, child) in _children)
            {
                Log($"Killing child: {key}");
                await child.DisposeAsync();
            }
            _children.Clear();

            // Clean up socket and PID files
            try { File.Delete(_socketPath); } catch { }
            try { File.Delete(_pidPath); } catch { }

            Log("Daemon stopped.");
        }
    }

    /// <summary>
    /// Handle a single client connection from accept through disconnect.
    /// </summary>
    private async Task HandleClientAsync(Socket clientSocket, int clientId, CancellationToken ct)
    {
        await using var stream = new NetworkStream(clientSocket, ownsSocket: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        try
        {
            // ── Read handshake request ──
            var requestLine = await reader.ReadLineAsync(ct);
            if (requestLine is null)
            {
                Log($"Client {clientId}: disconnected before handshake");
                return;
            }

            JsonElement request;
            try
            {
                request = JsonDocument.Parse(requestLine).RootElement;
            }
            catch (JsonException ex)
            {
                Log($"Client {clientId}: invalid handshake JSON: {ex.Message}");
                await WriteLineAsync(stream, "{\"status\":\"error\",\"error\":\"invalid request JSON\"}", ct);
                return;
            }

            // Check for shutdown action
            if (request.TryGetProperty("action", out var actionProp) &&
                actionProp.GetString() == "shutdown")
            {
                Log($"Client {clientId}: shutdown requested");
                await WriteLineAsync(stream, "{\"status\":\"ok\",\"message\":\"shutting down\"}", ct);
                Environment.Exit(0);
                return;
            }

            // Parse connect request
            var cmd = request.GetProperty("command").GetString()!;
            var cmdArgs = request.TryGetProperty("args", out var argsEl)
                ? argsEl.EnumerateArray().Select(a => a.GetString()!).ToList()
                : new List<string>();
            var cmdEnv = request.TryGetProperty("env", out var envEl)
                ? envEl.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)
                : new Dictionary<string, string>();
            var cmdCwd = request.TryGetProperty("cwd", out var cwdEl)
                ? cwdEl.GetString() ?? ""
                : "";

            // ── Find or create child ──
            var childKey = ManagedChild.ComputeKey(cmd, cmdArgs, cmdEnv, cmdCwd);
            var child = _children.GetOrAdd(childKey, _ =>
            {
                Log($"Client {clientId}: starting new child [{childKey}]: {cmd} {string.Join(' ', cmdArgs)}");
                return ManagedChild.Start(cmd, cmdArgs, cmdEnv, cmdCwd, _socketDir, Log);
            });

            // Check if child is still alive; if not, restart
            if (child.HasExited)
            {
                Log($"Client {clientId}: child [{childKey}] has exited, restarting");
                await child.DisposeAsync();
                child = ManagedChild.Start(cmd, cmdArgs, cmdEnv, cmdCwd, _socketDir, Log);
                _children[childKey] = child;
            }

            bool reused = child.IsInitialized;

            // Create client connection and register with child
            var clientConn = new ClientConnection(clientId, stream);
            child.AddClient(clientConn);

            // ── Send handshake response ──
            var handshakeResponse = reused
                ? "{\"status\":\"ok\",\"reused\":true}"
                : "{\"status\":\"ok\",\"reused\":false}";
            await WriteLineAsync(stream, handshakeResponse, ct);

            Log($"Client {clientId}: connected to child [{childKey}] (reused={reused})");

            // ── Proxy loop: read lines from client → forward to child ──
            clientConn.StartWriterLoop(ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(ct);
                    }
                    catch (IOException)
                    {
                        break;
                    }

                    if (line is null) break; // Client disconnected

                    await child.HandleClientMessageAsync(clientId, line, ct);
                }
            }
            finally
            {
                Log($"Client {clientId}: disconnected from child [{childKey}]");
                child.RemoveClient(clientId);
                clientConn.Complete();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Log($"Client {clientId}: error: {ex.Message}");
        }
    }

    /// <summary>
    /// Write a line to a NetworkStream (raw bytes to avoid StreamWriter buffering).
    /// </summary>
    private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private void Log(string message)
    {
        var entry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        try
        {
            File.AppendAllText(_logPath, entry + "\n");
        }
        catch
        {
            // Last resort — logging should never crash the daemon
        }
    }
}

/// <summary>
/// Represents a connected client on the daemon side.
/// Each client has an outbound message channel and a writer task.
/// </summary>
internal sealed class ClientConnection
{
    public int Id { get; }
    public NetworkStream Stream { get; }
    public Channel<string> Outbound { get; } = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private Task? _writerTask;

    public ClientConnection(int id, NetworkStream stream)
    {
        Id = id;
        Stream = stream;
    }

    /// <summary>
    /// Start the background task that drains the outbound channel and writes to the socket.
    /// </summary>
    public void StartWriterLoop(CancellationToken ct)
    {
        _writerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in Outbound.Reader.ReadAllAsync(ct))
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    await Stream.WriteAsync(bytes, ct);
                    await Stream.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }, ct);
    }

    /// <summary>
    /// Signal that no more outbound messages will be sent.
    /// </summary>
    public void Complete()
    {
        Outbound.Writer.TryComplete();
    }

    /// <summary>
    /// Enqueue a message to send to this client.
    /// </summary>
    public bool TrySend(string line) => Outbound.Writer.TryWrite(line);
}

/// <summary>
/// Helper for resolving socket/PID file paths.
/// Uses platform-appropriate directories for the daemon's runtime files.
/// </summary>
internal static class PathHelper
{
    /// <summary>
    /// Directory for daemon runtime files (socket, PID, logs).
    /// </summary>
    public static string GetSocketDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "mcp-host");
        }
        else
        {
            return Path.Combine(Path.GetTempPath(), $"mcp-host-{Environment.UserName}");
        }
    }

    /// <summary>
    /// Path to the Unix domain socket for client connections.
    /// </summary>
    public static string GetControlSocketPath() =>
        Path.Combine(GetSocketDir(), "ctrl.sock");

    /// <summary>
    /// Path to the daemon PID file.
    /// </summary>
    public static string GetPidFilePath() =>
        Path.Combine(GetSocketDir(), "daemon.pid");

    /// <summary>
    /// Path to the daemon log file.
    /// </summary>
    public static string GetLogFilePath() =>
        Path.Combine(GetSocketDir(), "daemon.log");
}
