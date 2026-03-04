using System.Text.Json;
using System.Text.Json.Serialization;
using GitHub.Copilot.SDK;

namespace CopilotShell;

/// <summary>
/// Loads MCP server configurations from a JSON config file (e.g. mcp-config.json)
/// and converts them into the SDK's <see cref="McpLocalServerConfig"/> /
/// <see cref="McpRemoteServerConfig"/> objects for <see cref="SessionConfig.McpServers"/>.
/// </summary>
internal static class McpConfigLoader
{
    /// <summary>
    /// Reads a JSON config file and returns a dictionary suitable for
    /// <see cref="SessionConfig.McpServers"/>.
    /// </summary>
    /// <remarks>
    /// Supports two formats (auto-detected):
    /// <para><b>Copilot CLI format</b> — top-level <c>"mcpServers"</c>:</para>
    /// <code>
    /// {
    ///   "mcpServers": {
    ///     "name": { "command": "...", "args": [...], "env": { ... }, "tools": ["*"] }
    ///   }
    /// }
    /// </code>
    /// <para><b>VS Code format</b> (.vscode/mcp.json) — top-level <c>"servers"</c>:</para>
    /// <code>
    /// {
    ///   "servers": {
    ///     "name": { "command": "...", "args": [...], "env": { ... } }
    ///   }
    /// }
    /// </code>
    /// Entries with <c>"url"</c> are treated as remote servers; entries with
    /// <c>"command"</c> are local. Entries with <c>"disabled": true</c> are skipped.
    /// When <c>"tools"</c> is absent, it defaults to <c>["*"]</c>.
    /// </remarks>
    public static Dictionary<string, object> Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"MCP config file not found: {filePath}", filePath);

        var json = File.ReadAllText(filePath);
        var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        // Auto-detect format:
        //   Copilot CLI format  → { "mcpServers": { ... } }
        //   VS Code mcp.json   → { "servers": { ... } }
        JsonElement serversElement;
        if (doc.RootElement.TryGetProperty("mcpServers", out serversElement))
        {
            // standard copilot CLI format
        }
        else if (doc.RootElement.TryGetProperty("servers", out serversElement))
        {
            // VS Code .vscode/mcp.json format
        }
        else
        {
            throw new InvalidOperationException(
                $"MCP config file does not contain a 'mcpServers' or 'servers' property: {filePath}");
        }

        var result = new Dictionary<string, object>();

        foreach (var serverProp in serversElement.EnumerateObject())
        {
            var name = serverProp.Name;
            var entry = serverProp.Value;

            // Skip disabled servers
            if (entry.TryGetProperty("disabled", out var disabledProp) &&
                disabledProp.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            if (entry.TryGetProperty("url", out _))
            {
                result[name] = ParseRemoteServer(entry);
            }
            else
            {
                result[name] = ParseLocalServer(entry);
            }
        }

        return result;
    }

    private static McpLocalServerConfig ParseLocalServer(JsonElement entry)
    {
        var config = new McpLocalServerConfig
        {
            Tools = new List<string> { "*" },
            Type = "stdio"
        };

        if (entry.TryGetProperty("command", out var cmd))
            config.Command = cmd.GetString()!;

        if (entry.TryGetProperty("args", out var args))
            config.Args = args.EnumerateArray().Select(a => a.GetString()!).ToList();

        if (entry.TryGetProperty("env", out var env))
            config.Env = env.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString()!);

        if (entry.TryGetProperty("cwd", out var cwd))
        {
            var cwdStr = cwd.GetString();
            if (cwdStr is not null)
                config.Cwd = cwdStr;
        }

        if (entry.TryGetProperty("tools", out var tools))
            config.Tools = tools.EnumerateArray().Select(t => t.GetString()!).ToList();

        if (entry.TryGetProperty("type", out var type))
            config.Type = type.GetString();

        if (entry.TryGetProperty("timeout", out var timeout))
            config.Timeout = timeout.GetInt32();

        return config;
    }

    private static McpRemoteServerConfig ParseRemoteServer(JsonElement entry)
    {
        var config = new McpRemoteServerConfig
        {
            Tools = new List<string> { "*" },
            Type = "stdio"
        };

        if (entry.TryGetProperty("url", out var url))
            config.Url = url.GetString()!;

        if (entry.TryGetProperty("headers", out var headers))
            config.Headers = headers.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);

        if (entry.TryGetProperty("tools", out var tools))
            config.Tools = tools.EnumerateArray().Select(t => t.GetString()!).ToList();

        if (entry.TryGetProperty("type", out var type))
            config.Type = type.GetString() ?? string.Empty;

        if (entry.TryGetProperty("timeout", out var timeout))
            config.Timeout = timeout.GetInt32();

        return config;
    }
}
