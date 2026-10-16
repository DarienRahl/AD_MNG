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
}
