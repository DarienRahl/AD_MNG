using System.Collections.ObjectModel;
using AdMng.Core.Batch;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Changes;

/// <summary>A started background execution.</summary>
/// <param name="Activity">What the activity center shows (progress, cancel, outcome counters).</param>
/// <param name="Execution">
/// Completes with the batch result, or faults/cancels when the execution failed as a whole. Only the caller holds it:
/// the activity keeps no reference to the result, so the plan with its passwords and old/new values becomes
/// collectable as soon as the caller is done with it (ADR-0003, CLAUDE.md rule 2).
/// </param>
public readonly record struct ActivityRun(ActivityViewModel Activity, Task<BatchResult> Execution);

/// <summary>
/// Background operations of the session (batch executions) shown in the status bar flyout with progress and
/// cancel, so the operator keeps working while a large batch runs. Activities keep only what they display (title,
/// counters, error) - never the <see cref="BatchResult"/> - and finished ones are dropped when the session changes.
/// </summary>
/// <remarks>
/// It is also the work of the connection that no window follows (<see cref="IConnectionWork"/>, the owner's decision D-A):
/// the connection window lists those activities in its close question and stops them before it closes.
/// </remarks>
public sealed partial class ActivityCenterViewModel : ObservableObject, IDisposable, IConnectionWork
{
    private readonly IUiDispatcher _dispatcher;
    private readonly ILocalizer _localizer;
    private readonly ErrorTranslator _errors;
    private readonly TimeProvider _time;
    private readonly ISessionManager _sessions;
    private bool _disposed;

    public ActivityCenterViewModel(IUiDispatcher dispatcher, ILocalizer localizer, ErrorTranslator errors, TimeProvider time, ISessionManager sessions)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

        // Disconnect or another identity: the finished operations of the previous session (titles, object names) go.
        _sessions.SessionChanged += OnSessionChanged;
        _localizer.CultureChanged += OnLocalizerCultureChanged;
    }

    /// <summary>
    /// Ends the subscriptions (the activity center is per connection window; the application-wide localizer must not keep a
    /// closed window alive). Running activities are not touched: the window cancels them before it closes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessions.SessionChanged -= OnSessionChanged;
        _localizer.CultureChanged -= OnLocalizerCultureChanged;
    }

    /// <summary>Newest first.</summary>
    public ObservableCollection<ActivityViewModel> Activities { get; } = [];

    [ObservableProperty]
    public partial int RunningCount { get; private set; }

    public bool HasRunning => RunningCount > 0;

    /// <summary>Status bar text, e.g. "2 operations running".</summary>
    public string Summary => RunningCount == 0 ? string.Empty : _localizer.Format(L.Activity.Running, RunningCount);

    /// <summary>
    /// The running activities that no window follows: runs whose window closed with "Close and continue in the background",
    /// changes of simple actions, computer account resets. The executions a window follows are that window's to ask about.
    /// </summary>
    public IReadOnlyList<IBackgroundWork> RunningInBackground => [.. Activities.Where(a => a.IsRunning && !a.HasWindow)];

    /// <summary>The UI thread: the coordinator says how a run without its window ended in a later turn of it.</summary>
    internal IUiDispatcher Dispatcher => _dispatcher;

    /// <summary>
    /// Starts a background batch execution. Must be called on the UI thread. The result is handed to the caller only
    /// through <see cref="ActivityRun.Execution"/>; the activity keeps just the counters it displays.
    /// </summary>
    /// <param name="title">Localized title.</param>
    /// <param name="total">Number of objects (for the progress bar before the first report).</param>
    /// <param name="work">The execution; receives a UI-thread progress sink and the activity's cancellation token.</param>
    public ActivityRun Start(string title, int total, Func<IProgress<BatchProgress>, CancellationToken, Task<BatchResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var activity = new ActivityViewModel(title, total, _time.GetLocalNow(), _localizer);
        Activities.Insert(0, activity);
        RunningCount++;
        var progress = _dispatcher.CreateProgress<BatchProgress>(activity.Report);
        return new ActivityRun(activity, RunAsync(activity, progress, work));
    }

    /// <summary>
    /// Cancels every running activity and waits until all of them have ended - also activities started meanwhile.
    /// Steps already sent to the DC complete and are reported, objects not reached are reported as cancelled, so the
    /// results stay complete (and undoable). Call before the session closes, on the UI thread. Never throws.
    /// </summary>
    public async Task CancelRunningAsync()
    {
        while (Activities.Where(a => a.IsRunning).ToList() is { Count: > 0 } running)
        {
            foreach (var activity in running)
            {
                if (activity.CancelCommand.CanExecute(null))
                {
                    activity.CancelCommand.Execute(null);
                }
            }

            await Task.WhenAll(running.Select(a => a.Completion)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void ClearFinished() => RemoveFinished();

    private void RemoveFinished()
    {
        for (var i = Activities.Count - 1; i >= 0; i--)
        {
            if (!Activities[i].IsRunning)
            {
                Activities.RemoveAt(i);
            }
        }
    }

    partial void OnRunningCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasRunning));
        OnPropertyChanged(nameof(Summary));
    }

    private void OnSessionChanged(object? sender, EventArgs e) => _ = _dispatcher.RunAsync(RemoveFinished);

    private void OnLocalizerCultureChanged(object? sender, EventArgs e) => OnCultureChanged();

    /// <summary>Live language switch: the status bar summary and the activities' status lines in the new language.</summary>
    private void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Summary));
        foreach (var activity in Activities)
        {
            activity.RaiseTextsChanged();
        }
    }

    private async Task<BatchResult> RunAsync(
        ActivityViewModel activity,
        IProgress<BatchProgress> progress,
        Func<IProgress<BatchProgress>, CancellationToken, Task<BatchResult>> work)
    {
        try
        {
            var result = await Task.Run(() => work(progress, activity.CancellationToken)).ConfigureAwait(false);

            // Only counters reach the activity (and the dispatched closure) - never the result with its plan.
            var outcome = ActivityOutcome.From(result);
            await _dispatcher.RunAsync(() => activity.Finish(outcome, null, _time.GetLocalNow())).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            var message = _errors.Translate(ex);
            await _dispatcher.RunAsync(() => activity.Finish(null, message, _time.GetLocalNow())).ConfigureAwait(false);
            throw;
        }
        finally
        {
            try
            {
                await _dispatcher.RunAsync(() => RunningCount--).ConfigureAwait(false);
            }
            finally
            {
                activity.MarkCompleted();
            }
        }
    }
}

