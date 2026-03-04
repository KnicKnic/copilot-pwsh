using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CopilotShell;

/// <summary>
/// Discovers available tools from MCP servers by performing the MCP protocol handshake
/// and calling <c>tools/list</c>. This enables dynamic tool discovery instead of relying
/// on hardcoded tool registries in <see cref="ToolFilterHelper"/>.
/// </summary>
/// <remarks>
/// <para>For local/stdio MCP servers, this class:</para>
/// <list type="number">
///   <item>Starts the server process</item>
///   <item>Sends the MCP <c>initialize</c> request</item>
///   <item>Sends <c>notifications/initialized</c></item>
///   <item>Sends <c>tools/list</c> request</item>
///   <item>Parses the response and returns tool names in <c>{serverName}-{toolName}</c> format</item>
///   <item>Kills the process</item>
/// </list>
/// <para>The server is started temporarily and killed after discovery. The actual session
/// will start its own instance of the MCP server via the Copilot CLI.</para>
/// </remarks>
internal static class McpToolDiscovery
{
    /// <summary>
    /// Discover tools from a single local MCP server.
    /// </summary>
    /// <param name="serverName">The MCP server name (used as tool name prefix)</param>
    /// <param name="command">The command to start the server</param>
    /// <param name="args">Arguments for the command</param>
    /// <param name="env">Optional environment variables</param>
    /// <param name="cwd">Optional working directory</param>
    /// <param name="timeoutMs">Timeout in milliseconds for the entire operation (default 30s)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of tool names in <c>{serverName}-{toolName}</c> format</returns>
    public static async Task<List<string>> DiscoverToolsAsync(
        string serverName,
        string command,
        IList<string>? args = null,
        IDictionary<string, string>? env = null,
        string? cwd = null,
        int timeoutMs = 30000,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var token = cts.Token;

        var psi = BuildProcessStartInfo(command, args, env, cwd);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                $"Failed to start MCP server '{serverName}': {command}");

        // Capture stderr asynchronously for error reporting
        var stderrTask = process.StandardError.ReadToEndAsync(token);

