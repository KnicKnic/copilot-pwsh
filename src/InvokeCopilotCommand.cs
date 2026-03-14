using System.Management.Automation;
using GitHub.Copilot.SDK;

namespace CopilotShell;

/// <summary>
/// One-shot convenience cmdlet: creates a client + session, sends a prompt,
/// collects or streams the response, and cleans up. Ideal for scripting.
/// </summary>
/// <example>
/// <code>Invoke-Copilot "What is 2+2?"</code>
/// <code>Invoke-Copilot "Explain this code" -Model claude-sonnet-4.5 -Stream</code>
/// <code>Invoke-Copilot "You are a pirate" -SystemMessage "Respond like a pirate." -SystemMessageMode Replace</code>
/// <code>Invoke-Copilot "Refactor this" -TimeoutSeconds 120 -MaxTurns 5</code>
/// <code>Invoke-Copilot "Help me" -Agent my-custom-agent</code>
/// <code>
/// # Define a custom agent and use it in one shot
/// $agent = [GitHub.Copilot.SDK.CustomAgentConfig]@{ Name = 'reviewer'; Prompt = 'You are a code reviewer.' }
/// Invoke-Copilot "Review this PR" -CustomAgents $agent -Agent reviewer
/// </code>
/// <code>
/// # Load a custom agent from a .agent.md file
/// Invoke-Copilot "Check the ADO pipeline" -CustomAgentFile .github\agents\ado-team.agent.md
/// </code>
/// <code>
/// # Use a VS Code-compatible .prompt.md file
/// Invoke-Copilot -PromptFile .github\prompts\get-work-items.prompt.md
/// </code>
/// <code>
/// # Use a prompt file but override the agent
/// Invoke-Copilot -PromptFile .github\prompts\get-work-items.prompt.md -Agent different-agent
/// </code>
/// </example>
[Cmdlet(VerbsLifecycle.Invoke, "Copilot")]
[OutputType(typeof(string), typeof(SessionEvent))]
public sealed class InvokeCopilotCommand : AsyncPSCmdlet
{
    [Parameter(Position = 0,
        HelpMessage = "The prompt to send. Required unless -PromptFile is specified.")]
    public string? Prompt { get; set; }

    [Parameter(HelpMessage = "Model to use (e.g. gpt-5, claude-sonnet-4.5).")]
    public string? Model { get; set; }

    [Parameter(HelpMessage = "System message content.")]
    public string? SystemMessage { get; set; }

    [Parameter(HelpMessage = "System message mode: Append or Replace.")]
    public SystemMessageMode SystemMessageMode { get; set; } = SystemMessageMode.Append;

    [Parameter(HelpMessage = "Stream events to the pipeline.")]
    public SwitchParameter Stream { get; set; }

    [Parameter(HelpMessage = "Timeout in seconds. 0 = no timeout.")]
    public int TimeoutSeconds { get; set; } = 0;

    [Parameter(HelpMessage = "Maximum number of assistant turns (tool-call round-trips).")]
    public int MaxTurns { get; set; } = 0;

    [Parameter(HelpMessage = "File paths to attach.")]
    public string[]? Attachment { get; set; }

    [Parameter(HelpMessage = "Path to the Copilot CLI executable.")]
    public string? CliPath { get; set; }

    [Parameter(HelpMessage = "GitHub token for authentication.")]
    public string? GitHubToken { get; set; }

    [Parameter(HelpMessage = "List of tool names to allow.")]
    public string[]? AvailableTools { get; set; }

    [Parameter(HelpMessage = "List of tool names to exclude.")]
    public string[]? ExcludedTools { get; set; }

    [Parameter(HelpMessage = "Path to an MCP config JSON file (e.g. mcp-config.json) that defines MCP servers to attach to this session.")]
    public string? McpConfigFile { get; set; }

    [Parameter(HelpMessage = "Disable the MCP wrapper that fixes environment variable propagation. By default, local MCP servers are launched through mcp-wrapper to ensure env vars are set correctly.")]
    public SwitchParameter NoMcpWrapper { get; set; }

    [Parameter(HelpMessage = "Name of a custom agent to select for this session (e.g. 'my-agent').")]
    public string? Agent { get; set; }

    [Parameter(HelpMessage = "One or more CustomAgentConfig objects to register with the session. Use [GitHub.Copilot.SDK.CustomAgentConfig]@{ Name='...'; Prompt='...' } to create them.")]
    public CustomAgentConfig[]? CustomAgents { get; set; }

