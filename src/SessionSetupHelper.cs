using GitHub.Copilot.SDK;

namespace CopilotShell;

internal sealed class SessionSetupOptions
{
    public CustomAgentConfig[]? CustomAgents { get; init; }
    public string[]? CustomAgentFiles { get; init; }
    public string[]? AvailableTools { get; init; }
    public string[]? ExcludedTools { get; init; }
    public string? McpConfigFile { get; init; }
    public bool NoMcpWrapper { get; init; }
    public string? Agent { get; init; }
    public bool AgentWasSpecified { get; init; }
    public string? PromptFileAgent { get; init; }
    public Func<string, string> ResolvePath { get; init; } = static path => path;
    public Action<string> WriteVerbose { get; init; } = static _ => { };
    public Action<string> WriteWarning { get; init; } = static _ => { };
}

internal sealed class SessionSetupResult
{
    public string? AgentToSelect { get; init; }
    public CustomAgentConfig? SelectedAgent { get; init; }
    public bool InlinedSelectedAgentPrompt { get; init; }
}

internal static class SessionSetupHelper
{
    public static async Task<SessionSetupResult> ConfigureAsync(
        SessionConfig sessionConfig,
        SessionSetupOptions options,
        CancellationToken cancellationToken = default)
    {
        var allAgents = LoadAgents(options);
        if (allAgents is not null)
        {
            sessionConfig.CustomAgents = allAgents;
            options.WriteVerbose($"Registered {allAgents.Count} custom agent(s): {string.Join(", ", allAgents.Select(a => a.Name))}");
        }

        Dictionary<string, McpServerConfig>? mcpConfig = null;
        Dictionary<string, McpServerConfig>? attachedMcpServers = null;

        var agentMcpRefs = ToolFilterHelper.GetMcpToolRefsFromAgents(allAgents);
        string[]? effectiveToolFilter = options.AvailableTools;
        if (options.AvailableTools != null && agentMcpRefs != null)
            effectiveToolFilter = options.AvailableTools.Concat(agentMcpRefs).ToArray();
        else if (options.AvailableTools == null && agentMcpRefs != null)
            effectiveToolFilter = agentMcpRefs;

        if (options.McpConfigFile is not null)
        {
            options.WriteVerbose($"Loading MCP config from: {options.McpConfigFile}");
            mcpConfig = McpConfigLoader.Load(options.ResolvePath(options.McpConfigFile));
            options.WriteVerbose($"Loaded {mcpConfig.Count} MCP server(s): {string.Join(", ", mcpConfig.Keys)}");

            var filtered = ToolFilterHelper.FilterMcpServers(mcpConfig, effectiveToolFilter);

            if (!options.NoMcpWrapper && filtered is not null)
            {
                var wrapperPath = McpWrapperHelper.ResolveWrapperPath();
                if (wrapperPath is not null)
                {
                    filtered = McpWrapperHelper.WrapLocalServers(filtered, wrapperPath);
                    options.WriteVerbose($"MCP servers wrapped via: {wrapperPath}");
                }
                else
                {
                    options.WriteWarning("mcp-wrapper executable not found — MCP servers will be launched directly (env vars may not propagate).");
                }
            }

            attachedMcpServers = filtered;
            sessionConfig.McpServers = attachedMcpServers;
            if (attachedMcpServers is not null)
                options.WriteVerbose($"MCP servers attached to session: {string.Join(", ", attachedMcpServers.Keys)}");
        }

        var agentAllRefs = ToolFilterHelper.GetAllToolRefsFromAgents(allAgents);
        var sessionTools = options.AvailableTools ?? agentAllRefs;
        Dictionary<string, List<string>>? discoveredTools = null;
        if (sessionTools is not null && mcpConfig is not null)
        {
            var serversToDiscover = ToolFilterHelper.GetServersNeedingDiscovery(sessionTools, mcpConfig);
            if (serversToDiscover.Count > 0)
            {
                options.WriteVerbose($"Discovering tools from MCP servers: {string.Join(", ", serversToDiscover)}");
                discoveredTools = await McpToolDiscovery.DiscoverToolsForServersAsync(
                    mcpConfig, serversToDiscover, ct: cancellationToken);
                foreach (var (server, tools) in discoveredTools)
                {
                    if (tools.Count > 0)
                        options.WriteVerbose($"  {server}: {tools.Count} tools discovered");
                    else
                    {
                        var errorMsg = McpToolDiscovery.LastDiscoveryErrors?.GetValueOrDefault(server);
                        options.WriteWarning($"  {server}: no tools discovered — {errorMsg ?? "unknown error"}");
                    }
                }
            }
        }

        var mergedTools = ToolFilterHelper.ExpandToolPatterns(sessionTools, discoveredTools);
        if (mergedTools is not null)
        {
            sessionConfig.AvailableTools = mergedTools;
            options.WriteVerbose($"Session tool list ({mergedTools.Count} tools): {string.Join(", ", mergedTools.Order())}");
        }
        if (options.ExcludedTools is not null)
            sessionConfig.ExcludedTools = new List<string>(options.ExcludedTools);

        ToolFilterHelper.TranslateAgentTools(allAgents, discoveredTools);
        if (allAgents != null)
        {
            foreach (var agent in allAgents)
            {
                if (agent.Tools == null || agent.Tools.Count == 0)
                    options.WriteVerbose($"Agent '{agent.Name}' tools: <all session tools>");
                else
                    options.WriteVerbose($"Agent '{agent.Name}' tools ({agent.Tools.Count}): {string.Join(", ", agent.Tools.Order())}");
            }
        }

        var agentToSelect = ResolveAgentToSelect(sessionConfig, options);
        CustomAgentConfig? selectedAgent = null;
        var inlineSelectedAgentPrompt = false;

        if (agentToSelect is not null)
        {
            selectedAgent = allAgents?.FirstOrDefault(a =>
                string.Equals(a.Name, agentToSelect, StringComparison.OrdinalIgnoreCase));

            if (selectedAgent?.Tools is { Count: > 0 })
            {
                sessionConfig.AvailableTools = selectedAgent.Tools;
                options.WriteVerbose($"Narrowed session tools to agent '{agentToSelect}' ({selectedAgent.Tools.Count} tools): {string.Join(", ", selectedAgent.Tools.Order())}");
            }

            if (selectedAgent is not null && attachedMcpServers is not null && attachedMcpServers.Count > 0)
            {
                selectedAgent.McpServers = attachedMcpServers;
                options.WriteVerbose($"Attached MCP servers to agent '{agentToSelect}': {string.Join(", ", attachedMcpServers.Keys.Order())}");
            }

            if (AgentUsesMcpTools(selectedAgent, mcpConfig))
            {
                inlineSelectedAgentPrompt = true;
                sessionConfig.CustomAgents = null;
                AppendAgentPromptToSystemMessage(sessionConfig, selectedAgent!);
                options.WriteVerbose($"Inlining agent '{agentToSelect}' prompt instead of selecting it because SDK agent-scoped MCP tools are not exposed to the model.");
            }
            else
            {
                sessionConfig.Agent = agentToSelect;
                options.WriteVerbose($"Pre-selecting agent: {agentToSelect}");
            }
        }

        return new SessionSetupResult
        {
            AgentToSelect = agentToSelect,
            SelectedAgent = selectedAgent,
            InlinedSelectedAgentPrompt = inlineSelectedAgentPrompt
        };
    }

