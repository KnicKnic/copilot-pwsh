using System.Management.Automation;
using GitHub.Copilot.SDK;

namespace CopilotShell;

/// <summary>
/// Send a message to a Copilot session.
/// With -Stream, emits session events to the pipeline in real time.
/// Without -Stream, waits for the session to become idle and returns the
/// final assistant message text.
/// </summary>
/// <example>
/// <code>Send-CopilotMessage -Session $session -Prompt "Hello"</code>
/// <code>Send-CopilotMessage $session "Explain this code" -Stream</code>
/// <code>"Fix the bug" | Send-CopilotMessage -Session $session -Attachment ./file.cs</code>
/// <code>Send-CopilotMessage $session "Use this agent" -Agent my-agent</code>
/// <code>
/// # Use a VS Code-compatible .prompt.md file
/// Send-CopilotMessage $session -PromptFile .github\prompts\get-work-items.prompt.md
/// </code>
/// </example>
[Cmdlet(VerbsCommunications.Send, "CopilotMessage")]
[OutputType(typeof(string), typeof(SessionEvent))]
public sealed class SendCopilotMessageCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
        HelpMessage = "The CopilotSession to send to.")]
    public CopilotSession Session { get; set; } = null!;

    [Parameter(Position = 1,
        HelpMessage = "The prompt/message to send. Required unless -PromptFile is specified.")]
    public string? Prompt { get; set; }

    [Parameter(HelpMessage = "Path to a .prompt.md file (VS Code compatible). Contains frontmatter with optional 'agent' and 'description' fields, and a body used as the prompt text. Explicit -Prompt and -Agent override values from the file.")]
    public string? PromptFile { get; set; }

    [Parameter(HelpMessage = "File paths to attach.")]
    [Alias("Attachments")]
    public string[]? Attachment { get; set; }

    [Parameter(HelpMessage = "Stream session events to the pipeline in real time.")]
    public SwitchParameter Stream { get; set; }

    [Parameter(HelpMessage = "Timeout in seconds. 0 = no timeout.")]
    public int TimeoutSeconds { get; set; } = 0;

    [Parameter(HelpMessage = "Maximum number of assistant turns (tool-call round-trips). 0 = unlimited.")]
    public int MaxTurns { get; set; } = 0;

    [Parameter(HelpMessage = "Name of a custom agent to select before sending the message. Pass $null or empty string to deselect the current agent.")]
    public string? Agent { get; set; }

    // Track cancellation tokens per session to cancel zombie operations from Ctrl+C
    private static readonly Dictionary<string, CancellationTokenSource> _activeCancellations = new();
    private CancellationTokenSource? _currentCts;

    protected override void StopProcessing()
    {
        // Called when user presses Ctrl+C
        _currentCts?.Cancel();
        base.StopProcessing();
    }

    protected override async Task ProcessRecordAsync()
    {
        // Cancel any zombie operation from a previous Ctrl+C
        lock (_activeCancellations)
        {
            if (_activeCancellations.TryGetValue(Session.SessionId, out var oldCts))
            {
                try
                {
                    oldCts.Cancel();
                    oldCts.Dispose();
                }
                catch { }
                _activeCancellations.Remove(Session.SessionId);
            }
        }

        // Create a new cancellation token for this operation
        var cts = new CancellationTokenSource();
        _currentCts = cts;
        lock (_activeCancellations)
        {
            _activeCancellations[Session.SessionId] = cts;
        }

        try
        {
            await ProcessRecordInternalAsync(cts.Token);
        }
        finally
        {
            _currentCts = null;
            lock (_activeCancellations)
            {
                _activeCancellations.Remove(Session.SessionId);
            }
            cts.Dispose();
        }
    }

    private async Task ProcessRecordInternalAsync(CancellationToken cancellationToken)
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

        // Switch agent: explicit -Agent > prompt file agent
        if (!MyInvocation.BoundParameters.ContainsKey(nameof(Agent)) && promptFileResult?.Agent is not null)
        {
            WriteVerbose($"Selecting agent from prompt file: {promptFileResult.Agent}");
            await Session.Rpc.Agent.SelectAsync(promptFileResult.Agent, cancellationToken);
        }
        else if (MyInvocation.BoundParameters.ContainsKey(nameof(Agent)))
        {
            if (string.IsNullOrEmpty(Agent))
            {
                WriteVerbose("Deselecting current agent.");
                await Session.Rpc.Agent.DeselectAsync(cancellationToken);
            }
            else
            {
                WriteVerbose($"Selecting agent: {Agent}");
                await Session.Rpc.Agent.SelectAsync(Agent, cancellationToken);
            }
        }

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

        if (Stream.IsPresent)
        {
            // For streaming, use manual subscription
            var done = new TaskCompletionSource();
            int turnCount = 0;
            
            // Capture the SynchronizationContext to marshal WriteObject calls back to the pipeline thread
            var syncContext = SynchronizationContext.Current;

            IDisposable? sub = null;
            
            // Register cancellation to complete the done task
            using var registration = cancellationToken.Register(() =>
            {
                done.TrySetCanceled(cancellationToken);
                sub?.Dispose();
            });
            
            sub = Session.On(evt =>
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

                if (evt is AssistantMessageEvent msg)
                {
                    turnCount++;
                    if (MaxTurns > 0 && turnCount >= MaxTurns)
                    {
                        _ = Session.AbortAsync(cancellationToken);
                    }
                }
                else if (evt is SessionIdleEvent)
                {
                    done.TrySetResult();
                }
                else if (evt is SessionErrorEvent err)
                {
                    done.TrySetException(new Exception(err.Data.Message));
                }
            });

            try
            {
                await Session.SendAsync(msgOpts, cancellationToken);
                
                if (TimeoutSeconds > 0)
                {
                    var completed = await Task.WhenAny(
                        done.Task,
                        Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), cancellationToken));
                    if (completed != done.Task)
                    {
                        await Session.AbortAsync(cancellationToken);
                        WriteWarning($"Timed out after {TimeoutSeconds}s — session aborted.");
                    }
                }
                else
                {
                    await done.Task;
                }
            }
            catch (OperationCanceledException)
            {
                // Operation was cancelled
                try { await Session.AbortAsync(CancellationToken.None); } catch { }
                throw;
            }
            finally
            {
                sub?.Dispose();
            }
        }
        else
        {
            // For non-streaming, use SendAndWaitAsync which handles lifecycle better
            if (MaxTurns > 0)
            {
                // With MaxTurns, we need event-based tracking instead of SendAndWaitAsync
                var done = new TaskCompletionSource();
                int turnCount = 0;
                string? lastAssistantContent = null;

                using var registration = cancellationToken.Register(() =>
                {
                    done.TrySetCanceled(cancellationToken);
                });

                using var sub = Session.On(evt =>
                {
                    switch (evt)
                    {
                        case AssistantMessageEvent msg:
                            lastAssistantContent = msg.Data.Content;
                            turnCount++;
                            if (MaxTurns > 0 && turnCount >= MaxTurns)
                            {
                                _ = Session.AbortAsync(cancellationToken);
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

                try
                {
                    await Session.SendAsync(msgOpts, cancellationToken);

                    if (TimeoutSeconds > 0)
                    {
                        var completed = await Task.WhenAny(
                            done.Task,
                            Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), cancellationToken));
                        if (completed != done.Task)
                        {
                            await Session.AbortAsync(cancellationToken);
                            WriteWarning($"Timed out after {TimeoutSeconds}s — session aborted.");
                        }
                        else
                        {
                            await done.Task;
                        }
                    }
                    else
                    {
                        await done.Task;
                    }

                    if (lastAssistantContent is not null)
                    {
                        WriteObject(lastAssistantContent);
                    }
                }
                catch (OperationCanceledException)
                {
                    try { await Session.AbortAsync(CancellationToken.None); } catch { }
                    throw;
                }
            }
            else
            {
                var timeout = TimeoutSeconds > 0 ? TimeSpan.FromSeconds(TimeoutSeconds) : (TimeSpan?)null;

                try
                {
                    var result = await Session.SendAndWaitAsync(msgOpts, timeout, cancellationToken);

                    if (result?.Data?.Content is not null)
                    {
                        WriteObject(result.Data.Content);
                    }
                }
                catch (OperationCanceledException)
                {
                    try { await Session.AbortAsync(CancellationToken.None); } catch { }
                    throw;
                }
            }
        }
    }
}

/// <summary>
/// Wait for a Copilot session to become idle.
/// </summary>
[Cmdlet(VerbsLifecycle.Wait, "CopilotSession")]
public sealed class WaitCopilotSessionCommand : AsyncPSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true,
        HelpMessage = "The CopilotSession to wait on.")]
    public CopilotSession Session { get; set; } = null!;

    [Parameter(HelpMessage = "Timeout in seconds. 0 = no timeout.")]
    public int TimeoutSeconds { get; set; } = 0;

    protected override async Task ProcessRecordAsync()
    {
        var done = new TaskCompletionSource();

        using var sub = Session.On(evt =>
        {
            if (evt is SessionIdleEvent)
                done.TrySetResult();
            else if (evt is SessionErrorEvent err)
                done.TrySetException(new Exception(err.Data.Message));
        });

        if (TimeoutSeconds > 0)
        {
            var completed = await Task.WhenAny(
                done.Task,
                Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds)));

            if (completed != done.Task)
            {
                WriteWarning($"Timed out after {TimeoutSeconds}s waiting for session idle.");
            }
        }
        else
        {
            await done.Task;
        }
    }
}
