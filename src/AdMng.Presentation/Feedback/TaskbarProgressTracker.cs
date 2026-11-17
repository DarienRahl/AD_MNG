using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Feedback;

/// <summary>What the taskbar button of a window shows of its progress (the Windows taskbar's states, without a WPF type).</summary>
public enum TaskbarProgressKind
{
    /// <summary>No progress: the button is as it is.</summary>
    None,

    /// <summary>Something runs and cannot say how far it is (the taskbar's marquee).</summary>
    Indeterminate,

    /// <summary>Something runs, <see cref="TaskbarProgressState.Fraction"/> of it is done (green).</summary>
    Normal,

    /// <summary>An operation ended with an attention that lasts (yellow).</summary>
    Paused,

    /// <summary>An operation ended with an error that lasts (red).</summary>
    Error,
}

/// <summary>The progress of the taskbar button: its kind and, for <see cref="TaskbarProgressKind.Normal"/>, <see cref="TaskbarProgressKind.Paused"/> and <see cref="TaskbarProgressKind.Error"/>, how far.</summary>
/// <param name="Kind">What the button shows.</param>
/// <param name="Fraction">The part done, 0 to 1 (0 for <see cref="TaskbarProgressKind.None"/> and <see cref="TaskbarProgressKind.Indeterminate"/>).</param>
public readonly record struct TaskbarProgressState(TaskbarProgressKind Kind, double Fraction = 0d)
{
    /// <summary>No progress.</summary>
    public static TaskbarProgressState None => default;

    /// <summary>Something runs and cannot say how far it is.</summary>
    public static TaskbarProgressState Indeterminate => new(TaskbarProgressKind.Indeterminate);

    /// <summary>Something runs; <paramref name="fraction"/> of it is done.</summary>
    public static TaskbarProgressState Normal(double fraction) => new(TaskbarProgressKind.Normal, Math.Clamp(fraction, 0d, 1d));
}

