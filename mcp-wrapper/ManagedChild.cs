using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace McpWrapper;

/// <summary>
/// Manages a single long-lived MCP server child process. Handles the MCP
/// initialize handshake, multiplexes JSON-RPC messages between multiple
/// connected clients, and remaps message IDs to avoid collisions.
/// </summary>
/// <remarks>
/// <para>Each <see cref="ManagedChild"/> is identified by a key derived from
/// the unique combination of (command + args + env + cwd). This ensures that
/// two requests for the same MCP server reuse the same process.</para>
///
/// <para>JSON-RPC ID remapping:</para>
/// <list type="bullet">
///   <item>Client sends request with ID X → mapped to unique global ID G</item>
///   <item>Forward request to child with ID G</item>
///   <item>Child responds with ID G → mapped back to ID X for that client</item>
///   <item>This allows multiple clients to independently send IDs 1, 2, 3, etc.</item>
/// </list>
///
/// <para>Concurrency: MCP (JSON-RPC 2.0) supports concurrent in-flight requests.
/// Writes to the child's stdin are serialized via <see cref="_stdinLock"/> to
/// prevent interleaved bytes. Multiple clients can have concurrent requests — the
/// daemon tracks them via the global ID map and routes responses correctly.</para>
/// </remarks>
internal sealed class ManagedChild : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _childStdin;
    private readonly ConcurrentDictionary<int, ClientConnection> _clients = new();
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly Action<string> _log;

    // ID remapping: global ID → (clientId, originalId)
    private readonly ConcurrentDictionary<long, (int clientId, JsonElement originalId)> _pendingRequests = new();
    private long _nextGlobalId;

    // Track initialization state
    private bool _isInitialized;
    private JsonElement? _cachedInitResponse;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // Track if any client has initialized
    private int _initializeCount;

    /// <summary>Whether the MCP initialize handshake has completed for this child.</summary>
    public bool IsInitialized => _isInitialized;

    /// <summary>Whether the child process has exited.</summary>
    public bool HasExited => _process.HasExited;

    private ManagedChild(Process process, StreamWriter childStdin, Action<string> log)
    {
        _process = process;
        _childStdin = childStdin;
        _log = log;
    }

    /// <summary>
    /// Compute a deterministic key for the child process identity.
    /// Same key = same MCP server = reuse the process.
    /// </summary>
    public static string ComputeKey(
        string command,
        IList<string> args,
        IDictionary<string, string> env,
        string cwd)
    {
        var sb = new StringBuilder();
        sb.Append(command);
        foreach (var arg in args)
            sb.Append('\0').Append(arg);
        sb.Append('\x01'); // separator
        foreach (var (k, v) in env.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\x01');
        sb.Append(cwd);

        // SHA256 hash to keep key short
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16]; // 8 bytes = 16 hex chars
    }

    /// <summary>
    /// Start a new MCP server child process.
    /// </summary>
    public static ManagedChild Start(
        string command,
        IList<string> args,
        IDictionary<string, string> env,
        string cwd,
        string socketDir,
        Action<string> log)
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
        bool needsCmdWrapper = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && !Path.HasExtension(command)
            && !Path.IsPathRooted(command);

        if (needsCmdWrapper)
        {
            psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = command;
        }

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        foreach (var (key, value) in env)
            psi.Environment[key] = value;

        if (!string.IsNullOrEmpty(cwd))
            psi.WorkingDirectory = cwd;

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start MCP server: {command}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start MCP server '{command}': {ex.Message}", ex);
        }

        var child = new ManagedChild(process, process.StandardInput, log);

        // Start background stdout reader (routes responses back to clients)
        _ = Task.Run(() => child.StdoutReaderLoopAsync());

        // Start background stderr reader (logs everything)
        _ = Task.Run(() => child.StderrReaderLoopAsync());

        return child;
    }

    /// <summary>Register a client connection.</summary>
    public void AddClient(ClientConnection client) => _clients[client.Id] = client;

    /// <summary>Unregister a client connection.</summary>
    public void RemoveClient(int clientId)
    {
        _clients.TryRemove(clientId, out _);

        // Clean up pending requests for this client
        var toRemove = _pendingRequests
            .Where(kvp => kvp.Value.clientId == clientId)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var id in toRemove)
            _pendingRequests.TryRemove(id, out _);
    }

    /// <summary>
    /// Handle a JSON-RPC message from a client. Remaps IDs and forwards to child.
    /// For the first "initialize" request, performs the handshake and caches the
    /// response. Subsequent clients get the cached response immediately.
    /// </summary>
    /// <remarks>
    /// Writes to the child's stdin are serialized via a semaphore — MCP (JSON-RPC 2.0)
    /// supports concurrent requests but the stdio transport is a single byte stream
    /// where interleaved writes would corrupt messages.
    /// </remarks>
    public async Task HandleClientMessageAsync(int clientId, string line, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // Not valid JSON — forward as-is (shouldn't happen but be safe)
            await SendToChildAsync(line, ct);
            return;
        }

        var root = doc.RootElement;

        // Check if this is a request (has "id") or notification (no "id")
        bool hasId = root.TryGetProperty("id", out var idProp);
        string? method = root.TryGetProperty("method", out var methodProp)
            ? methodProp.GetString()
            : null;

        // ── Handle initialize request specially ──
        if (method == "initialize")
        {
            var initCount = Interlocked.Increment(ref _initializeCount);

            if (_isInitialized && _cachedInitResponse.HasValue)
            {
                // Already initialized — send cached response directly to client
                _log($"Child: returning cached initialize response to client {clientId}");

                var cachedResult = _cachedInitResponse.Value;
                var response = new Dictionary<string, object>();
                response["jsonrpc"] = "2.0";

                if (hasId)
                    response["id"] = DeserializeId(idProp);

                if (cachedResult.TryGetProperty("result", out var result))
                    response["result"] = JsonSerializer.Deserialize<object>(result.GetRawText())!;

                var responseJson = JsonSerializer.Serialize(response);

                if (_clients.TryGetValue(clientId, out var client))
                    client.TrySend(responseJson);
                return;
            }

            // First initialize — let it through and cache the response
            await _initLock.WaitAsync(ct);
            try
            {
                // Double-check after acquiring lock
                if (_isInitialized && _cachedInitResponse.HasValue)
                {
                    var cachedResult = _cachedInitResponse.Value;
                    var response = new Dictionary<string, object>();
                    response["jsonrpc"] = "2.0";
                    if (hasId) response["id"] = DeserializeId(idProp);
                    if (cachedResult.TryGetProperty("result", out var result))
                        response["result"] = JsonSerializer.Deserialize<object>(result.GetRawText())!;

                    var responseJson = JsonSerializer.Serialize(response);
                    if (_clients.TryGetValue(clientId, out var client))
                        client.TrySend(responseJson);
                    return;
                }

                // Fall through to forward the message (will be intercepted in stdout reader)
            }
            finally
            {
                _initLock.Release();
            }
        }

        // ── Handle "notifications/initialized" — forward but don't remap ──
        if (method == "notifications/initialized")
        {
            // Only forward if this is the first client (child hasn't been initialized yet)
            if (_isInitialized)
                return; // Skip — child already knows it's initialized

            await SendToChildAsync(line, ct);
            return;
        }

        // ── Remap ID for requests ──
        if (hasId)
        {
            var globalId = Interlocked.Increment(ref _nextGlobalId);
            _pendingRequests[globalId] = (clientId, idProp.Clone());

            // Rewrite the message with the new global ID
            var rewritten = RewriteId(root, globalId);
            await SendToChildAsync(rewritten, ct);
        }
        else
        {
            // Notifications — forward as-is
            await SendToChildAsync(line, ct);
        }
    }

    /// <summary>
    /// Background loop reading stdout from the child MCP server.
    /// Routes responses back to the correct client by reversing ID remapping.
    /// </summary>
    private async Task StdoutReaderLoopAsync()
    {
        try
        {
            while (!_process.HasExited)
            {
                var line = await _process.StandardOutput.ReadLineAsync();
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // Non-JSON output — broadcast to all clients
                    BroadcastToClients(line);
                    continue;
                }

                var root = doc.RootElement;

                // Check for initialize response (cache it)
                if (!_isInitialized && root.TryGetProperty("id", out var respId))
                {
                    long id = respId.ValueKind == JsonValueKind.Number
                        ? respId.GetInt64()
                        : long.Parse(respId.GetString()!);

                    if (_pendingRequests.TryGetValue(id, out var pending))
                    {
                        if (root.TryGetProperty("result", out var result) &&
                            result.TryGetProperty("capabilities", out _))
                        {
                            _cachedInitResponse = root.Clone();
                            _isInitialized = true;
                            _log("Child: initialize handshake complete, response cached");
                        }
                    }
                }

                // ── Route response to the correct client ──
                if (root.TryGetProperty("id", out var responseId))
                {
                    long globalId = responseId.ValueKind == JsonValueKind.Number
                        ? responseId.GetInt64()
                        : long.Parse(responseId.GetString()!);

                    if (_pendingRequests.TryRemove(globalId, out var mapping))
                    {
                        var rewritten = RewriteIdBack(root, mapping.originalId);
                        if (_clients.TryGetValue(mapping.clientId, out var client))
                        {
                            client.TrySend(rewritten);
                        }
                    }
                    else
                    {
                        // Unknown ID — broadcast (shouldn't happen normally)
                        BroadcastToClients(line);
                    }
                }
                else
                {
                    // Notifications from server — broadcast to all connected clients
                    BroadcastToClients(line);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"Child stdout reader error: {ex.Message}");
        }
    }

    /// <summary>
    /// Background loop reading stderr from the child MCP server (for logging).
    /// </summary>
    private async Task StderrReaderLoopAsync()
    {
        try
        {
            while (!_process.HasExited)
            {
                var line = await _process.StandardError.ReadLineAsync();
                if (line is null) break;
                _log($"Child stderr: {line}");
            }
        }
        catch (Exception ex)
        {
            _log($"Child stderr reader error: {ex.Message}");
        }
    }

    /// <summary>
    /// Send a line to the child's stdin.
    /// Serialized via semaphore — JSON-RPC 2.0 supports concurrent requests but
    /// the stdio byte stream must not have interleaved writes.
    /// </summary>
    private async Task SendToChildAsync(string line, CancellationToken ct)
    {
        await _stdinLock.WaitAsync(ct);
        try
        {
            await _childStdin.WriteLineAsync(line.AsMemory(), ct);
            await _childStdin.FlushAsync(ct);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    /// <summary>Send a line to all connected clients.</summary>
    private void BroadcastToClients(string line)
    {
        foreach (var (_, client) in _clients)
        {
            client.TrySend(line);
        }
    }

    /// <summary>Rewrite a JSON-RPC message's "id" field to a new global ID.</summary>
    private static string RewriteId(JsonElement root, long newId)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "id")
            {
                writer.WriteNumber("id", newId);
            }
            else
            {
                prop.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Rewrite a JSON-RPC response's "id" back to the client's original ID.</summary>
    private static string RewriteIdBack(JsonElement root, JsonElement originalId)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "id")
            {
                writer.WritePropertyName("id");
                originalId.WriteTo(writer);
            }
            else
            {
                prop.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Deserialize a JsonElement ID into a boxed object.</summary>
    private static object DeserializeId(JsonElement id) =>
        id.ValueKind switch
        {
            JsonValueKind.Number => id.GetInt64(),
            JsonValueKind.String => id.GetString()!,
            _ => id.GetRawText()
        };

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var (_, client) in _clients)
            client.Complete();
        _clients.Clear();

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _childStdin.Close();
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch
                {
                    try { _process.Kill(entireProcessTree: true); } catch { }
                }
            }
            _process.Dispose();
        }
        catch { }

        _stdinLock.Dispose();
        _initLock.Dispose();
    }
}