/// <summary>
/// What a finished activity displays: counters only (no plan, no values, no passwords). "Cancelled" is said of a run
/// that a Stop cut short (<see cref="BatchResult.WasCutShort"/>), not of one whose Stop came while its last objects ran.
/// </summary>
internal readonly record struct ActivityOutcome(int Executed, int Succeeded, int Failed, bool WasCancelled)
{
    public static ActivityOutcome From(BatchResult result) => new(
        result.Objects.Count(o => o.Status != ObjectResultStatus.Skipped),
        result.SucceededCount,
        result.FailedCount,
        result.WasCutShort);
}

/// <summary>One background operation.</summary>
/// <remarks>
/// <para><b>Its window</b> (the owner's decision D-A): an execution started by a window that follows it in its status line
/// (<c>ExecutionStatus</c>) is <em>followed</em>; when it ends, its result goes back to that window, which says how it ended.
/// A window that closes with "Close and continue in the background" lets the activity <em>leave</em> it: the coordinator
/// then says how it ended (a notification, or a dialog with the failures). The two moments - the window leaves, the result
/// comes back - may meet on different threads (the tests), so the state changes atomically and exactly one side wins. An
/// activity that no window ever followed (a simple action, a property sheet, a computer account reset) is its caller's,
/// which says how it ended itself.</para>
/// </remarks>
public sealed partial class ActivityViewModel : ObservableObject, IBackgroundWork
{
    // The window state: nobody followed it (its caller reports), a window follows it, it left its window (the connection
    // reports), its result went back to the window that follows it.
    private const int NoWindow = 0;
    private const int Followed = 1;
    private const int Left = 2;
    private const int Returned = 3;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILocalizer _localizer;

    internal ActivityViewModel(string title, int total, DateTimeOffset startedAt, ILocalizer localizer)
    {
        Title = title;
        Total = total;
        StartedAt = startedAt;
        _localizer = localizer;
        IsRunning = true;
    }

