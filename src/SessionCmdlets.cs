using System.Management.Automation;
using GitHub.Copilot.SDK;

namespace CopilotShell;

/// <summary>
/// Create a new Copilot conversation session on a running client.
/// </summary>
/// <example>
/// <code>$session = New-CopilotSession -Client $client -Model gpt-5</code>
/// <code>$session = New-CopilotSession $client -Model gpt-5 -SystemMessage "You are a pirate." -SystemMessageMode Replace</code>
/// <code>$session = New-CopilotSession $client -Model claude-sonnet-4.5 -Stream</code>
/// <code>$session = New-CopilotSession $client -McpConfigFile C:\code\project\mcp-config.json</code>
/// <code>$session = New-CopilotSession $client -Agent "my-custom-agent"</code>
/// <code>
/// # Define and use a custom agent inline
/// $agent = [GitHub.Copilot.SDK.CustomAgentConfig]@{ Name = 'reviewer'; Prompt = 'You are a code reviewer.'; Description = 'Reviews code changes' }
/// $session = New-CopilotSession $client -CustomAgents $agent -Agent reviewer
/// </code>
/// <code>
/// # Load a custom agent from a .agent.md file
/// $session = New-CopilotSession $client -CustomAgentFile .github\agents\ado-team.agent.md
/// </code>
/// </example>
[Cmdlet(VerbsCommon.New, "CopilotSession")]
[OutputType(typeof(CopilotSession))]
public sealed class NewCopilotSessionCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
        HelpMessage = "The CopilotClient to create the session on.")]
    public CopilotClient Client { get; set; } = null!;

    [Parameter(HelpMessage = "Custom session ID.")]
    public string? SessionId { get; set; }

    [Parameter(HelpMessage = "Model to use (e.g. gpt-5, claude-sonnet-4.5).")]
    public string? Model { get; set; }

    [Parameter(HelpMessage = "Reasoning effort: low, medium, high, xhigh.")]
    public string? ReasoningEffort { get; set; }

    [Parameter(HelpMessage = "Enable streaming of response chunks.")]
    public SwitchParameter Stream { get; set; }

    [Parameter(HelpMessage = "System message content.")]
    public string? SystemMessage { get; set; }

    [Parameter(HelpMessage = "System message mode: Append or Replace.")]
    public SystemMessageMode SystemMessageMode { get; set; } = SystemMessageMode.Append;

    [Parameter(HelpMessage = "List of tool names to allow.")]
    public string[]? AvailableTools { get; set; }

    [Parameter(HelpMessage = "List of tool names to exclude.")]
    public string[]? ExcludedTools { get; set; }

    [Parameter(HelpMessage = "Enable infinite sessions (automatic context compaction).")]
    public SwitchParameter InfiniteSessions { get; set; }

    [Parameter(HelpMessage = "Disable infinite sessions.")]
    public SwitchParameter NoInfiniteSessions { get; set; }

    [Parameter(HelpMessage = "Path to an MCP config JSON file (e.g. mcp-config.json) that defines MCP servers to attach to this session.")]
    public string? McpConfigFile { get; set; }

    [Parameter(HelpMessage = "Disable the MCP wrapper that fixes environment variable propagation. By default, local MCP servers are launched through mcp-wrapper to ensure env vars are set correctly.")]
    public SwitchParameter NoMcpWrapper { get; set; }

    [Parameter(HelpMessage = "Name of a custom agent to select for this session (e.g. 'my-agent'). The agent must be available in the Copilot runtime or defined via -CustomAgents.")]
    public string? Agent { get; set; }

    [Parameter(HelpMessage = "One or more CustomAgentConfig objects to register with the session. Use [GitHub.Copilot.SDK.CustomAgentConfig]@{ Name='...'; Prompt='...' } to create them.")]
    public CustomAgentConfig[]? CustomAgents { get; set; }

    [Parameter(HelpMessage = "Path(s) to .agent.md files that define custom agents. The agent name is derived from the filename (e.g. 'ado-team.agent.md' → 'ado-team').")]
    [Alias("AgentFile")]
    public string[]? CustomAgentFile { get; set; }

    protected override async Task ProcessRecordAsync()
    {
        var config = new SessionConfig();

        if (SessionId is not null) config.SessionId = SessionId;
        if (Model is not null) config.Model = Model;
        if (ReasoningEffort is not null) config.ReasoningEffort = ReasoningEffort;
        if (Stream.IsPresent) config.Streaming = true;

        if (SystemMessage is not null)
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode,
                Content = SystemMessage
            };
        }

        // Parse agent files early (needed for MCP server filtering and tool discovery)
        List<CustomAgentConfig>? allAgents = null;
        if (CustomAgents is not null || CustomAgentFile is not null)
        {
            allAgents = new List<CustomAgentConfig>();
            if (CustomAgents is not null)
                allAgents.AddRange(CustomAgents);
            if (CustomAgentFile is not null)
            {
                foreach (var file in CustomAgentFile)
                {
                    var resolvedPath = ResolvePSPath(file);
                    var parsed = AgentFileParser.Parse(resolvedPath);
                    allAgents.Add(parsed);
                    WriteVerbose($"Loaded agent '{parsed.Name}' from {Path.GetFileName(resolvedPath)}");
                }
            }
            config.CustomAgents = allAgents;
            WriteVerbose($"Registered {allAgents.Count} custom agent(s): {string.Join(", ", allAgents.Select(a => a.Name))}");
        }

        // Load MCP config
        Dictionary<string, McpServerConfig>? mcpConfig = null;

        // Determine effective tool filter for MCP server scoping.
        // If -AvailableTools is explicit, use that (+ agent refs).
        // If only agent tool refs exist (no explicit -AvailableTools), use agent refs
        // to filter servers so only referenced MCP servers are attached.
        var agentMcpRefs = ToolFilterHelper.GetMcpToolRefsFromAgents(allAgents);
        string[]? effectiveToolFilter = AvailableTools;
        if (AvailableTools != null && agentMcpRefs != null)
            effectiveToolFilter = AvailableTools.Concat(agentMcpRefs).ToArray();
        else if (AvailableTools == null && agentMcpRefs != null)
            effectiveToolFilter = agentMcpRefs;

        if (McpConfigFile is not null)
        {
            mcpConfig = McpConfigLoader.Load(ResolvePSPath(McpConfigFile));

            var filtered = ToolFilterHelper.FilterMcpServers(mcpConfig, effectiveToolFilter);

            // Wrap local MCP servers through mcp-wrapper for env var support (default)
            if (!NoMcpWrapper.IsPresent && filtered is not null)
            {
                var wrapperPath = McpWrapperHelper.ResolveWrapperPath();
                if (wrapperPath is not null)
                {
                    filtered = McpWrapperHelper.WrapLocalServers(filtered, wrapperPath);
                    WriteVerbose($"MCP servers wrapped via: {wrapperPath}");
                }
                else
                {
                    WriteWarning("mcp-wrapper executable not found — MCP servers will be launched directly (env vars may not propagate, no persistent connections).");
                }
            }

            config.McpServers = filtered;
        }

        // Pass AvailableTools to session — either explicit or derived from agent tool refs.
        // Wildcards (ado-*) and bare server names (ado) need discovery to expand to exact names.
        // GetAllToolRefsFromAgents includes translated VS Code tools + MCP patterns.
        var agentAllRefs = ToolFilterHelper.GetAllToolRefsFromAgents(allAgents);
        var sessionTools = AvailableTools ?? agentAllRefs;
        Dictionary<string, List<string>>? discoveredTools = null;
        if (sessionTools is not null && mcpConfig is not null)
        {
            var serversToDiscover = ToolFilterHelper.GetServersNeedingDiscovery(sessionTools, mcpConfig);
            if (serversToDiscover.Count > 0)
            {
                WriteVerbose($"Discovering tools from MCP servers: {string.Join(", ", serversToDiscover)}");
                discoveredTools = await McpToolDiscovery.DiscoverToolsForServersAsync(
                    mcpConfig, serversToDiscover);
                foreach (var (server, tools) in discoveredTools)
                {
                    if (tools.Count > 0)
                        WriteVerbose($"  {server}: {tools.Count} tools discovered");
                    else
                    {
                        var errorMsg = McpToolDiscovery.LastDiscoveryErrors?.GetValueOrDefault(server);
                        WriteWarning($"  {server}: no tools discovered — {errorMsg ?? "unknown error"}");
                    }
                }
            }
        }
        var mergedTools = ToolFilterHelper.ExpandToolPatterns(sessionTools, discoveredTools);
        if (mergedTools is not null)
        {
            config.AvailableTools = mergedTools;
            WriteVerbose($"Session tool list ({mergedTools.Count} tools): {string.Join(", ", mergedTools.Order())}");
        }
        if (ExcludedTools is not null) config.ExcludedTools = new List<string>(ExcludedTools);

        // Translate VS Code tool names and expand MCP patterns per-agent
        ToolFilterHelper.TranslateAgentTools(allAgents, discoveredTools);
        if (allAgents != null)
        {
            foreach (var agent in allAgents)
            {
                if (agent.Tools == null || agent.Tools.Count == 0)
                    WriteVerbose($"Agent '{agent.Name}' tools: <all session tools>");
                else
                    WriteVerbose($"Agent '{agent.Name}' tools ({agent.Tools.Count}): {string.Join(", ", agent.Tools.Order())}");
            }
        }

        if (InfiniteSessions.IsPresent)
        {
            config.InfiniteSessions = new InfiniteSessionConfig { Enabled = true };
        }
        else if (NoInfiniteSessions.IsPresent)
        {
            config.InfiniteSessions = new InfiniteSessionConfig { Enabled = false };
        }

        // Auto-approve tool permission requests using the SDK's built-in handler
        config.OnPermissionRequest = PermissionHandler.ApproveAll;

        // Pre-select agent at session creation (SDK 0.1.33+)
        // If a single custom agent was loaded and -Agent was not specified, auto-select it
        var agentToSelect = Agent;
        if (agentToSelect is null && config.CustomAgents?.Count == 1)
        {
            agentToSelect = config.CustomAgents[0].Name;
            WriteVerbose($"Auto-selecting sole custom agent: {agentToSelect}");
        }
        if (agentToSelect is not null)
        {
            config.Agent = agentToSelect;
            WriteVerbose($"Pre-selecting agent: {agentToSelect}");

            // Narrow session AvailableTools to the selected agent's scoped tools.
            // The CLI enforces visibility at the session level, not per-agent.
            var selectedAgent = allAgents?.FirstOrDefault(a =>
                string.Equals(a.Name, agentToSelect, StringComparison.OrdinalIgnoreCase));
            if (selectedAgent?.Tools is { Count: > 0 })
            {
                config.AvailableTools = selectedAgent.Tools;
                WriteVerbose($"Narrowed session tools to agent '{agentToSelect}' ({selectedAgent.Tools.Count} tools)");
            }
        }

        var session = await Client.CreateSessionAsync(config);

        WriteObject(session);
    }
}

