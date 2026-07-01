using System.Management.Automation;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitHub.Copilot;

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
/// $agent = [GitHub.Copilot.CustomAgentConfig]@{ Name = 'reviewer'; Prompt = 'You are a code reviewer.' }
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

    [Parameter(HelpMessage = "Reasoning effort: low, medium, high, xhigh. The model must support reasoning effort; otherwise session creation fails.")]
    [ArgumentCompletions("low", "medium", "high", "xhigh")]
    public string? ReasoningEffort { get; set; }

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

    [Parameter(HelpMessage = "When no agent is specified, restrict the (built-in) default agent to an isolated builtin tool set: BuiltInTools.Isolated minus exit_plan_mode and ask_user (send_inbox and context_board are kept). Ignored if an agent is specified.")]
    public SwitchParameter IsolatedDefaultAgent { get; set; }

    [Parameter(HelpMessage = "List of tool names to exclude.")]
    public string[]? ExcludedTools { get; set; }

    [Parameter(HelpMessage = "One or more directories to discover skills from. Passing any directory enables skills for the session.")]
    [Alias("SkillDirectories")]
    public string[]? SkillDirectory { get; set; }

    [Parameter(HelpMessage = "Names of skills to disable for the session.")]
    [Alias("DisabledSkills")]
    public string[]? DisabledSkill { get; set; }

    [Parameter(HelpMessage = "Path to an MCP config JSON file (e.g. mcp-config.json) that defines MCP servers to attach to this session.")]
    public string? McpConfigFile { get; set; }

    [Parameter(HelpMessage = "Disable the MCP wrapper that fixes environment variable propagation. By default, local MCP servers are launched through mcp-wrapper to ensure env vars are set correctly.")]
    public SwitchParameter NoMcpWrapper { get; set; }

    [Parameter(HelpMessage = "Name of a custom agent to select for this session (e.g. 'my-agent').")]
    public string? Agent { get; set; }

    [Parameter(HelpMessage = "One or more CustomAgentConfig objects to register with the session. Use [GitHub.Copilot.CustomAgentConfig]@{ Name='...'; Prompt='...' } to create them.")]
    public CustomAgentConfig[]? CustomAgents { get; set; }

    [Parameter(HelpMessage = "Path(s) to .agent.md files that define custom agents. The agent name is derived from the filename (e.g. 'ado-team.agent.md' → 'ado-team').")]
    [Alias("AgentFile")]
    public string[]? CustomAgentFile { get; set; }

    [Parameter(HelpMessage = "Path to a .prompt.md file (VS Code compatible). Contains frontmatter with optional 'agent' and 'description' fields, and a body used as the prompt text. Explicit -Prompt and -Agent override values from the file.")]
    public string? PromptFile { get; set; }

    [Parameter(HelpMessage = "Text to prepend to the prompt. Inserted before the -Prompt or -PromptFile body (separated by a newline). If no other prompt is provided, this becomes the entire prompt.")]
    public string? PrependPrompt { get; set; }

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

        // Prepend text if provided (or use as the entire prompt when nothing else is given)
        if (!string.IsNullOrEmpty(PrependPrompt))
        {
            effectivePrompt = string.IsNullOrEmpty(effectivePrompt)
                ? PrependPrompt
                : PrependPrompt + "\n" + effectivePrompt;
        }

        if (string.IsNullOrEmpty(effectivePrompt))
        {
            ThrowTerminatingError(new ErrorRecord(
                new PSArgumentException("Either -Prompt, -PromptFile (with prompt body), or -PrependPrompt must be specified."),
                "MissingPrompt", ErrorCategory.InvalidArgument, null));
            return;
        }

        // --- Client ---
        var clientOpts = new CopilotClientOptions();
        string? cliPath = CliPath;
        if (cliPath is null)
        {
            var resolved = await CliPathResolver.ResolveOrDownloadAsync(
                msg => WriteVerbose(msg), cancellationToken);
            if (resolved is not null) cliPath = resolved;
        }
        clientOpts.Connection = RuntimeConnection.ForStdio(cliPath!);
        if (GitHubToken is not null) clientOpts.GitHubToken = GitHubToken;

        await using var client = new CopilotClient(clientOpts);
        await client.StartAsync();

        // --- Session ---
        var sessionConfig = new SessionConfig
        {
            Streaming = Stream.IsPresent
        };

        if (Model is not null) sessionConfig.Model = Model;
        if (ReasoningEffort is not null) sessionConfig.ReasoningEffort = ReasoningEffort;

        if (SystemMessage is not null)
        {
            sessionConfig.SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode,
                Content = SystemMessage
            };
        }

        // Auto-approve tool permission requests using the SDK's built-in handler
        sessionConfig.OnPermissionRequest = PermissionHandler.ApproveAll;

        var setupResult = await SessionSetupHelper.ConfigureAsync(sessionConfig, new SessionSetupOptions
        {
            CustomAgents = CustomAgents,
            CustomAgentFiles = CustomAgentFile,
            AvailableTools = AvailableTools,
            IsolatedDefaultAgent = IsolatedDefaultAgent.IsPresent,
            ExcludedTools = ExcludedTools,
            SkillDirectories = SkillDirectory,
            DisabledSkills = DisabledSkill,
            McpConfigFile = McpConfigFile,
            NoMcpWrapper = NoMcpWrapper.IsPresent,
            Agent = Agent,
            AgentWasSpecified = MyInvocation.BoundParameters.ContainsKey(nameof(Agent)),
            PromptFileAgent = promptFileResult?.Agent,
            ResolvePath = ResolvePSPath,
            WriteVerbose = WriteVerbose,
            WriteWarning = WriteWarning
        }, cancellationToken);

        WriteVerbose($"Session config JSON:{Environment.NewLine}{ToJson(sessionConfig)}");

        await using var session = await client.CreateSessionAsync(sessionConfig);

        try
        {
            SessionDataStore.WriteCreationDefaults(sessionConfig.ConfigDirectory, session.SessionId);
            WriteVerbose("Session sidecar created with creation defaults.");
        }
        catch (Exception ex)
        {
            WriteWarning($"Failed to write session sidecar: {ex.Message}");
        }

        // --- Message options ---
        var msgOpts = new MessageOptions { Prompt = effectivePrompt };

        if (Attachment is not null)
        {
            var attachments = new List<Attachment>();
            foreach (var path in Attachment)
            {
                attachments.Add(new AttachmentFile
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

        using var sub = session.On<SessionEvent>(evt =>
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

    private static readonly JsonSerializerOptions s_verboseJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    private static string ToJson(SessionConfig config)
    {
        try
        {
            return JsonSerializer.Serialize(config, s_verboseJson);
        }
        catch (Exception ex)
        {
            return $"<unable to serialize session config: {ex.Message}>";
        }
    }
}
