namespace AdMng.Presentation.Services;

/// <summary>
/// Marshals work to the UI thread. Implemented by the WPF app (Dispatcher); tests use a synchronous fake.
/// View models touch observable state only on the UI thread; background work (LDAP streaming, sorting
/// large lists) hands its results over through this service.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller is on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Runs <paramref name="action"/> on the UI thread; the task completes after it ran.</summary>
    Task InvokeAsync(Action action);
}

/// <summary>Helpers for <see cref="IUiDispatcher"/>.</summary>
public static class UiDispatcherExtensions
{
    /// <summary>Runs inline when already on the UI thread, otherwise dispatches.</summary>
    public static Task RunAsync(this IUiDispatcher dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action);
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> whose handler always runs on the UI thread (independent of the
    /// synchronization context of the thread that created it).
    /// </summary>
    public static IProgress<T> CreateProgress<T>(this IUiDispatcher dispatcher, Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(handler);
        return new DispatcherProgress<T>(dispatcher, handler);
    }

    private sealed class DispatcherProgress<T>(IUiDispatcher dispatcher, Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => _ = dispatcher.RunAsync(() => handler(value));
    }
}