    public string Title { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Completes when the operation has ended (succeeded, failed or cancelled - see <see cref="StatusText"/>). It never
    /// faults and carries no result: the caller of <see cref="ActivityCenterViewModel.Start"/> gets that.
    /// </summary>
    public Task Completion => _completion.Task;

    internal CancellationToken CancellationToken => _cancellation.Token;

    private int _window;
    private volatile bool _reportsByConnection;
    private volatile bool _reportSuppressed;

    /// <summary>True while a window follows this activity in its status line (until it left it or its result came back).</summary>
    internal bool HasWindow => Volatile.Read(ref _window) == Followed;

    /// <summary>True once the window that followed it closed and let it go on: the connection says how it ended.</summary>
    internal bool HasLeftWindow => Volatile.Read(ref _window) == Left;

    /// <summary>True once its result went back to the window that followed it.</summary>
    internal bool HasReturnedToWindow => Volatile.Read(ref _window) == Returned;

    /// <summary>
    /// The window that closed after this run (Stop and close) asked the connection to say how it ended too - its line goes
    /// with the window.
    /// </summary>
    internal bool ReportsByConnection => _reportsByConnection;

    /// <summary>
    /// Its connection closes: how it ended is not reported in a notification or a dialog (the activity list and the session
    /// log go with the connection window). Set by <see cref="StopBecauseTheConnectionCloses"/>.
    /// </summary>
    internal bool IsReportSuppressed => _reportSuppressed;

    /// <summary>One line of the close question of the connection window (<see cref="IBackgroundWork"/>).</summary>
    string IBackgroundWork.Line => _localizer.Format(L.CloseWork.BackgroundLine, Title, StatusText);

    /// <summary>True once Stop was asked for (<see cref="IBackgroundWork"/>).</summary>
    bool IBackgroundWork.IsStopping => IsCancelling || _cancellation.IsCancellationRequested;

    /// <summary>Completes when the activity ended (<see cref="IBackgroundWork"/>).</summary>
    Task IBackgroundWork.Ended => Completion;

    /// <summary>A window follows this activity from now on (a second call, or one after it left or came back, changes nothing).</summary>
    internal void AttachWindow() => Interlocked.CompareExchange(ref _window, Followed, NoWindow);

    /// <summary>
    /// The window that follows it lets it go on without the window. False when its result already went back to the window
    /// (or no window followed it); true when it left (also when it had left before).
    /// </summary>
    internal bool TryLeaveWindow()
    {
        var previous = Interlocked.CompareExchange(ref _window, Left, Followed);
        return previous is Followed or Left;
    }

    /// <summary>
    /// A part of a run whose window is gone (the next account of the bulk wizard after a forced close): it starts without
    /// a window, and the connection says how it ended.
    /// </summary>
    internal void MarkLeftWithoutWindow() => Interlocked.CompareExchange(ref _window, Left, NoWindow);

    /// <summary>
    /// Its result goes back to its caller: true when the caller's code says how it ended - the window that follows it, or
    /// the caller of a run no window followed - and false when it left its window (the connection says it).
    /// </summary>
    internal bool TryReturnToWindow() => Interlocked.CompareExchange(ref _window, Returned, Followed) != Left;

    /// <summary>The connection says how it ended too (<see cref="ReportsByConnection"/>).</summary>
    internal void MarkReportsByConnection() => _reportsByConnection = true;

    /// <inheritdoc />
    public void StopBecauseTheConnectionCloses()
    {
        _reportSuppressed = true;
        if (CancelCommand.CanExecute(null))
        {
            CancelCommand.Execute(null);
        }
    }

    [ObservableProperty]
    public partial int Completed { get; private set; }

    [ObservableProperty]
    public partial int Total { get; private set; }

    [ObservableProperty]
    public partial int Failed { get; private set; }

    /// <summary>Display name of the object processed last.</summary>
    [ObservableProperty]
    public partial string? CurrentObject { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsCancelling { get; private set; }

    /// <summary>Objects that succeeded (set when the execution ended).</summary>
    [ObservableProperty]
    public partial int SucceededCount { get; private set; }

    /// <summary>Objects that failed or partially failed (set when the execution ended).</summary>
    [ObservableProperty]
    public partial int FailedCount { get; private set; }

    /// <summary>The execution was cancelled; objects not reached were skipped.</summary>
    [ObservableProperty]
    public partial bool WasCancelled { get; private set; }

    /// <summary>Localized failure of the execution as a whole (per-object failures are counted in <see cref="FailedCount"/>).</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>0..1 for the progress bar.</summary>
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);

    /// <summary>"12 / 120 · 1 failed", "Completed", "Cancelled"...</summary>
    public string StatusText
    {
        get
        {
            if (Error is not null)
            {
                return Error;
            }

            if (IsRunning)
            {
                return Failed > 0
                    ? _localizer.Format(L.Activity.ProgressWithFailures, Completed, Total, Failed)
                    : _localizer.Format(L.Activity.Progress, Completed, Total);
            }

            if (WasCancelled)
            {
                return _localizer[L.Activity.Cancelled];
            }

            return FailedCount > 0
                ? _localizer.Format(L.Activity.CompletedWithFailures, SucceededCount, FailedCount)
                : _localizer.Format(L.Activity.Completed, SucceededCount);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsCancelling = true;
        _cancellation.Cancel();
        OnPropertyChanged(nameof(StatusText));
    }

    private bool CanCancel() => IsRunning && !IsCancelling;

    /// <summary>Thread-safe cancellation request (no UI state change), e.g. from a caller's token.</summary>
    internal void RequestCancellation() => _cancellation.Cancel();

    internal void Report(BatchProgress progress)
    {
        if (!IsRunning)
        {
            return;
        }

        Total = progress.Total;
        Completed = progress.Completed;
        Failed = progress.Failed;
        CurrentObject = progress.Last?.Plan.DisplayName;
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(StatusText));
    }

    internal void Finish(ActivityOutcome? outcome, string? error, DateTimeOffset finishedAt)
    {
        Error = error;
        if (outcome is { } o)
        {
            Completed = o.Executed;
            Failed = o.Failed;
            SucceededCount = o.Succeeded;
            FailedCount = o.Failed;
            WasCancelled = o.WasCancelled;
        }

        FinishedAt = finishedAt;
        IsRunning = false;
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(StatusText));
    }

    internal void MarkCompleted() => _completion.TrySetResult();

    /// <summary>The localized texts depend on the UI language (see <see cref="ActivityCenterViewModel"/>).</summary>
    internal void RaiseTextsChanged() => OnPropertyChanged(nameof(StatusText));
}
