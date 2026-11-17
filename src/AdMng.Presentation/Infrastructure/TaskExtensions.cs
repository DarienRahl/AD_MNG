namespace AdMng.Presentation.Infrastructure;

/// <summary>Helpers for fire-and-forget work started from property change handlers and events.</summary>
public static class TaskExtensions
{
    /// <summary>
    /// Lets a task run without awaiting it while still observing its exception (the work is expected to handle
    /// and surface its own errors; this only prevents unobserved task exceptions).
    /// </summary>
    public static void Forget(this Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Waits for <paramref name="task"/> until <paramref name="cancellationToken"/> is cancelled, like
    /// <see cref="Task{TResult}.WaitAsync(CancellationToken)"/>, and keeps observing the task it stopped waiting for.
    /// For work that cannot be interrupted - a file read on a share, a parse, a COM call: Stop gives control back at once
    /// and the work runs to its end with nobody waiting. When it then fails, a plain <c>WaitAsync</c> leaves that
    /// exception unobserved, and the application reports it later as an unexpected error - a dialog about work the
    /// operator gave up on. A wait that was not cancelled behaves exactly like <c>WaitAsync</c>: the result, or the
    /// failure of the work, reaches the caller.
    /// </summary>
    public static Task<T> WaitOrAbandonAsync<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.Forget();
        return task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc cref="WaitOrAbandonAsync{T}(Task{T}, CancellationToken)"/>
    public static Task WaitOrAbandonAsync(this Task task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.Forget();
        return task.WaitAsync(cancellationToken);
    }
}
