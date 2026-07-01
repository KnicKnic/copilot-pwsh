using GitHub.Copilot;

namespace CopilotShell;

internal sealed class SessionSetupOptions
{
    public CustomAgentConfig[]? CustomAgents { get; init; }
    public string[]? CustomAgentFiles { get; init; }
    public string[]? AvailableTools { get; init; }
    public bool IsolatedDefaultAgent { get; init; }
    public string[]? ExcludedTools { get; init; }
    public string[]? SkillDirectories { get; init; }
    public string[]? DisabledSkills { get; init; }
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
}

/// <summary>
/// Wires custom agents, MCP servers, tool filters, and skills onto a session config.
///
/// <para>There are two entry points that share a common configuration core but differ in how the
/// selected agent is applied:</para>
/// <list type="bullet">
///   <item><see cref="ConfigureAsync"/> — for <b>creating</b> a session (<see cref="SessionConfig"/>).
///   The agent (from <c>-Agent</c> or a prompt file) is <i>pre-selected</i> by setting
///   <see cref="SessionConfigBase.Agent"/> before the session is created. A sole custom agent is
///   never auto-selected.</item>
///   <item><see cref="ConfigureResume"/> — for <b>resuming</b> a session (<see cref="ResumeSessionConfig"/>).
///   The config's <see cref="SessionConfigBase.Agent"/> is left unset; the agent name is returned so the
///   caller can apply it <i>after</i> resume via <c>session.Rpc.Agent.SelectAsync</c>. An agent is selected
///   only when explicitly requested via <c>-Agent</c>; a sole custom agent is never auto-selected.</item>
/// </list>
///
/// <para>Shared configuration core (intentionally minimal — applies to both paths):</para>
/// <list type="bullet">
///   <item>MCP servers, when a config file is given, are always attached to the session as-is
///   (optionally wrapped). They are never filtered by tool list nor attached to individual agents.</item>
///   <item>The session-level tool filter (<see cref="SessionConfigBase.AvailableTools"/>) is only set when
///   the caller explicitly passes <c>-AvailableTools</c> and/or <c>-IsolatedDefaultAgent</c>; otherwise
///   the session inherits all tools.
///   <c>-IsolatedDefaultAgent</c> (only when no agent is selected) restricts the session — and therefore
///   the built-in default agent it caps — to <see cref="BuiltInTools.Isolated"/> minus
///   <c>exit_plan_mode</c>/<c>ask_user</c>.</item>
///   <item>Agent <c>Tools</c> lists are passed through verbatim — including MCP selectors like
///   <c>"&lt;server&gt;/*"</c>. (VS Code tool-group names from <c>.agent.md</c> files are translated
///   to CLI tools earlier, in <see cref="AgentFileParser"/>.) No wildcard expansion and no MCP
///   tool discovery are performed.</item>
///   <item>Skills are off unless skill directories are supplied; passing any directory sets
///   <see cref="SessionConfigBase.EnableSkills"/> and registers the (resolved) directories. Disabled
///   skill names are passed through verbatim. See <see cref="ApplySkills"/>.</item>
/// </list>
/// </summary>
internal static class SessionSetupHelper
{
    // The isolated default agent's tool set: BuiltInTools.Isolated minus exit_plan_mode/ask_user.
    //   exit_plan_mode / ask_user are interactive planning/prompting tools that don't make sense
    //   for an unattended default agent. send_inbox / context_board are intentionally KEPT so the
    //   default agent can still participate in the inbox / dynamic-context-board machinery.
    private static readonly string[] IsolatedDefaultAgentExcluded = { "exit_plan_mode", "ask_user" };

    private static readonly IReadOnlyList<string> IsolatedDefaultAgentTools =
        BuiltInTools.Isolated.Where(t => !IsolatedDefaultAgentExcluded.Contains(t, StringComparer.Ordinal)).ToList();

    private static void AddTools(List<string> target, IEnumerable<string> tools)
    {
        foreach (var tool in tools)
        {
            if (!target.Contains(tool, StringComparer.Ordinal))
                target.Add(tool);
        }
    }

