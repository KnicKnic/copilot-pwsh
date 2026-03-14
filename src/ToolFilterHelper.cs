using GitHub.Copilot.SDK;

namespace CopilotShell;

/// <summary>
/// Helper for managing tool filters.
/// Supports wildcard patterns, bare MCP server name expansion, and dynamic MCP tool discovery.
/// </summary>
/// <remarks>
/// <para>The copilot CLI does NOT support wildcards in <c>availableTools</c> — it requires exact tool names.
/// MCP tools are named <c>{serverName}-{toolName}</c> (e.g., <c>kusto-mcp-kusto_query</c>).
/// This helper expands wildcard patterns and bare server names against dynamically discovered
/// MCP tools so that exact names are sent to the CLI.</para>
/// <para>Supported patterns in <c>-AvailableTools</c>:</para>
/// <list type="bullet">
///   <item><c>"ado-*"</c> — wildcard: expands all discovered tools starting with <c>ado-</c></item>
///   <item><c>"ado"</c> — bare server name: auto-detected and expanded like <c>ado-*</c></item>
///   <item><c>"kusto-mcp"</c> — bare server name: expands to all <c>kusto-mcp-*</c> tools</item>
///   <item><c>"kusto-mcp-kusto_query"</c> — exact tool name: passed through as-is</item>
///   <item><c>"view"</c> — built-in tool: passed through as-is</item>
///   <item><c>"powershell"</c> — shorthand: expands to all <c>*_powershell</c> core tools</item>
/// </list>
/// <para>Server name and wildcard patterns require <c>-McpConfigFile</c> so that the server
/// can be started temporarily to discover its tools via the MCP <c>tools/list</c> protocol.</para>
/// </remarks>
internal static class ToolFilterHelper
{
    /// <summary>
    /// Core Copilot CLI tools that are always included when <c>-AvailableTools</c> is specified.
    /// </summary>
    private static readonly HashSet<string> CoreTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_powershell",
        "read_powershell",
        "stop_powershell",
        "list_powershell",
        "view",
        "create",
        "edit",
        "grep",
        "glob",
        "web_fetch",
        "fetch_copilot_cli_documentation",
        "report_intent",
        "update_todo",
        "task",
        "read_agent",
        "list_agents"
    };

    /// <summary>
    /// Core CLI tools for agents — excludes agent orchestration tools (<c>task</c>,
    /// <c>read_agent</c>, <c>list_agents</c>) that cause recursive delegation loops
    /// when a model is already running as a specific agent.
    /// </summary>
    private static readonly HashSet<string> AgentCoreTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_powershell",
        "read_powershell",
        "stop_powershell",
        "list_powershell",
        "view",
        "create",
        "edit",
        "grep",
        "glob",
        "web_fetch",
        "fetch_copilot_cli_documentation",
        "report_intent",
        "update_todo",
    };

    /// <summary>
    /// Shorthand expansions (e.g., "powershell" → all powershell tools).
    /// </summary>
    private static readonly Dictionary<string, string[]> Shorthands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["powershell"] = new[] { "write_powershell", "read_powershell", "stop_powershell", "list_powershell" }
    };

    /// <summary>
    /// Expand user-specified tool patterns (wildcards, bare server names, shorthands)
    /// into exact tool names, always including core CLI tools.
    /// If userTools is null, returns null (allowing all tools).
    /// Wildcard patterns (e.g. "ado-*") and bare MCP server names (e.g. "ado", "kusto-mcp")
    /// are expanded against dynamically discovered MCP tools.
    /// Raw wildcard patterns are NOT passed to the CLI (it doesn't support them).
    /// </summary>
    public static List<string>? ExpandToolPatterns(
        string[]? userTools,
        Dictionary<string, List<string>>? discoveredMcpTools = null)
    {
        // If user didn't specify any tools, don't filter (allow all)
        if (userTools == null || userTools.Length == 0)
            return null;

        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Always include core tools
        foreach (var tool in CoreTools)
            merged.Add(tool);

        // Build a set of all discovered tools for expansion
        var discoveredTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (discoveredMcpTools != null)
        {
            foreach (var tools in discoveredMcpTools.Values)
            {
                foreach (var tool in tools)
                    discoveredTools.Add(tool);
            }
        }

        // Process user-specified tools
        foreach (var pattern in userTools)
        {
            // Normalize: replace / with - (MCP tools use dashes)
            var normalized = pattern.Replace('/', '-');

            // Check for shorthands first (e.g., "powershell" → all powershell tools)
            if (Shorthands.TryGetValue(normalized, out var shorthandTools))
            {
                foreach (var tool in shorthandTools)
                    merged.Add(tool);
                continue;
            }

            if (normalized.EndsWith('*'))
            {
                // Wildcard pattern - expand from discovered tools
                var prefix = normalized[..^1]; // Remove trailing *

                foreach (var tool in discoveredTools)
                {
                    if (tool.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        merged.Add(tool);
                }

                // Do NOT pass the raw wildcard pattern - the CLI doesn't understand it
            }
            else if (IsMcpServerPrefix(normalized, discoveredTools))
            {
                // Bare MCP server name (e.g., "ado", "kusto-mcp", "grafana-mcp")
                // Expand like wildcard from discovered tools
                var prefix = normalized + "-";
                foreach (var tool in discoveredTools)
                {
                    if (tool.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        merged.Add(tool);
                }
            }
            else
            {
                // Exact tool name
                merged.Add(normalized);
            }
        }

        return merged.ToList();
    }

    /// <summary>
    /// Identify MCP server names referenced in user-specified tools that exist in the MCP config.
    /// These servers need dynamic tool discovery via <see cref="McpToolDiscovery"/>.
    /// </summary>
    /// <param name="userTools">User-specified tool patterns</param>
    /// <param name="mcpConfig">MCP server configuration dictionary (from <see cref="McpConfigLoader"/>)</param>
    /// <returns>List of server names that need discovery</returns>
    public static List<string> GetServersNeedingDiscovery(
        string[]? userTools,
        Dictionary<string, object>? mcpConfig)
    {
        var result = new List<string>();
        if (userTools == null || mcpConfig == null)
            return result;

        foreach (var pattern in userTools)
        {
            var normalized = pattern.Replace('/', '-');

            // Skip shorthands
            if (Shorthands.ContainsKey(normalized))
                continue;

            string? serverName = null;
            if (normalized.EndsWith('*'))
            {
                // "ado-*" → "ado", "grafana-mcp-*" → "grafana-mcp"
                serverName = normalized[..^1].TrimEnd('-');
            }
            else if (mcpConfig.ContainsKey(normalized))
            {
                // Bare server name that's in the MCP config (e.g., "ado", "kusto-mcp")
                serverName = normalized;
            }
            else
            {
                // Exact tool name — check if prefix matches a server
                foreach (var key in mcpConfig.Keys)
                {
                    if (normalized.StartsWith(key + "-", StringComparison.OrdinalIgnoreCase))
                    {
                        serverName = key;
                        break;
                    }
                }
            }

            if (serverName != null
                && mcpConfig.ContainsKey(serverName)
                && !result.Contains(serverName, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(serverName);
            }
        }

        return result;
    }

    /// <summary>
    /// Check if the given name matches a server prefix in the provided tool set.
    /// A name is considered a server prefix if any tool starts with "{name}-".
    /// </summary>
    private static bool IsMcpServerPrefix(string name, IEnumerable<string> toolSet)
    {
        var prefix = name + "-";
        foreach (var tool in toolSet)
        {
            if (tool.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Maps VS Code Copilot agent tool names (from .agent.md files) to their
    /// Copilot CLI equivalents. These names use namespace/tool format in VS Code
    /// but have different names in the CLI.
    /// </summary>
    private static readonly Dictionary<string, string[]> VsCodeToolMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        // VS Code tool names → CLI tool names
        ["vscode-vscodeAPI"] = new[] { "view", "create", "edit", "grep", "glob" },
        ["vscode/vscodeAPI"] = new[] { "view", "create", "edit", "grep", "glob" },
        // execute/runTask and execute/createAndRunTask are VS Code-only concepts
        // (run VS Code tasks). No CLI equivalent — map to empty to drop them.
        ["execute-runTask"] = Array.Empty<string>(),
        ["execute/runTask"] = Array.Empty<string>(),
        ["execute-createAndRunTask"] = Array.Empty<string>(),
        ["execute/createAndRunTask"] = Array.Empty<string>(),
        ["execute-runInTerminal"] = new[] { "write_powershell", "read_powershell", "stop_powershell", "list_powershell" },
        ["execute/runInTerminal"] = new[] { "write_powershell", "read_powershell", "stop_powershell", "list_powershell" },
        ["read-readFile"] = new[] { "view" },
        ["read/readFile"] = new[] { "view" },
        ["web-fetch"] = new[] { "web_fetch" },
        ["web/fetch"] = new[] { "web_fetch" },
        ["search"] = new[] { "grep", "glob" },
        ["edit"] = new[] { "edit", "create" },
    };

    /// <summary>
    /// Extract MCP tool patterns from agent tool lists, suitable for use with
    /// <see cref="ExpandToolPatterns"/> at the session level. Filters out VS Code
    /// tool names, shorthands, and core tools — returns only MCP-relevant patterns
    /// (wildcards like <c>ado-*</c>, exact MCP tool names like <c>ev2-get_rollout_details</c>).
    /// </summary>
    /// <returns>Normalized MCP patterns, or null if no MCP patterns found.</returns>
    public static string[]? GetMcpToolRefsFromAgents(
        IEnumerable<CustomAgentConfig>? agents)
    {
        if (agents == null)
            return null;

        var mcpPatterns = new List<string>();
        foreach (var agent in agents)
        {
            if (agent.Tools == null || agent.Tools.Count == 0)
                continue;

            foreach (var pattern in agent.Tools)
            {
                // Skip VS Code tool names (they map to core tools)
                if (VsCodeToolMappings.ContainsKey(pattern))
                    continue;

                var normalized = pattern.Replace('/', '-');
                if (VsCodeToolMappings.ContainsKey(normalized))
                    continue;
                if (Shorthands.ContainsKey(normalized))
                    continue;

                // Skip things that are already core tools
                if (CoreTools.Contains(normalized) || AgentCoreTools.Contains(normalized))
                    continue;

                // This is an MCP pattern — keep it (normalized)
                if (!mcpPatterns.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    mcpPatterns.Add(normalized);
            }
        }

        return mcpPatterns.Count > 0 ? mcpPatterns.ToArray() : null;
    }

    /// <summary>
    /// Process each agent's <c>Tools</c> list to handle VS Code tool name mappings
    /// and MCP tool references. When agents reference MCP tools (wildcards, bare server
    /// names, or exact MCP tool names), the Tools list is cleared (<c>null</c>) so
    /// the agent inherits all session tools. The CLI's internal tool registry may use
    /// different names than our discovery — setting Tools to null avoids mismatches.
    /// Tool scoping is handled at the session level via <see cref="ExpandToolPatterns"/>
    /// using MCP patterns extracted by <see cref="GetMcpToolRefsFromAgents"/>.
    /// </summary>
    /// <param name="agents">Custom agents whose tools may need processing</param>
    /// <param name="discoveredMcpTools">Discovered tools keyed by server name (from <see cref="McpToolDiscovery"/>)</param>
    public static void ExpandAgentTools(
        IEnumerable<CustomAgentConfig>? agents,
        Dictionary<string, List<string>>? discoveredMcpTools)
    {
        if (agents == null)
            return;

        // Build a flat set of all discovered tools for detecting MCP references
        var allDiscovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (discoveredMcpTools != null)
        {
            foreach (var tools in discoveredMcpTools.Values)
            {
                foreach (var tool in tools)
                    allDiscovered.Add(tool);
            }
        }

        foreach (var agent in agents)
        {
            if (agent.Tools == null || agent.Tools.Count == 0)
                continue;

            bool hasMcpTools = false;

            foreach (var pattern in agent.Tools)
            {
                // VS Code mappings and shorthands are non-MCP — skip them
                if (VsCodeToolMappings.ContainsKey(pattern))
                    continue;

                var normalized = pattern.Replace('/', '-');
                if (VsCodeToolMappings.ContainsKey(normalized))
                    continue;
                if (Shorthands.ContainsKey(normalized))
                    continue;

                // Check if this references MCP tools
                if (normalized.EndsWith('*'))
                {
                    // Wildcard like "ado-*" — definitely MCP
                    hasMcpTools = true;
                    break;
                }
                else if (IsMcpServerPrefix(normalized, allDiscovered))
                {
                    // Bare server name like "ado" — definitely MCP
                    hasMcpTools = true;
                    break;
                }
                else
                {
                    // Check if it matches a discovered MCP tool
                    if (allDiscovered.Contains(normalized))
                    {
                        hasMcpTools = true;
                        break;
                    }
                }
            }

            if (hasMcpTools)
            {
                // Agent references MCP tools — clear the Tools list so the agent
                // inherits all session tools. The CLI's tool registry uses its own
                // naming, and an explicit allowlist causes mismatches.
                // Tool scoping is enforced at the session level instead.
                agent.Tools = null;
            }
            else
            {
                // Agent only uses VS Code/core tool names — translate VS Code names
                // to CLI equivalents so the CLI can match them.
                var translated = new List<string>();
                foreach (var pattern in agent.Tools)
                {
                    if (VsCodeToolMappings.TryGetValue(pattern, out var mapped))
                    {
                        translated.AddRange(mapped);
                        continue;
                    }
                    var normalized = pattern.Replace('/', '-');
                    if (VsCodeToolMappings.TryGetValue(normalized, out var mappedNorm))
                    {
                        translated.AddRange(mappedNorm);
                        continue;
                    }
                    if (Shorthands.TryGetValue(normalized, out var shorthandTools))
                    {
                        translated.AddRange(shorthandTools);
                        continue;
                    }
                    // Pass through as-is (core tool name or unknown)
                    translated.Add(normalized);
                }
                agent.Tools = translated.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    /// <summary>
    /// Filter the MCP server config to only include servers referenced in <c>-AvailableTools</c>.
    /// Matches bare names ("ado"), wildcards ("ado-*"), and exact tool names ("ev2-get_rollout_details").
    /// If <c>userTools</c> is null/empty, returns the full config (no filtering).
    /// </summary>
    public static Dictionary<string, object>? FilterMcpServers(
        Dictionary<string, object>? mcpConfig,
        string[]? userTools)
    {
        if (mcpConfig == null) return null;
        if (userTools == null || userTools.Length == 0) return mcpConfig;

        var referencedServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in userTools)
        {
            var normalized = pattern.Replace('/', '-');

            if (Shorthands.ContainsKey(normalized))
                continue;

            if (normalized.EndsWith('*'))
            {
                // "ado-*" → "ado"
                var serverName = normalized[..^1].TrimEnd('-');
                if (mcpConfig.ContainsKey(serverName))
                    referencedServers.Add(serverName);
            }
            else if (mcpConfig.ContainsKey(normalized))
            {
                // Bare server name (e.g., "ado", "kusto-mcp")
                referencedServers.Add(normalized);
            }
            else
            {
                // Exact tool name — check if prefix matches a server
                foreach (var serverName in mcpConfig.Keys)
                {
                    if (normalized.StartsWith(serverName + "-", StringComparison.OrdinalIgnoreCase))
                    {
                        referencedServers.Add(serverName);
                        break;
                    }
                }
            }
        }

        if (referencedServers.Count == 0)
            return new Dictionary<string, object>();

        var filtered = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in referencedServers)
        {
            if (mcpConfig.TryGetValue(server, out var config))
                filtered[server] = config;
        }
        return filtered;
    }

}