/// <summary>
/// The progress of the taskbar button of a window (track V, stage V2b-5b; design note docs/dev/v-interface-renewal.md 6.7: "how
/// far - in the action button, the tab header and the taskbar"): it follows the status line of the window - the holder in front
/// (<see cref="PageStatusHolder.Current"/>, which already is what the page in front says) - and says nothing the line does not.
/// Without a WPF type: the window hands the holder over and puts <see cref="State"/> on its button.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Working</b> with a part done is <see cref="TaskbarProgressKind.Normal"/> with that fraction, without a count (or while
/// it says "Stopping…") <see cref="TaskbarProgressKind.Indeterminate"/>. It appears after the same delay the line waits before it
/// shows "Working" (<c>Motion.WorkingDelay</c>, handed in): a read that ends at once never flashes on the button, and a button
/// that shows progress always has a line that says what.</item>
/// <item><b>An operation that ended with a state that lasts</b> - an attention or an error that cannot be closed ("could not be
/// read", "was deleted"), after its progress was on the button - keeps the button <see cref="TaskbarProgressKind.Paused"/>
/// (yellow) or <see cref="TaskbarProgressKind.Error"/> (red) at the place it stopped, for as long as the page shows that state:
/// the operator who works elsewhere sees that it did not end well. Anything the page says instead - a new operation, a result,
/// the summary - ends it. A state that lasts without an operation behind it (the session ended, an object that was deleted)
/// and a result that can be closed are not the button's business: <see cref="TaskbarProgressKind.None"/> - also when an
/// operation ran meanwhile and ended well, and the page shows that state again. A state at the very start (the first item
/// failed) fills a small part of the button, so that it can be seen.</item>
/// <item><b>Everything else is None:</b> the summary, a closable result, no holder at all.</item>
/// </list>
/// A write that runs on a page that is not in front is not on the button, as it is not in the line: the close question of the
/// window still finds it (<see cref="RunningWork"/>).
/// </remarks>
public sealed class TaskbarProgressTracker : ObservableObject, IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    /// <summary>
    /// The least part of the button a yellow or red state fills. An operation that failed before its first item was done
    /// stopped at nothing, and a state at nothing is drawn as no state at all.
    /// </summary>
    internal const double MinimumLatchedFraction = 0.1;

    private readonly Func<TimeSpan?> _delay;

    // What the page said before the operation that is (or was last) on the way, and whether that was on the button.
    private PageStatus? _before;
    private bool _beforeLatched;
    private PageStatusHolder? _holder;
    private ITimer? _timer;
    private int _generation;

    // What a running operation would put on the button, and whether it is on it (the delay has run out).
    private TaskbarProgressState _working;
    private bool _shown;
    private double _lastFraction = 1d;

    // The state that lasts an operation of the button ended with.
    private PageStatus? _latched;
    private bool _disposed;

    /// <param name="dispatcher">The UI thread: the delay runs on a timer thread and hands over to it.</param>
    /// <param name="time">The clock; a test passes one it moves.</param>
    /// <param name="workingDelay">The delay before progress is shown (<c>Motion.WorkingDelay</c>); null or not positive: none. Read when an operation arrives.</param>
    public TaskbarProgressTracker(IUiDispatcher dispatcher, TimeProvider? time = null, Func<TimeSpan?>? workingDelay = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _time = time ?? TimeProvider.System;
        _delay = workingDelay ?? (() => null);
    }

    /// <summary>What the taskbar button shows now. Raises <c>PropertyChanged</c> when it changes.</summary>
    public TaskbarProgressState State { get; private set; }

    /// <summary>
    /// The holder the button follows: the window's own, or the one of the page in front (the main window follows its selected
    /// tab), or null. Setting it starts from what that holder says now; nothing that was on the button for another holder stays.
    /// </summary>
    public PageStatusHolder? Holder
    {
        get => _holder;
        set
        {
            if (ReferenceEquals(_holder, value))
            {
                return;
            }

            if (_holder is not null)
            {
                _holder.PropertyChanged -= OnHolderChanged;
            }

            StopDelay();
            _holder = value;
            _shown = false;
            _latched = null;

            // Nothing that was on the button for the holder before stays: what the new one says starts from nothing.
            Set(TaskbarProgressState.None);
            if (value is not null && !_disposed)
            {
                value.PropertyChanged += OnHolderChanged;
            }

            Evaluate();
        }
    }

    /// <summary>The window closed: the holder is let go and the delay ends.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Holder = null;
        StopDelay();
    }

    private void OnHolderChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageStatusHolder.Current) && ReferenceEquals(sender, _holder))
        {
            Evaluate();
        }
    }

    private void Evaluate()
    {
        var status = _holder?.Current ?? PageStatus.Empty;
        if (status.IsWorking)
        {
            _working = status.Progress?.Fraction is { } fraction ? TaskbarProgressState.Normal(fraction) : TaskbarProgressState.Indeterminate;
            if (_shown)
            {
                // The stages and the counts of an operation that is on the button follow at once.
                Show(_working);
            }
            else
            {
                // Until it is shown the button keeps what it showed, as the line does.
                StartDelay();
            }

            return;
        }

        // Something else is said: an operation that did not reach the button never will, and one that did has ended.
        StopDelay();
        if (_shown && IsLasting(status) && !WasThereBefore(status))
        {
            _latched = status;
        }

        _shown = false;
        if (_latched is { } latched && !ReferenceEquals(latched, status) && !latched.HasSameContentAs(status))
        {
            _latched = null;
        }

        _before = status;
        _beforeLatched = _latched is not null;

        Set(_latched is null
            ? TaskbarProgressState.None
            : new TaskbarProgressState(
                _latched.Kind == PageStatusKind.Error ? TaskbarProgressKind.Error : TaskbarProgressKind.Paused,
                Math.Max(_lastFraction, MinimumLatchedFraction)));
    }

    /// <summary>
    /// The state the page shows again after an operation is the one it had before, and no operation had put it on the
    /// button: "the results are stale", "the journal cannot be written" are states of the page, and an operation that ran
    /// meanwhile and ended well has nothing to do with them. (A state that an earlier operation left on the button comes
    /// back with a retry that fails the same way.)
    /// </summary>
    private bool WasThereBefore(PageStatus status) =>
        !_beforeLatched && _before is { } before && (ReferenceEquals(before, status) || before.HasSameContentAs(status));

    /// <summary>An attention or an error that is a state of the page, not a result the operator may close.</summary>
    private static bool IsLasting(PageStatus status) => status is { Kind: PageStatusKind.Attention or PageStatusKind.Error, CanClose: false };

    private void StartDelay()
    {
        if (_timer is not null)
        {
            // The delay runs on from the first moment; a second status of the same operation only replaces what it will show.
            return;
        }

        if (_delay() is not { } delay || delay <= TimeSpan.Zero)
        {
            ShowWorking();
            return;
        }

        var generation = ++_generation;
        _timer = _time.CreateTimer(_ => _ = _dispatcher.RunAsync(() => OnDelayElapsed(generation)), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void OnDelayElapsed(int generation)
    {
        if (_disposed || generation != _generation || _timer is null)
        {
            return;
        }

        StopDelay();
        if (_holder?.Current.IsWorking == true)
        {
            ShowWorking();
        }
    }

    /// <summary>The operation is on the button now: it takes the place of a state that lasts from an earlier one.</summary>
    private void ShowWorking()
    {
        _shown = true;
        _latched = null;
        Show(_working);
    }

    private void StopDelay()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }

    private void Show(TaskbarProgressState state)
    {
        if (state.Kind == TaskbarProgressKind.Normal)
        {
            _lastFraction = state.Fraction;
        }
        else
        {
            // A new operation that cannot say how far it is starts from nothing; a state it ends with then shows a full bar.
            _lastFraction = 1d;
        }

        Set(state);
    }

    private void Set(TaskbarProgressState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        OnPropertyChanged(nameof(State));
    }
}