    /// <summary>
    /// Applies skill configuration onto a session config. Passing one or more skill directories
    /// turns skills on (<see cref="SessionConfigBase.EnableSkills"/> = <c>true</c>) and registers
    /// the resolved directories. Disabled skill names are passed through verbatim.
    /// </summary>
    public static void ApplySkills(
        SessionConfigBase config,
        string[]? skillDirectories,
        string[]? disabledSkills,
        Func<string, string> resolvePath,
        Action<string> writeVerbose)
    {
        if (skillDirectories is { Length: > 0 })
        {
            var resolved = skillDirectories.Select(resolvePath).ToList();
            config.SkillDirectories = resolved;
            config.EnableSkills = true;
            writeVerbose($"Skills enabled. Skill directories ({resolved.Count}): {string.Join(", ", resolved)}");
        }

        if (disabledSkills is { Length: > 0 })
        {
            config.DisabledSkills = new List<string>(disabledSkills);
            writeVerbose($"Disabled skills ({disabledSkills.Length}): {string.Join(", ", disabledSkills)}");
        }
    }

    public static Task<SessionSetupResult> ConfigureAsync(
        SessionConfig sessionConfig,
        SessionSetupOptions options,
        CancellationToken cancellationToken = default)
    {
        var allAgents = RegisterAgents(sessionConfig, options);

        // Create-time agent: explicit -Agent or a prompt file. A sole custom agent is NOT auto-selected.
        var agentToSelect = ResolveCreateAgent(options);

        ApplyServersToolsAndSkills(sessionConfig, options, agentToSelect);
        LogAgentTools(allAgents, options);

        CustomAgentConfig? selectedAgent = null;
        if (agentToSelect is not null)
        {
            selectedAgent = FindAgent(allAgents, agentToSelect);
            // Create path pre-selects the agent on the config before the session is created.
            sessionConfig.Agent = agentToSelect;
            options.WriteVerbose($"Pre-selecting agent: {agentToSelect}");
        }

        return Task.FromResult(new SessionSetupResult
        {
            AgentToSelect = agentToSelect,
            SelectedAgent = selectedAgent
        });
    }

    /// <summary>
    /// Configures a <see cref="ResumeSessionConfig"/> for resuming a session. Applies the shared
    /// configuration core (custom agents, MCP servers, tool filters, skills) but — unlike
    /// <see cref="ConfigureAsync"/> — does NOT pre-set <see cref="SessionConfigBase.Agent"/>. The
    /// resolved agent name is returned via <see cref="SessionSetupResult.AgentToSelect"/> so the
    /// caller can apply it after resume through <c>session.Rpc.Agent.SelectAsync</c>. An agent is
    /// selected only when explicitly requested via <c>-Agent</c>; a sole loaded custom agent is
    /// never auto-selected.
    /// </summary>
    public static SessionSetupResult ConfigureResume(
        ResumeSessionConfig resumeConfig,
        SessionSetupOptions options)
    {
        var allAgents = RegisterAgents(resumeConfig, options);

        // Resume-time agent: selected only when explicitly requested via -Agent. A sole loaded
        // custom agent is NOT auto-selected.
        var agentToSelect = options.Agent;

        ApplyServersToolsAndSkills(resumeConfig, options, agentToSelect);
        LogAgentTools(allAgents, options);

        // Resume path does NOT pre-set config.Agent — selection is applied post-resume via RPC.
        var selectedAgent = agentToSelect is not null ? FindAgent(allAgents, agentToSelect) : null;

        return new SessionSetupResult
        {
            AgentToSelect = agentToSelect,
            SelectedAgent = selectedAgent
        };
    }

    /// <summary>
    /// Shared core used by both <see cref="ConfigureAsync"/> and <see cref="ConfigureResume"/>:
    /// attaches MCP servers, applies the session-level tool filter, sets excluded tools, and
    /// applies skills onto any <see cref="SessionConfigBase"/>.
    /// </summary>
    private static void ApplyServersToolsAndSkills(
        SessionConfigBase config,
        SessionSetupOptions options,
        string? agentToSelect)
    {
        ApplyMcpServers(config, options);
        ApplyToolFilters(config, options, agentToSelect);

        if (options.ExcludedTools is not null)
            config.ExcludedTools = new List<string>(options.ExcludedTools);

        ApplySkills(config, options.SkillDirectories, options.DisabledSkills, options.ResolvePath, options.WriteVerbose);
    }

    /// <summary>Loads custom agents and registers them on the config. Returns the loaded list (or null).</summary>
    private static List<CustomAgentConfig>? RegisterAgents(SessionConfigBase config, SessionSetupOptions options)
    {
        var allAgents = LoadAgents(options);
        if (allAgents is not null)
        {
            config.CustomAgents = allAgents;
            options.WriteVerbose($"Registered {allAgents.Count} custom agent(s): {string.Join(", ", allAgents.Select(a => a.Name))}");
        }
        return allAgents;
    }

