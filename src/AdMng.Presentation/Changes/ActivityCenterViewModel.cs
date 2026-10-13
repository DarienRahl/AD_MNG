using System.Collections.ObjectModel;
using AdMng.Core.Batch;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Changes;

/// <summary>
/// Background operations of the session (batch executions) shown in the status bar flyout with progress and
/// cancel, so the operator keeps working while a large batch runs.
/// </summary>
public sealed partial class ActivityCenterViewModel : ObservableObject
{
    private readonly IUiDispatcher _dispatcher;
    private readonly ILocalizer _localizer;
    private readonly ErrorTranslator _errors;
    private readonly TimeProvider _time;

    public ActivityCenterViewModel(IUiDispatcher dispatcher, ILocalizer localizer, ErrorTranslator errors, TimeProvider time)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Newest first.</summary>
    public ObservableCollection<ActivityViewModel> Activities { get; } = [];

    [ObservableProperty]
    public partial int RunningCount { get; private set; }

    public bool HasRunning => RunningCount > 0;

    /// <summary>Status bar text, e.g. "2 operations running".</summary>
    public string Summary => RunningCount == 0 ? string.Empty : _localizer.Format(L.Activity.Running, RunningCount);

    /// <summary>
    /// Starts a background batch execution. Must be called on the UI thread. The returned activity's
    /// <see cref="ActivityViewModel.Completion"/> completes with the result (or the failure).
    /// </summary>
    /// <param name="title">Localized title.</param>
    /// <param name="total">Number of objects (for the progress bar before the first report).</param>
    /// <param name="work">The execution; receives a UI-thread progress sink and the activity's cancellation token.</param>
    public ActivityViewModel Start(string title, int total, Func<IProgress<BatchProgress>, CancellationToken, Task<BatchResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var activity = new ActivityViewModel(title, total, _time.GetLocalNow(), _localizer);
        Activities.Insert(0, activity);
        RunningCount++;
        var progress = _dispatcher.CreateProgress<BatchProgress>(activity.Report);
        activity.Completion = RunAsync(activity, progress, work);
        return activity;
    }

    [RelayCommand]
    private void ClearFinished()
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
            await _dispatcher.RunAsync(() => activity.Finish(result, null, _time.GetLocalNow())).ConfigureAwait(false);
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
            await _dispatcher.RunAsync(() => RunningCount--).ConfigureAwait(false);
        }
    }
}

/// <summary>One background operation.</summary>
public sealed partial class ActivityViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cancellation = new();
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

    /// <summary>Completes with the batch result; faults when the execution itself failed.</summary>
    public Task<BatchResult> Completion { get; internal set; } = null!;

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

    [ObservableProperty]
    public partial BatchResult? Result { get; private set; }

    /// <summary>Localized failure of the execution as a whole (per-object failures are in <see cref="Result"/>).</summary>
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

            if (Result is { WasCancelled: true })
            {
                return _localizer[L.Activity.Cancelled];
            }

            return Result is { FailedCount: > 0 } r
                ? _localizer.Format(L.Activity.CompletedWithFailures, r.SucceededCount, r.FailedCount)
                : _localizer.Format(L.Activity.Completed, Result?.SucceededCount ?? Completed);
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

    internal void Finish(BatchResult? result, string? error, DateTimeOffset finishedAt)
    {
        Result = result;
        Error = error;
        if (result is not null)
        {
            Completed = result.Objects.Count(o => o.Status != ObjectResultStatus.Skipped);
            Failed = result.FailedCount;
        }

        FinishedAt = finishedAt;
        IsRunning = false;
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(StatusText));
    }
}
