using System.Collections.ObjectModel;
using AdMng.Core.Batch;
using AdMng.Presentation.Errors;
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
public sealed partial class ActivityCenterViewModel : ObservableObject
{
    private readonly IUiDispatcher _dispatcher;
    private readonly ILocalizer _localizer;
    private readonly ErrorTranslator _errors;
    private readonly TimeProvider _time;

    public ActivityCenterViewModel(IUiDispatcher dispatcher, ILocalizer localizer, ErrorTranslator errors, TimeProvider time, ISessionManager sessions)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        ArgumentNullException.ThrowIfNull(sessions);

        // Disconnect or another identity: the finished operations of the previous session (titles, object names) go.
        sessions.SessionChanged += (_, _) => _ = _dispatcher.RunAsync(RemoveFinished);
    }

    /// <summary>Newest first.</summary>
    public ObservableCollection<ActivityViewModel> Activities { get; } = [];

    [ObservableProperty]
    public partial int RunningCount { get; private set; }

    public bool HasRunning => RunningCount > 0;

    /// <summary>Status bar text, e.g. "2 operations running".</summary>
    public string Summary => RunningCount == 0 ? string.Empty : _localizer.Format(L.Activity.Running, RunningCount);

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

/// <summary>What a finished activity displays: counters only (no plan, no values, no passwords).</summary>
internal readonly record struct ActivityOutcome(int Executed, int Succeeded, int Failed, bool WasCancelled)
{
    public static ActivityOutcome From(BatchResult result) => new(
        result.Objects.Count(o => o.Status != ObjectResultStatus.Skipped),
        result.SucceededCount,
        result.FailedCount,
        result.WasCancelled);
}

/// <summary>One background operation.</summary>
public sealed partial class ActivityViewModel : ObservableObject
{
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
}
