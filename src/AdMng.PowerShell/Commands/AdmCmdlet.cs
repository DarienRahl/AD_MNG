using System.Management.Automation;
using AdMng.PowerShell.Hosting;

namespace AdMng.PowerShell.Commands;

/// <summary>
/// Base of AD Manager's commands: gives them the window's services and a cancellation token that Stop cancels, and the two
/// ways to wait for async work on the pipeline thread (never the UI thread). A command is a plain class; tests run it through
/// a <see cref="PowerShellSession"/> over a fake directory, which is the path the window uses.
/// </summary>
public abstract class AdmCmdlet : PSCmdlet
{
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Cancelled by <see cref="StopProcessing"/>: pass it to every directory call (CLAUDE.md rule 6).</summary>
    protected CancellationToken StopToken => _stop.Token;

    /// <summary>The window's services, or null when the session has none (a command that needs them calls <see cref="RequireContext"/>).</summary>
    protected IAdmWindowContext? ContextOrNull => (Host?.PrivateData?.BaseObject as WindowPrivateData)?.Context;

    /// <summary>The window's services.</summary>
    /// <exception cref="PSInvalidOperationException">The command runs outside an AD Manager window.</exception>
    protected IAdmWindowContext RequireContext() =>
        ContextOrNull ?? throw new PSInvalidOperationException("This command works only in an AD Manager PowerShell window that is connected to a domain.");

    protected override void StopProcessing() => _stop.Cancel();

    /// <summary>Waits for <paramref name="task"/> on the pipeline thread; a stop turns into <see cref="PipelineStoppedException"/>.</summary>
    protected T Wait<T>(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            throw new PipelineStoppedException();
        }
    }

    /// <summary>
    /// Pulls <paramref name="source"/> on the pipeline thread and writes each mapped item as it arrives (streaming: nothing
    /// is collected). A stop ends the pull and the pipeline.
    /// </summary>
    protected void WriteAll<T>(IAsyncEnumerable<T> source, Func<T, object?> map)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);
        var enumerator = source.GetAsyncEnumerator(_stop.Token);
        try
        {
            while (Wait(enumerator.MoveNextAsync().AsTask()))
            {
                if (map(enumerator.Current) is { } item)
                {
                    WriteObject(item, enumerateCollection: false);
                }
            }
        }
        finally
        {
            try
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // The pull was cancelled by Stop; nothing to report.
            }
        }
    }
}