    /// <summary>
    /// Loads MCP servers from <see cref="SessionSetupOptions.McpConfigFile"/> (if given), optionally
    /// wraps local servers via <c>mcp-wrapper</c>, and attaches ALL of them to the session. They are
    /// never filtered out or attached to individual agents — agents scope themselves through their own
    /// Tools list (e.g. "&lt;server&gt;/*").
    /// </summary>
    private static void ApplyMcpServers(SessionConfigBase config, SessionSetupOptions options)
    {
        if (options.McpConfigFile is null)
            return;

        options.WriteVerbose($"Loading MCP config from: {options.McpConfigFile}");
        var mcpConfig = McpConfigLoader.Load(options.ResolvePath(options.McpConfigFile));
        options.WriteVerbose($"Loaded {mcpConfig.Count} MCP server(s): {string.Join(", ", mcpConfig.Keys)}");

        if (!options.NoMcpWrapper)
        {
            var wrapperPath = McpWrapperHelper.ResolveWrapperPath();
            if (wrapperPath is not null)
            {
                mcpConfig = McpWrapperHelper.WrapLocalServers(mcpConfig, wrapperPath);
                options.WriteVerbose($"MCP servers wrapped via: {wrapperPath}");
            }
            else
            {
                options.WriteWarning("mcp-wrapper executable not found — MCP servers will be launched directly (env vars may not propagate).");
            }
        }

        config.McpServers = mcpConfig;
        options.WriteVerbose($"MCP servers attached to session: {string.Join(", ", mcpConfig.Keys)}");
    }

    /// <summary>
    /// Applies the session-level tool filter. The session inherits ALL tools unless one of these is set:
    /// <list type="bullet">
    ///   <item><c>-AvailableTools</c> — an explicit allow-list (passed verbatim, no expansion).</item>
    ///   <item><c>-IsolatedDefaultAgent</c> — when NO agent is selected, restrict the (built-in) default
    ///   agent to the isolated builtin set minus <c>exit_plan_mode</c>/<c>ask_user</c>.</item>
    /// </list>
    /// A session-level allow-list is a hard cap that cascades to the default agent (and any subagents it
    /// spawns), so it is how we make the default agent "isolated".
    /// </summary>
    private static void ApplyToolFilters(SessionConfigBase config, SessionSetupOptions options, string? agentToSelect)
    {
        var sessionTools = new List<string>();
        var restrictTools = false;

        if (options.IsolatedDefaultAgent)
        {
            if (agentToSelect is null)
            {
                restrictTools = true;
                AddTools(sessionTools, IsolatedDefaultAgentTools);
                options.WriteVerbose($"Isolated default agent: restricting session to {IsolatedDefaultAgentTools.Count} builtin tool(s): {string.Join(", ", IsolatedDefaultAgentTools)}");
            }
            else
            {
                options.WriteWarning($"-IsolatedDefaultAgent ignored because an agent was specified ('{agentToSelect}').");
            }
        }

        if (options.AvailableTools is not null)
        {
            restrictTools = true;
            AddTools(sessionTools, options.AvailableTools);
        }

        if (restrictTools)
        {
            config.AvailableTools = sessionTools;
            options.WriteVerbose($"Session tool list ({sessionTools.Count} tools): {string.Join(", ", sessionTools.Order())}");
        }
    }

    private static void LogAgentTools(IReadOnlyList<CustomAgentConfig>? allAgents, SessionSetupOptions options)
    {
        if (allAgents is null)
            return;

        foreach (var agent in allAgents)
        {
            if (agent.Tools is not { Count: > 0 })
                options.WriteVerbose($"Agent '{agent.Name}' tools: <all session tools>");
            else
                options.WriteVerbose($"Agent '{agent.Name}' tools ({agent.Tools.Count}): {string.Join(", ", agent.Tools.Order())}");
        }
    }

    private static CustomAgentConfig? FindAgent(IReadOnlyList<CustomAgentConfig>? agents, string name) =>
        agents?.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Resolves the agent to select when <b>creating</b> a session: explicit
    /// <c>-Agent</c> takes precedence, then a prompt-file agent. A sole custom agent is NOT
    /// auto-selected.</summary>
    private static string? ResolveCreateAgent(SessionSetupOptions options)
    {
        if (options.AgentWasSpecified && options.Agent is not null)
            return options.Agent;

        if (options.PromptFileAgent is not null)
        {
            options.WriteVerbose($"Using agent from prompt file: {options.PromptFileAgent}");
            return options.PromptFileAgent;
        }

        return null;
    }
}