/// <summary>
/// List all sessions or return a specific session's metadata.
/// </summary>
/// <example>
/// <code>Get-CopilotSession -Client $client</code>
/// </example>
[Cmdlet(VerbsCommon.Get, "CopilotSession")]
[OutputType(typeof(SessionMetadata))]
public sealed class GetCopilotSessionCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
        HelpMessage = "The CopilotClient.")]
    public CopilotClient Client { get; set; } = null!;

    [Parameter(Position = 1, HelpMessage = "Optional session ID to filter.")]
    public string? SessionId { get; set; }

    protected override async Task ProcessRecordAsync()
    {
        var sessions = await Client.ListSessionsAsync();

        if (SessionId is not null)
        {
            var match = sessions.FirstOrDefault(s => s.SessionId == SessionId);
            if (match is not null)
                WriteObject(match);
            else
                WriteWarning($"Session '{SessionId}' not found.");
        }
        else
        {
            foreach (var s in sessions)
                WriteObject(s);
        }
    }
}

/// <summary>
/// Resume an existing session by ID.
/// </summary>
[Cmdlet(VerbsLifecycle.Resume, "CopilotSession")]
[OutputType(typeof(CopilotSession))]
public sealed class ResumeCopilotSessionCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0,
        HelpMessage = "The CopilotClient.")]
    public CopilotClient Client { get; set; } = null!;

    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true,
        HelpMessage = "The session ID to resume.")]
    public string SessionId { get; set; } = null!;

    [Parameter(HelpMessage = "Name of a custom agent to select for the resumed session.")]
    public string? Agent { get; set; }

    [Parameter(HelpMessage = "One or more CustomAgentConfig objects to register with the resumed session.")]
    public CustomAgentConfig[]? CustomAgents { get; set; }

    [Parameter(HelpMessage = "Path(s) to .agent.md files that define custom agents.")]
    [Alias("AgentFile")]
    public string[]? CustomAgentFile { get; set; }

    protected override async Task ProcessRecordAsync()
    {
        var config = new ResumeSessionConfig();
        config.OnPermissionRequest = PermissionHandler.ApproveAll;

        if (CustomAgents is not null || CustomAgentFile is not null)
        {
            var allAgents = new List<CustomAgentConfig>();
            if (CustomAgents is not null)
                allAgents.AddRange(CustomAgents);
            if (CustomAgentFile is not null)
            {
                foreach (var file in CustomAgentFile)
                {
                    var resolvedPath = ResolvePSPath(file);
                    var parsed = AgentFileParser.Parse(resolvedPath);
                    allAgents.Add(parsed);
                    WriteVerbose($"Loaded agent '{parsed.Name}' from {Path.GetFileName(resolvedPath)}");
                }
            }
            config.CustomAgents = allAgents;
            WriteVerbose($"Registered {allAgents.Count} custom agent(s): {string.Join(", ", allAgents.Select(a => a.Name))}");
        }

        var session = await Client.ResumeSessionAsync(SessionId, config);

        // Select agent after resume if specified
        // If a single custom agent was loaded and -Agent was not specified, auto-select it
        var agentToSelect = Agent;
        if (agentToSelect is null && config.CustomAgents?.Count == 1)
        {
            agentToSelect = config.CustomAgents[0].Name;
            WriteVerbose($"Auto-selecting sole custom agent: {agentToSelect}");
        }
        if (agentToSelect is not null)
        {
            WriteVerbose($"Selecting agent: {agentToSelect}");
            await session.Rpc.Agent.SelectAsync(agentToSelect);
        }

        WriteObject(session);
    }
}

