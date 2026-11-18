using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;
using AdMng.App.Services;
using AdMng.Presentation.Feedback;

namespace AdMng.App.Behaviors;

/// <summary>
/// The progress of the taskbar button of every window that has one (track V, stage V2b-5b, the owner's decision 6; a dialog
/// with <c>ShowInTaskbar="False"</c> - the property sheet, Reset Password, the plan preview - has no button and says it in
/// its status line only; design note
/// docs/dev/v-interface-renewal.md 6.7: "how far - in the action button, the tab header and the taskbar"). A window that
/// is closed to the taskbar or that the operator works away from shows what its status line says: how far a long operation
/// is (a send that takes minutes, the execution of a large plan, a long read), that it cannot say how far, or that it ended
/// with a state that lasts.
/// </summary>
/// <remarks>
/// <para><b>Set up once, for every window.</b> <see cref="Register"/> is called when the application starts
/// (<c>AppHost.InitializeAsync</c>, which the application, the smoke test and the UI tests all run) and adds one class handler
/// for the <see cref="FrameworkElement.Loaded"/> event of every <see cref="Window"/>; no XAML file and no window class names
/// it. The window then follows the holder of its view model: the one of an <see cref="IHasPageStatus"/> view model (the
/// dialogs and tool windows), the holder in front of an <see cref="IHasFrontStatus"/> one (the main window: the selected
/// tab), and nothing for a window without one (the message dialogs), whose button is never touched.</para>
/// <para><b>What it shows</b> is decided by <see cref="TaskbarProgressTracker"/> (no WPF type, tested on Linux): a part done
/// as green progress, an unknown part as the marquee, a state that lasts after an operation as yellow or red, everything else
/// nothing. This class only puts that on <see cref="Window.TaskbarItemInfo"/> - the main window's, which already carries the
/// connection's dot, or one it creates when there is something to show.</para>
/// <para><b>Not motion.</b> Progress on a button is information, not a movement: <see cref="Motion.IsEnabled"/> is not
/// consulted, so it works with animations off. A taskbar that is not there (a remote session without one, a window without a
/// handle yet) is not an error: the known exceptions of the taskbar calls are swallowed, the window works as before.</para>
/// </remarks>
public static class TaskbarProgress
{
    private static readonly DependencyProperty FollowerProperty =
        DependencyProperty.RegisterAttached("Follower", typeof(Follower), typeof(TaskbarProgress), new PropertyMetadata(null));

    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>The clock of the delay before progress is shown; a test replaces it before the window is shown.</summary>
    internal static TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Adds the class handler that makes every window follow its status. Once per process; safe to call again.</summary>
    internal static void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        }
    }

    /// <summary>What the taskbar button of <paramref name="window"/> shows of its progress now (a window that is not followed: none).</summary>
    internal static TaskbarProgressState StateOf(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.GetValue(FollowerProperty) is Follower follower ? follower.State : TaskbarProgressState.None;
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && window.GetValue(FollowerProperty) is null)
        {
            window.SetValue(FollowerProperty, new Follower(window));
        }
    }

    /// <summary>One window and what its button follows.</summary>
    private sealed class Follower
    {
        private readonly Window _window;
        private readonly TaskbarProgressTracker _tracker;
        private IHasFrontStatus? _front;

        public Follower(Window window)
        {
            _window = window;
            _tracker = new TaskbarProgressTracker(new WpfUiDispatcher(window.Dispatcher), Time, () => Motion.TimeOf(Motion.WorkingDelayKey));
            _tracker.PropertyChanged += OnTrackerChanged;
            window.DataContextChanged += OnDataContextChanged;
            window.Closed += OnClosed;
            Attach(window.DataContext);
        }

        public TaskbarProgressState State => _tracker.State;

        /// <summary>Follows the holder of <paramref name="viewModel"/>: its own, the one in front, or none.</summary>
        private void Attach(object? viewModel)
        {
            if (_front is not null)
            {
                _front.PropertyChanged -= OnFrontChanged;
                _front = null;
            }

            switch (viewModel)
            {
                case IHasFrontStatus front:
                    _front = front;
                    front.PropertyChanged += OnFrontChanged;
                    _tracker.Holder = front.FrontStatus;
                    break;
                case IHasPageStatus page:
                    _tracker.Holder = page.Status;
                    break;
                default:
                    _tracker.Holder = null;
                    break;
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Attach(e.NewValue);

        private void OnFrontChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IHasFrontStatus.FrontStatus) && sender is IHasFrontStatus front)
            {
                _tracker.Holder = front.FrontStatus;
            }
        }

        private void OnTrackerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TaskbarProgressTracker.State))
            {
                Apply(_tracker.State);
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _window.Closed -= OnClosed;
            _window.DataContextChanged -= OnDataContextChanged;
            _tracker.PropertyChanged -= OnTrackerChanged;
            if (_front is not null)
            {
                _front.PropertyChanged -= OnFrontChanged;
                _front = null;
            }

            _tracker.Dispose();
        }

        /// <summary>Puts <paramref name="state"/> on the button; a button that has nothing to show and never had anything is left alone.</summary>
        private void Apply(TaskbarProgressState state)
        {
            try
            {
                if (state.Kind == TaskbarProgressKind.None && _window.TaskbarItemInfo is null)
                {
                    return;
                }

                var info = _window.TaskbarItemInfo ??= new TaskbarItemInfo();
                info.ProgressState = state.Kind switch
                {
                    TaskbarProgressKind.Indeterminate => TaskbarItemProgressState.Indeterminate,
                    TaskbarProgressKind.Normal => TaskbarItemProgressState.Normal,
                    TaskbarProgressKind.Paused => TaskbarItemProgressState.Paused,
                    TaskbarProgressKind.Error => TaskbarItemProgressState.Error,
                    _ => TaskbarItemProgressState.None,
                };
                info.ProgressValue = state.Kind is TaskbarProgressKind.Normal or TaskbarProgressKind.Paused or TaskbarProgressKind.Error ? state.Fraction : 0d;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // No taskbar to talk to (a session without one, a button that is not there yet): the window works without it.
            }
        }
    }
}
