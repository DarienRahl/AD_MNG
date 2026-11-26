using System.Collections.ObjectModel;
using System.Windows.Threading;
using AdMng.App.Behaviors;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.App.Services;

/// <summary>A toast shown in the bottom right corner of the main window.</summary>
public sealed partial class ToastViewModel : ObservableObject
{
    private bool _isLeaving;

    internal ToastViewModel(NotificationSeverity severity, string message, Action<ToastViewModel> dismiss)
    {
        Severity = severity;
        Message = message;
        DismissCommand = new RelayCommand(() => dismiss(this));
    }

    public NotificationSeverity Severity { get; }

    public string Message { get; }

    public IRelayCommand DismissCommand { get; }

    /// <summary>
    /// True from the moment the toast was dismissed until it is removed from the list: the view plays its exit
    /// meanwhile (<c>b:Motion.Leaving</c>). Never true when motion is off - the toast is then removed at once.
    /// </summary>
    public bool IsLeaving
    {
        get => _isLeaving;
        internal set => SetProperty(ref _isLeaving, value);
    }

    /// <summary>The running timer of the toast: its time on screen, then the time of its exit.</summary>
    internal DispatcherTimer? Timer { get; set; }
}

/// <summary>
/// <see cref="INotificationService"/> as a toast queue: thread-safe <see cref="Show"/>, at most
/// <see cref="MaxVisible"/> toasts (the oldest is dropped), auto-dismiss after a severity dependent time. Toast texts are
/// held only while shown and never logged. One per connection window (scoped): toasts appear only in their own window,
/// and none after the window closed (<see cref="MarkClosed"/>).
/// </summary>
/// <remarks>
/// With motion on (<see cref="Motion.IsEnabled"/>) a dismissed toast stays in the list for the time of its exit
/// (<c>Motion.Disappear</c> of Themes/Motion.xaml), marked <see cref="ToastViewModel.IsLeaving"/>, and is removed by a
/// timer; with motion off, after the window closed, and when room is made for a new toast it is removed at once. The
/// service owns nothing that needs disposing: a timer that is still running when the connection scope ends only
/// removes its toast from the list, on the UI thread.
/// </remarks>
public sealed class NotificationService : INotificationService
{
    /// <summary>Maximum number of toasts on screen.</summary>
    public const int MaxVisible = 4;

    private readonly WpfUiDispatcher _dispatcher;

    public NotificationService(WpfUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    private volatile bool _closed;

    /// <summary>Visible toasts, oldest first (bind on the UI thread).</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /// <summary>The connection window closed: later toasts of its leftover work are dropped.</summary>
    public void MarkClosed() => _closed = true;

    public void Show(NotificationSeverity severity, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || _closed)
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
            // Room is made at once, without an exit: the limit holds at every moment, also while the oldest toast is
            // already on its way out.
            Remove(Toasts[0]);
        }

        Toasts.Add(toast);
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher.Dispatcher) { Interval = DurationOf(severity) };
        timer.Tick += (_, _) => Dismiss(toast);
        toast.Timer = timer;
        timer.Start();
    }

    /// <summary>
    /// Dismisses a toast (close button, timeout): with motion on it leaves over <c>Motion.Disappear</c> and is removed
    /// then, otherwise it is removed at once. Dismissing a toast that is leaving or already gone does nothing. UI thread.
    /// </summary>
    public void Dismiss(ToastViewModel toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        if (toast.IsLeaving)
        {
            // Its exit is running (or it was removed after it): the timer of the exit removes it.
            return;
        }

        toast.Timer?.Stop();
        toast.Timer = null;
        if (!_closed && Motion.IsEnabled && Toasts.Contains(toast) && Motion.TimeOf(Motion.DisappearKey) is { } exit)
        {
            toast.IsLeaving = true;
            var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher.Dispatcher) { Interval = exit };
            timer.Tick += (_, _) => Remove(toast);
            toast.Timer = timer;
            timer.Start();
            return;
        }

        Remove(toast);
    }

    /// <summary>Takes a toast off the list now and stops its timer (also the one of a running exit).</summary>
    private void Remove(ToastViewModel toast)
    {
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