    private static List<CustomAgentConfig>? LoadAgents(SessionSetupOptions options)
    {
        if (options.CustomAgents is null && options.CustomAgentFiles is null)
            return null;

        var allAgents = new List<CustomAgentConfig>();
        if (options.CustomAgents is not null)
            allAgents.AddRange(options.CustomAgents);

        if (options.CustomAgentFiles is not null)
        {
            foreach (var file in options.CustomAgentFiles)
            {
                var resolvedPath = options.ResolvePath(file);
                var parsed = AgentFileParser.Parse(resolvedPath);
                allAgents.Add(parsed);
                options.WriteVerbose($"Loaded agent '{parsed.Name}' from {Path.GetFileName(resolvedPath)}");
            }
        }

        return allAgents;
    }

    private static string? ResolveAgentToSelect(SessionConfig sessionConfig, SessionSetupOptions options)
    {
        var agentToSelect = options.AgentWasSpecified ? options.Agent : null;
        if (agentToSelect is null && options.PromptFileAgent is not null)
        {
            agentToSelect = options.PromptFileAgent;
            options.WriteVerbose($"Using agent from prompt file: {agentToSelect}");
        }
        if (agentToSelect is null && sessionConfig.CustomAgents?.Count == 1)
        {
            agentToSelect = sessionConfig.CustomAgents[0].Name;
            options.WriteVerbose($"Auto-selecting sole custom agent: {agentToSelect}");
        }

        return agentToSelect;
    }

    private static bool AgentUsesMcpTools(CustomAgentConfig? agent, Dictionary<string, McpServerConfig>? mcpConfig)
    {
        if (agent?.Tools is not { Count: > 0 } || mcpConfig is null || mcpConfig.Count == 0)
            return false;

        foreach (var tool in agent.Tools)
        {
            var normalized = tool.Replace('/', '-');
            foreach (var serverName in mcpConfig.Keys)
            {
                if (normalized.Equals(serverName, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(serverName + "-", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AppendAgentPromptToSystemMessage(SessionConfig sessionConfig, CustomAgentConfig agent)
    {
        if (string.IsNullOrWhiteSpace(agent.Prompt))
            return;

        if (sessionConfig.SystemMessage is null)
        {
            sessionConfig.SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = agent.Prompt
            };
            return;
        }

        sessionConfig.SystemMessage.Content = string.IsNullOrWhiteSpace(sessionConfig.SystemMessage.Content)
            ? agent.Prompt
            : $"{sessionConfig.SystemMessage.Content}{Environment.NewLine}{Environment.NewLine}{agent.Prompt}";
    }
}
