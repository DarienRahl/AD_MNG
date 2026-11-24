using System.Globalization;
using System.Management.Automation;
using AdMng.PowerShell.Hosting;
using AdMng.PowerShell.Reading;

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

    /// <summary>
    /// The window's services, or null when the session has none (a command that needs them calls <see cref="RequireContext"/>). Found in
    /// <see cref="WindowRegistry"/> by the runspace the command runs in - not in anything a script can read.
    /// </summary>
    protected IAdmWindowContext? ContextOrNull => WindowRegistry.Current()?.Context;

    /// <summary>The window's services.</summary>
    /// <exception cref="PSInvalidOperationException">The command runs outside an AD Manager window.</exception>
    protected IAdmWindowContext RequireContext() =>
        ContextOrNull ?? throw new PSInvalidOperationException(Text(AdmText.Keys.ContextMissing));

    /// <summary>The language of the window: the culture of the session's host (the culture of the process outside a host).</summary>
    private protected CultureInfo UiCulture => Host?.CurrentUICulture ?? CultureInfo.CurrentUICulture;

    /// <summary>A text of the commands in the window's language (<see cref="AdmText"/>, audit ps-commands-8).</summary>
    private protected string Text(string key, params object?[] args) => AdmText.Format(UiCulture, key, args);

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

    /// <summary>Waits for <paramref name="task"/> on the pipeline thread; a stop turns into <see cref="PipelineStoppedException"/>.</summary>
    protected void Wait(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            task.GetAwaiter().GetResult();
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