    [Parameter(HelpMessage = "Path(s) to .agent.md files that define custom agents. The agent name is derived from the filename (e.g. 'ado-team.agent.md' → 'ado-team').")]
    [Alias("AgentFile")]
    public string[]? CustomAgentFile { get; set; }

    [Parameter(HelpMessage = "Path to a .prompt.md file (VS Code compatible). Contains frontmatter with optional 'agent' and 'description' fields, and a body used as the prompt text. Explicit -Prompt and -Agent override values from the file.")]
    public string? PromptFile { get; set; }

    private CancellationTokenSource? _cts;

    protected override void StopProcessing()
    {
        // Called when user presses Ctrl+C
        _cts?.Cancel();
        base.StopProcessing();
    }

    protected override async Task ProcessRecordAsync()
    {
        // Create cancellation token for Ctrl+C support
        _cts = new CancellationTokenSource();
        var cancellationToken = _cts.Token;

        try
        {
            await ProcessInternalAsync(cancellationToken);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task ProcessInternalAsync(CancellationToken cancellationToken)
    {
        // --- Parse prompt file if specified ---
        PromptFileResult? promptFileResult = null;
        if (PromptFile is not null)
        {
            var resolvedPath = ResolvePSPath(PromptFile);
            promptFileResult = PromptFileParser.Parse(resolvedPath);
            WriteVerbose($"Loaded prompt file: {Path.GetFileName(resolvedPath)}");
            if (promptFileResult.Agent is not null)
                WriteVerbose($"  Agent from prompt file: {promptFileResult.Agent}");
            if (promptFileResult.Description is not null)
                WriteVerbose($"  Description: {promptFileResult.Description}");
        }

        // Resolve effective prompt: explicit -Prompt overrides prompt file body
        var effectivePrompt = Prompt ?? promptFileResult?.Prompt;
        if (string.IsNullOrEmpty(effectivePrompt))
        {
            ThrowTerminatingError(new ErrorRecord(
                new PSArgumentException("Either -Prompt or -PromptFile (with prompt body) must be specified."),
                "MissingPrompt", ErrorCategory.InvalidArgument, null));
            return;
        }

        // --- Client ---
        var clientOpts = new CopilotClientOptions();
        if (CliPath is not null)
            clientOpts.CliPath = CliPath;
        else
        {
            var resolved = await CliPathResolver.ResolveOrDownloadAsync(
                msg => WriteVerbose(msg), cancellationToken);
            if (resolved is not null) clientOpts.CliPath = resolved;
        }
        if (GitHubToken is not null) clientOpts.GitHubToken = GitHubToken;

        await using var client = new CopilotClient(clientOpts);
        await client.StartAsync();

        // --- Session ---
        var sessionConfig = new SessionConfig
        {
            Streaming = Stream.IsPresent
        };

        if (Model is not null) sessionConfig.Model = Model;

        if (SystemMessage is not null)
        {
            sessionConfig.SystemMessage = new SystemMessageConfig
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
            sessionConfig.CustomAgents = allAgents;
            WriteVerbose($"Registered {allAgents.Count} custom agent(s): {string.Join(", ", allAgents.Select(a => a.Name))}");
        }

        // Load MCP config (needed for both McpServers and tool discovery)
        Dictionary<string, object>? mcpConfig = null;

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
                    WriteWarning("mcp-wrapper executable not found — MCP servers will be launched directly (env vars may not propagate).");
                }
            }

            sessionConfig.McpServers = filtered;
        }

