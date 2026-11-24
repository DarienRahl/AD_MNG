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

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread at <paramref name="priority"/>; the task completes after it ran. A stream that
    /// hands the UI thread its results as fast as they come uses <see cref="UiDispatchPriority.Background"/>, so that the keyboard, the
    /// mouse and the drawing of the window go first (audit W-03). An implementation without priorities runs it as
    /// <see cref="InvokeAsync(Action)"/> does.
    /// </summary>
    Task InvokeAsync(Action action, UiDispatchPriority priority) => InvokeAsync(action);
}

/// <summary>How urgent a piece of work handed to the UI thread is.</summary>
public enum UiDispatchPriority
{
    /// <summary>As <see cref="IUiDispatcher.InvokeAsync(Action)"/>: before input (WPF <c>DispatcherPriority.Normal</c>).</summary>
    Normal = 0,

    /// <summary>After input and drawing (WPF <c>DispatcherPriority.Background</c>): work that must never keep the operator from Stop.</summary>
    Background = 1,
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
