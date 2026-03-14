using GitHub.Copilot.SDK;

namespace CopilotShell;

/// <summary>
/// Helper for managing tool filters and translating VS Code agent tool names to CLI equivalents.
/// MCP server filtering scopes which servers attach to a session.
/// Wildcard patterns and bare server names require MCP tool discovery to expand to exact names
/// since the CLI requires exact tool names in <c>availableTools</c>.
/// </summary>
internal static class ToolFilterHelper
{
    /// <summary>
    /// Maps VS Code Copilot agent tool names (from .agent.md files) to their
    /// Copilot CLI equivalents.
    /// </summary>
    private static readonly Dictionary<string, string[]> VsCodeToolMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vscode-vscodeAPI"] = new[] { "view", "create", "edit", "grep", "glob" },
        ["vscode/vscodeAPI"] = new[] { "view", "create", "edit", "grep", "glob" },
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
        ["agent"] = new[] { "task", "read_agent", "list_agents" },
    };

    /// <summary>
    /// CLI tools that are always included when an agent specifies a tools list.
    /// </summary>
    private static readonly string[] AlwaysIncludedTools = new[] { "skill" };

    /// <summary>
    /// Extract MCP tool patterns from agent tool lists for use in MCP server filtering.
    /// Filters out VS Code tool names — returns only MCP-relevant patterns
    /// (wildcards like <c>ado/*</c>, bare server names, exact MCP tool names).
    /// Slashes are normalized to dashes but wildcards are preserved for later expansion.
    /// </summary>
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
                if (VsCodeToolMappings.ContainsKey(pattern))
                    continue;

                var normalized = pattern.Replace('/', '-');
                if (VsCodeToolMappings.ContainsKey(normalized))
                    continue;

                if (!mcpPatterns.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    mcpPatterns.Add(normalized);
            }
        }

        return mcpPatterns.Count > 0 ? mcpPatterns.ToArray() : null;
    }

    /// <summary>
    /// Extract ALL tool refs from agent tool lists, translating VS Code tool names
    /// to CLI equivalents and preserving MCP patterns (wildcards and exact names).
    /// Used for building the session's <c>AvailableTools</c> list.
    /// </summary>
    public static string[]? GetAllToolRefsFromAgents(
        IEnumerable<CustomAgentConfig>? agents)
    {
        if (agents == null)
            return null;

        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Always include these CLI tools when agents specify a tools list
        foreach (var t in AlwaysIncludedTools)
            tools.Add(t);

        foreach (var agent in agents)
        {
            if (agent.Tools == null || agent.Tools.Count == 0)
                continue;

            foreach (var pattern in agent.Tools)
            {
                // Try VS Code mapping first
                if (VsCodeToolMappings.TryGetValue(pattern, out var mapped))
                {
                    foreach (var t in mapped)
                        tools.Add(t);
                    continue;
                }

                var normalized = pattern.Replace('/', '-');
                if (VsCodeToolMappings.TryGetValue(normalized, out var mappedNorm))
                {
                    foreach (var t in mappedNorm)
                        tools.Add(t);
                    continue;
                }

                // MCP pattern — keep as-is (wildcard or exact)
                tools.Add(normalized);
            }
        }

        return tools.Count > 0 ? tools.ToArray() : null;
    }

    /// <summary>
    /// Identify MCP server names that need tool discovery because they are referenced
    /// via wildcard (<c>ado-*</c>) or bare server name (<c>ado</c>) in the tool list.
    /// Exact tool names (e.g. <c>ev2-get_rollout_details</c>) do not need discovery.
    /// </summary>
    public static List<string> GetServersNeedingDiscovery(
        string[]? toolPatterns,
        Dictionary<string, object>? mcpConfig)
    {
        var result = new List<string>();
        if (toolPatterns == null || mcpConfig == null)
            return result;

        foreach (var pattern in toolPatterns)
        {
            var normalized = pattern.Replace('/', '-');
            string? serverName = null;

            if (normalized.EndsWith('*'))
            {
                // "ado-*" → "ado"
                serverName = normalized[..^1].TrimEnd('-');
            }
            else if (mcpConfig.ContainsKey(normalized))
            {
                // Bare server name (e.g. "ado", "kusto-mcp")
                serverName = normalized;
            }
            // Exact tool names like "ev2-get_rollout_details" don't need discovery

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
    /// Expand tool patterns into exact tool names for the CLI.
    /// Wildcards (<c>ado-*</c>) and bare server names (<c>ado</c>) are expanded
    /// against discovered MCP tools. Exact tool names pass through as-is.
    /// Returns null if no tool filter is specified (allow all).
    /// </summary>
    public static List<string>? ExpandToolPatterns(
        string[]? toolPatterns,
        Dictionary<string, List<string>>? discoveredMcpTools)
    {
        if (toolPatterns == null || toolPatterns.Length == 0)
            return null;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var allDiscovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (discoveredMcpTools != null)
        {
            foreach (var tools in discoveredMcpTools.Values)
                foreach (var tool in tools)
                    allDiscovered.Add(tool);
        }

        foreach (var pattern in toolPatterns)
        {
            var normalized = pattern.Replace('/', '-');

            if (normalized.EndsWith('*'))
            {
                // Wildcard: expand from discovered tools
                var prefix = normalized[..^1]; // "ado-*" → "ado-"
                foreach (var tool in allDiscovered)
                    if (tool.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        result.Add(tool);
            }
            else if (discoveredMcpTools != null && discoveredMcpTools.ContainsKey(normalized))
            {
                // Bare server name: add all its discovered tools
                foreach (var tool in discoveredMcpTools[normalized])
                    result.Add(tool);
            }
            else
            {
                // Exact tool name: pass through
                result.Add(normalized);
            }
        }

        return result.ToList();
    }

    /// <summary>
    /// Translate each agent's <c>Tools</c> list to CLI equivalents with per-agent scoping.
    /// VS Code tool names are mapped to CLI names. MCP patterns (wildcards and bare server
    /// names) are expanded against discovered tools. Each agent ends up with only its own
    /// tools, not the union of all agents' tools. Always-included tools (e.g. <c>skill</c>)
    /// are added to every agent.
    /// </summary>
    public static void TranslateAgentTools(
        IEnumerable<CustomAgentConfig>? agents,
        Dictionary<string, List<string>>? discoveredMcpTools)
    {
        if (agents == null)
            return;

        // Build flat set of all discovered tools for expansion
        var allDiscovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (discoveredMcpTools != null)
        {
            foreach (var tools in discoveredMcpTools.Values)
                foreach (var tool in tools)
                    allDiscovered.Add(tool);
        }

        foreach (var agent in agents)
        {
            if (agent.Tools == null || agent.Tools.Count == 0)
                continue;

            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Always include these
            foreach (var t in AlwaysIncludedTools)
                expanded.Add(t);

            foreach (var pattern in agent.Tools)
            {
                // Try VS Code mapping first
                if (VsCodeToolMappings.TryGetValue(pattern, out var mapped))
                {
                    foreach (var t in mapped)
                        expanded.Add(t);
                    continue;
                }
                var normalized = pattern.Replace('/', '-');
                if (VsCodeToolMappings.TryGetValue(normalized, out var mappedNorm))
                {
                    foreach (var t in mappedNorm)
                        expanded.Add(t);
                    continue;
                }

                // MCP pattern — expand wildcards and bare server names
                if (normalized.EndsWith('*'))
                {
                    var prefix = normalized[..^1];
                    foreach (var tool in allDiscovered)
                        if (tool.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            expanded.Add(tool);
                }
                else if (discoveredMcpTools != null && discoveredMcpTools.ContainsKey(normalized))
                {
                    foreach (var tool in discoveredMcpTools[normalized])
                        expanded.Add(tool);
                }
                else
                {
                    // Exact tool name — pass through
                    expanded.Add(normalized);
                }
            }

            agent.Tools = expanded.ToList();
        }
    }

    /// <summary>
    /// Filter the MCP server config to only include servers referenced in the tool list.
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

            if (normalized.EndsWith('*'))
            {
                var serverName = normalized[..^1].TrimEnd('-');
                if (mcpConfig.ContainsKey(serverName))
                    referencedServers.Add(serverName);
            }
            else if (mcpConfig.ContainsKey(normalized))
            {
                referencedServers.Add(normalized);
            }
            else
            {
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