/// <summary>
/// Delete a session by ID.
/// </summary>
[Cmdlet(VerbsCommon.Remove, "CopilotSession", SupportsShouldProcess = true)]
public sealed class RemoveCopilotSessionCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0,
        HelpMessage = "The CopilotClient.")]
    public CopilotClient Client { get; set; } = null!;

    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true,
        HelpMessage = "The session ID to delete.")]
    public string SessionId { get; set; } = null!;

    protected override async Task ProcessRecordAsync()
    {
        if (ShouldProcess(SessionId, "Delete Copilot Session"))
        {
            await Client.DeleteSessionAsync(SessionId);
        }
    }
}

/// <summary>
/// Get all messages/events from a session.
/// </summary>
[Cmdlet(VerbsCommon.Get, "CopilotSessionMessages")]
[OutputType(typeof(SessionEvent))]
public sealed class GetCopilotSessionMessagesCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
        HelpMessage = "The CopilotSession.")]
    public CopilotSession Session { get; set; } = null!;

    protected override async Task ProcessRecordAsync()
    {
        var messages = await Session.GetMessagesAsync();
        foreach (var msg in messages)
            WriteObject(msg);
    }
}

/// <summary>
/// Abort the currently processing message in a session.
/// </summary>
[Cmdlet(VerbsLifecycle.Stop, "CopilotSession")]
public sealed class StopCopilotSessionCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
        HelpMessage = "The CopilotSession to abort.")]
    public CopilotSession Session { get; set; } = null!;

    protected override async Task ProcessRecordAsync()
    {
        await Session.AbortAsync();
    }
}

/// <summary>
/// Disconnect (dispose) a local session object to free resources without deleting
/// the session from the server. The session can be resumed later with
/// <c>Resume-CopilotSession</c> using the same session ID.
/// </summary>
/// <example>
/// <code>
/// $id = $session.SessionId
/// Disconnect-CopilotSession $session
/// # later...
/// $session = Resume-CopilotSession $client $id
/// </code>
/// </example>
[Cmdlet(VerbsCommunications.Disconnect, "CopilotSession")]
public sealed class DisconnectCopilotSessionCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
        HelpMessage = "The CopilotSession to disconnect (dispose locally).")]
    public CopilotSession Session { get; set; } = null!;

    protected override async Task ProcessRecordAsync()
    {
        var sessionId = Session.SessionId;
        await Session.DisposeAsync();
        WriteVerbose($"Session '{sessionId}' disconnected. Use Resume-CopilotSession to reconnect.");
    }
}
