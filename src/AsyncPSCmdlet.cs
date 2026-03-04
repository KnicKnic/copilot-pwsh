using System.Collections.Concurrent;
using System.Management.Automation;

namespace CopilotShell;

/// <summary>
/// Base cmdlet that bridges async/await into the PowerShell pipeline by
/// pumping a single-threaded <see cref="SynchronizationContext"/> on the
/// pipeline thread. This ensures that <c>WriteObject</c>, <c>WriteError</c>,
/// etc. are always called from the correct thread.
/// </summary>
public abstract class AsyncPSCmdlet : PSCmdlet
{
    /// <summary>
    /// Resolve a user-supplied path (relative or absolute) against PowerShell's
    /// current working directory ($PWD) instead of .NET's Environment.CurrentDirectory.
    /// </summary>
    protected string ResolvePSPath(string path)
        => SessionState.Path.GetUnresolvedProviderPathFromPSPath(path);

    protected sealed override void ProcessRecord()
    {
        RunWithPump(() => ProcessRecordAsync());
    }

    protected sealed override void EndProcessing()
    {
        RunWithPump(() => EndProcessingAsync());
    }

    /// <summary>Override this to implement async pipeline processing.</summary>
    protected virtual Task ProcessRecordAsync() => Task.CompletedTask;

    /// <summary>Override this to implement async end processing.</summary>
    protected virtual Task EndProcessingAsync() => Task.CompletedTask;

    /// <summary>
    /// Runs the async factory on a thread-pool thread while pumping
    /// continuations back onto the current (pipeline) thread.
    /// </summary>
    private void RunWithPump(Func<Task> asyncWork)
    {
        using var queue = new BlockingCollection<(SendOrPostCallback Callback, object? State)>();
        var prevCtx = SynchronizationContext.Current;
        var pumpCtx = new PipelineSyncContext(queue);
        SynchronizationContext.SetSynchronizationContext(pumpCtx);

        try
        {
            // Start the async work — its continuations will Post back to our queue.
            var task = asyncWork();

            // Pump the queue on the pipeline thread until the task completes.
            while (!task.IsCompleted)
            {
                if (queue.TryTake(out var item, millisecondsTimeout: 50))
                {
                    item.Callback(item.State);
                }
            }

            // Drain any remaining callbacks.
            while (queue.TryTake(out var item))
            {
                item.Callback(item.State);
            }

            // Propagate exceptions.
            task.GetAwaiter().GetResult();
        }
        catch (AggregateException ae) when (ae.InnerException is not null)
        {
            ThrowTerminatingError(new ErrorRecord(
                ae.InnerException,
                ae.InnerException.GetType().Name,
                ErrorCategory.NotSpecified,
                null));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prevCtx);
        }
    }

    /// <summary>
    /// A <see cref="SynchronizationContext"/> that posts callbacks to a
    /// <see cref="BlockingCollection{T}"/> so they execute on the pipeline thread.
    /// </summary>
    private sealed class PipelineSyncContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue;

        public PipelineSyncContext(BlockingCollection<(SendOrPostCallback, object?)> queue)
            => _queue = queue;

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // Queue has been marked as complete (disposed) - ignore the post
                // This happens when a previous operation is cancelled after the pump exits
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            // Send is synchronous — just execute inline if we're already on the
            // pipeline thread, otherwise marshal through the queue.
            // For safety we always go through the queue and block.
            using var done = new ManualResetEventSlim(false);
            Exception? caught = null;
            _queue.Add(((s) =>
            {
                try { d(s); }
                catch (Exception ex) { caught = ex; }
                finally { done.Set(); }
            }, state));
            done.Wait();
            if (caught is not null) throw caught;
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