        try
        {
            var reader = process.StandardOutput;
            var writer = process.StandardInput;

            // 1. Send initialize request
            await SendJsonRpcAsync(writer, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "CopilotShell", version = "1.0" }
                }
            }, token);

            // 2. Read initialize response
            await ReadResponseAsync(reader, 1, token);

            // 3. Send initialized notification (no id = notification)
            await SendJsonRpcAsync(writer, new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized"
            }, token);

            // 4. Send tools/list request
            await SendJsonRpcAsync(writer, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/list",
                @params = new { }
            }, token);

            // 5. Read tools/list response
            var toolsResponse = await ReadResponseAsync(reader, 2, token);

            // 6. Parse tool names
            var tools = new List<string>();
            if (toolsResponse.TryGetProperty("result", out var result) &&
                result.TryGetProperty("tools", out var toolsArray))
            {
                foreach (var tool in toolsArray.EnumerateArray())
                {
                    if (tool.TryGetProperty("name", out var nameProp))
                    {
                        var toolName = nameProp.GetString();
                        if (!string.IsNullOrEmpty(toolName))
                        {
                            tools.Add($"{serverName}-{toolName}");
                        }
                    }
                }
            }

            return tools;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("closed connection"))
        {
            // Server closed unexpectedly — include stderr for diagnostics
            string? stderr = null;
            try { stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
            catch { /* ignore */ }

            var msg = $"MCP server '{serverName}' closed connection unexpectedly";
            if (!string.IsNullOrWhiteSpace(stderr))
                msg += $": {stderr.Trim().Split('\n')[0]}"; // First line of stderr
            throw new InvalidOperationException(msg);
        }
        finally
        {
            KillProcess(process);
        }
    }

    /// <summary>
    /// Discover tools from specific servers in an MCP config dictionary.
    /// Only local/stdio servers are supported for discovery; remote servers are skipped.
    /// </summary>
    /// <param name="mcpServers">The MCP server config dictionary (from <see cref="McpConfigLoader"/>)</param>
    /// <param name="serverNames">Server names to discover tools for</param>
    /// <param name="timeoutMs">Timeout per server in milliseconds</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>
    /// Dictionary mapping server name to list of discovered tool names.
    /// Servers that fail or time out will have empty lists.
    /// </returns>
    public static async Task<Dictionary<string, List<string>>> DiscoverToolsForServersAsync(
        Dictionary<string, object> mcpServers,
        IEnumerable<string> serverNames,
        int timeoutMs = 30000,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in serverNames)
        {
            if (!mcpServers.TryGetValue(name, out var config))
                continue;

            if (config is not GitHub.Copilot.SDK.McpLocalServerConfig localConfig)
            {
                errors[name] = "Remote servers are not supported for tool discovery";
                result[name] = new List<string>();
                continue;
            }

            try
            {
                // Route through mcp-wrapper if available — reuses zombie daemon
                // for eligible servers instead of spawning a new process
                var wrapperPath = McpWrapperHelper.ResolveWrapperPath();
                string effectiveCommand;
                IList<string>? effectiveArgs;
                IDictionary<string, string>? effectiveEnv;
                string? effectiveCwd;

                if (wrapperPath != null)
                {
                    var wrapped = McpWrapperHelper.WrapConfig(localConfig, wrapperPath);
                    effectiveCommand = wrapped.Command;
                    effectiveArgs = wrapped.Args;
                    effectiveEnv = null;  // env is passed as --env args to wrapper
                    effectiveCwd = null;  // cwd is passed as --cwd arg to wrapper
                }
                else
                {
                    effectiveCommand = localConfig.Command;
                    effectiveArgs = localConfig.Args;
                    effectiveEnv = localConfig.Env;
                    effectiveCwd = localConfig.Cwd;
                }

                var tools = await DiscoverToolsAsync(
                    name,
                    effectiveCommand,
                    effectiveArgs,
                    effectiveEnv,
                    effectiveCwd,
                    timeoutMs,
                    ct);
                result[name] = tools;
            }
            catch (Exception ex)
            {
                errors[name] = ex.Message;
                result[name] = new List<string>();
            }
        }

        // Store errors for the caller to retrieve
        LastDiscoveryErrors = errors.Count > 0 ? errors : null;

        return result;
    }

    /// <summary>
    /// Errors from the most recent discovery operation, if any.
    /// Keys are server names, values are error messages.
    /// </summary>
    public static Dictionary<string, string>? LastDiscoveryErrors { get; private set; }

    /// <summary>
    /// Build a <see cref="ProcessStartInfo"/> for starting an MCP server.
    /// On Windows, commands without extensions (like <c>npx</c>, <c>uvx</c>) are
    /// run through <c>cmd.exe /c</c> to resolve <c>.cmd</c>/<c>.bat</c> scripts.
    /// </summary>
    private static ProcessStartInfo BuildProcessStartInfo(
        string command,
        IList<string>? args,
        IDictionary<string, string>? env,
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

        // On Windows, commands like 'npx', 'uvx' are actually .cmd files
        // that need cmd.exe to resolve them
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

        if (args != null)
        {
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
        }

        if (env != null)
        {
            foreach (var (key, value) in env)
                psi.Environment[key] = value;
        }

        if (!string.IsNullOrEmpty(cwd))
            psi.WorkingDirectory = cwd;

        return psi;
    }

    /// <summary>
    /// Send a JSON-RPC message to the MCP server's stdin (newline-delimited).
    /// </summary>
    private static async Task SendJsonRpcAsync(StreamWriter writer, object message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message);
        await writer.WriteLineAsync(json.AsMemory(), ct);
        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// Read lines from stdout until we find a JSON-RPC response with the expected id.
    /// Skips notifications, non-JSON lines, and log output.
    /// </summary>
    private static async Task<JsonElement> ReadResponseAsync(StreamReader reader, int expectedId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null)
                throw new InvalidOperationException("MCP server closed connection unexpectedly");

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var idProp))
                {
                    int id = idProp.ValueKind == JsonValueKind.Number
                        ? idProp.GetInt32()
                        : int.Parse(idProp.GetString()!);

                    if (id == expectedId)
                        return root.Clone();
                }
                // Skip notifications and other messages
            }
            catch (JsonException)
            {
                // Skip non-JSON lines (e.g., log output from server)
            }
        }

        throw new OperationCanceledException(ct);
    }

    /// <summary>
    /// Kill the MCP server process and its process tree.
    /// </summary>
    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
