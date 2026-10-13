using System.Collections.ObjectModel;
using System.Windows.Threading;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.App.Services;

/// <summary>A toast shown in the bottom right corner of the main window.</summary>
public sealed partial class ToastViewModel : ObservableObject
{
    internal ToastViewModel(NotificationSeverity severity, string message, Action<ToastViewModel> dismiss)
    {
        Severity = severity;
        Message = message;
        DismissCommand = new RelayCommand(() => dismiss(this));
    }

    public NotificationSeverity Severity { get; }

    public string Message { get; }

    public IRelayCommand DismissCommand { get; }

    internal DispatcherTimer? Timer { get; set; }
}

/// <summary>
/// <see cref="INotificationService"/> as a toast queue: thread-safe <see cref="Show"/>, at most
/// <see cref="MaxVisible"/> toasts (the oldest is dropped), auto-dismiss after a severity dependent time. Toast texts are
/// held only while shown and never logged.
/// </summary>
public sealed class NotificationService : INotificationService
{
    /// <summary>Maximum number of toasts on screen.</summary>
    public const int MaxVisible = 4;

    private readonly WpfUiDispatcher _dispatcher;

    public NotificationService(WpfUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>Visible toasts, oldest first (bind on the UI thread).</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    public void Show(NotificationSeverity severity, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => Show(severity, message));
            return;
        }

        var toast = new ToastViewModel(severity, message, Dismiss);
        while (Toasts.Count >= MaxVisible)
        {
            Dismiss(Toasts[0]);
        }

        Toasts.Add(toast);
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher.Dispatcher) { Interval = DurationOf(severity) };
        timer.Tick += (_, _) => Dismiss(toast);
        toast.Timer = timer;
        timer.Start();
    }

    /// <summary>Removes a toast (close button, timeout).</summary>
    public void Dismiss(ToastViewModel toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        toast.Timer?.Stop();
        toast.Timer = null;
        Toasts.Remove(toast);
    }

    /// <summary>Errors and warnings stay longer than confirmations.</summary>
    internal static TimeSpan DurationOf(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => TimeSpan.FromSeconds(12),
        NotificationSeverity.Warning => TimeSpan.FromSeconds(8),
        _ => TimeSpan.FromSeconds(5),
    };
}
