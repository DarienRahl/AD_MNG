using System.Windows.Threading;
using AdMng.Presentation.Services;

namespace AdMng.App.Services;

/// <summary><see cref="IUiDispatcher"/> over the WPF <see cref="Dispatcher"/> of the UI thread.</summary>
public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    /// <summary>The underlying dispatcher (for WPF-specific helpers).</summary>
    public Dispatcher Dispatcher => _dispatcher;

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _dispatcher.InvokeAsync(action).Task;
    }
}