        // Discover MCP tools dynamically for servers referenced by -AvailableTools or agent tool patterns
        Dictionary<string, List<string>>? discoveredTools = null;
        if (mcpConfig is not null)
        {
            var serversToDiscover = ToolFilterHelper.GetServersNeedingDiscovery(effectiveToolFilter, mcpConfig);
            if (serversToDiscover.Count > 0)
            {
                WriteVerbose($"Discovering tools from MCP servers: {string.Join(", ", serversToDiscover)}");
                discoveredTools = await McpToolDiscovery.DiscoverToolsForServersAsync(
                    mcpConfig, serversToDiscover, ct: cancellationToken);
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

        // Expand wildcards/server names into exact tool names for session-level scoping.
        // When agent MCP refs exist but no explicit -AvailableTools, use agent refs as the
        // session AvailableTools so the model only sees core tools + referenced MCP tools.
        var sessionToolSource = AvailableTools ?? agentMcpRefs;
        var mergedTools = ToolFilterHelper.ExpandToolPatterns(sessionToolSource, discoveredTools);
        if (mergedTools is not null)
        {
            sessionConfig.AvailableTools = mergedTools;
            WriteVerbose($"Session tool list ({mergedTools.Count} tools): {string.Join(", ", mergedTools.Order())}");
        }
        if (ExcludedTools is not null) sessionConfig.ExcludedTools = new List<string>(ExcludedTools);

        // Process agent tool lists: clear MCP-referencing Tools so agent inherits
        // session tools (CLI applies agent.Tools filter before MCP servers connect)
        ToolFilterHelper.ExpandAgentTools(allAgents, discoveredTools);
        if (allAgents != null)
        {
            foreach (var agent in allAgents)
            {
                if (agent.Tools == null)
                    WriteVerbose($"Agent '{agent.Name}' tools: <session-scoped> (MCP references detected)");
                else if (agent.Tools.Count > 0)
                    WriteVerbose($"Agent '{agent.Name}' tools ({agent.Tools.Count}): {string.Join(", ", agent.Tools.Order())}");
            }
        }

        // Auto-approve tool permission requests using the SDK's built-in handler
        sessionConfig.OnPermissionRequest = PermissionHandler.ApproveAll;

        // Pre-select agent at session creation (SDK 0.1.33+)
        // Priority: explicit -Agent > prompt file agent > auto-select single custom agent
        var agentToSelect = MyInvocation.BoundParameters.ContainsKey(nameof(Agent)) ? Agent : null;
        if (agentToSelect is null && promptFileResult?.Agent is not null)
        {
            agentToSelect = promptFileResult.Agent;
            WriteVerbose($"Using agent from prompt file: {agentToSelect}");
        }
        if (agentToSelect is null && sessionConfig.CustomAgents?.Count == 1)
        {
            agentToSelect = sessionConfig.CustomAgents[0].Name;
            WriteVerbose($"Auto-selecting sole custom agent: {agentToSelect}");
        }
        if (agentToSelect is not null)
        {
            sessionConfig.Agent = agentToSelect;
            WriteVerbose($"Pre-selecting agent: {agentToSelect}");
        }

        await using var session = await client.CreateSessionAsync(sessionConfig);

        // --- Message options ---
        var msgOpts = new MessageOptions { Prompt = effectivePrompt };

        if (Attachment is not null)
        {
            var attachments = new List<UserMessageDataAttachmentsItem>();
            foreach (var path in Attachment)
            {
                attachments.Add(new UserMessageDataAttachmentsItemFile
                {
                    Path = path,
                    DisplayName = System.IO.Path.GetFileName(path)
                });
            }
            msgOpts.Attachments = attachments;
        }

        // --- Send & collect ---
        var done = new TaskCompletionSource();
        string? lastAssistantContent = null;
        int turnCount = 0;
        
        // Capture the SynchronizationContext to marshal WriteObject calls back to the pipeline thread
        var syncContext = SynchronizationContext.Current;

        using var sub = session.On(evt =>
        {
            if (Stream.IsPresent)
            {
                // Marshal WriteObject back to the pipeline thread
                if (syncContext is not null)
                {
                    syncContext.Post(_ => WriteObject(evt), null);
                }
                else
                {
                    WriteObject(evt);
                }
            }

            switch (evt)
            {
                case AssistantMessageEvent msg:
                    lastAssistantContent = msg.Data.Content;
                    turnCount++;
                    if (MaxTurns > 0 && turnCount >= MaxTurns)
                    {
                        // Fire-and-forget abort; idle event will resolve done
                        _ = session.AbortAsync(cancellationToken);
                    }
                    break;

                case SessionIdleEvent:
                    done.TrySetResult();
                    break;

                case SessionErrorEvent err:
                    done.TrySetException(new Exception(err.Data.Message));
                    break;
            }
        });

        // Register cancellation to complete the done task
        using var registration = cancellationToken.Register(() =>
        {
            done.TrySetCanceled(cancellationToken);
        });

        await session.SendAsync(msgOpts, cancellationToken);

        if (TimeoutSeconds > 0)
        {
            var completed = await Task.WhenAny(
                done.Task,
                Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), cancellationToken));

            if (completed != done.Task)
            {
                await session.AbortAsync(cancellationToken);
                WriteWarning($"Timed out after {TimeoutSeconds}s — session aborted.");
            }
            else
            {
                await done.Task; // propagate exceptions
            }
        }
        else
        {
            await done.Task;
        }

        if (!Stream.IsPresent && lastAssistantContent is not null)
        {
            WriteObject(lastAssistantContent);
        }
    }
}
