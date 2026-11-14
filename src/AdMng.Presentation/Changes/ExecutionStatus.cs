using System.ComponentModel;
using AdMng.Core.Batch;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Changes;

/// <summary>
/// The execution of a change plan as the operation of a window (track V, stage V2b; design note
/// docs/dev/v-interface-renewal.md 4.5 and "V2b-1 as built"): the status line of the window says "Executing “…” in
/// contoso.test: 12 of 300…" with the part done; Stop - in the line or on the action button that started the
/// execution - cancels the activity that executes the plan; and the result names the domain: done, executed only in
/// part, stopped, or failed.
/// </summary>
/// <remarks>
/// <para>It only <b>says</b> what happens. The plan is executed by the coordinator as an activity of the connection, as
/// before (CLAUDE.md rule 5): the window hands the activity it gets from the coordinator to <see cref="Follow"/>. A
/// plan that runs as several activities one after another (the bulk creation wizard: one object first, then the rest)
/// hands over each of them; the numbers add up.</para>
/// <para>Stop works both ways: asked for in the window it cancels the activity, and an activity that was cancelled in
/// the list of background operations makes the window say "Stopping…". An operation that merely ends - the window was
/// closed - does <b>not</b> cancel the activity: the execution keeps running in the background.</para>
/// <para>UI-thread object, like the view model that owns it; put it in a <c>using</c>: disposed without a result the
/// window shows its summary again, never "working".</para>
/// </remarks>
public sealed class ExecutionStatus : IDisposable
{
    private readonly PageStatusHolder _status;
    private readonly OperationScope _scope;
    private readonly ILocalizer _localizer;
    private readonly string _title;
    private readonly string _domain;
    private readonly int _total;
    private readonly CancellationTokenRegistration _stop;
    private ActivityViewModel? _activity;

    // What was reported so far, and the part of it that the earlier activities of this execution did (see Follow).
    private int _done;
    private int _failed;
    private int _doneBefore;
    private int _failedBefore;
    private bool _stopRequested;

    private ExecutionStatus(PageStatusHolder status, ILocalizer localizer, string title, string domain, int total)
    {
        _status = status;
        _localizer = localizer;
        _title = title;
        _domain = domain;
        _total = Math.Max(total, 0);

        // How far is not known until the first object is done: the ring turns until then.
        _scope = status.Begin(Text(0, 0), Log(0, 0));
        _stop = _scope.Token.Register(OnTokenCancelled);
    }

    /// <summary>
    /// The window starts to execute: its status line says so at once, with Stop.
    /// </summary>
    /// <param name="status">The status of the window.</param>
    /// <param name="localizer">Texts.</param>
    /// <param name="title">The title of the change (it may name objects - it is shown, never logged).</param>
    /// <param name="domain">The domain the plan is executed in (<c>WindowViewModelBase.ConfirmationDomain</c>).</param>
    /// <param name="total">
    /// The objects of the plan - all of them: the executor counts every object it is done with, also one it skips, and
    /// the list of background operations shows the same numbers.
    /// </param>
    public static ExecutionStatus Begin(PageStatusHolder status, ILocalizer localizer, string title, string domain, int total)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(domain);
        return new ExecutionStatus(status, localizer, title, domain, total);
    }

    /// <summary>
    /// The activity that executes the plan, or the next part of it: its progress is the progress of the window from now
    /// on. Called from the coordinator's "started" callback (UI thread).
    /// </summary>
    public void Follow(ActivityViewModel activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (ReferenceEquals(activity, _activity))
        {
            return;
        }

        if (_activity is { } previous)
        {
            // The numbers go on from what the earlier activity reported while it ran (its counters of afterwards are
            // those of its outcome, which counts differently).
            previous.PropertyChanged -= OnActivityChanged;
            _doneBefore = _done;
            _failedBefore = _failed;
        }

        _activity = activity;
        activity.PropertyChanged += OnActivityChanged;
        if (_stopRequested)
        {
            // Stop was pressed before this part started.
            StopActivity();
        }

        Report();
    }

    /// <summary>
    /// The execution ended: what happened, how many, and in which domain - done when every object was changed, attention
    /// when it was stopped or some objects failed, an error when none was changed.
    /// </summary>
    /// <param name="result">The result of this execution.</param>
    /// <param name="next">What the operator can do about what was not changed (a sentence), or null.</param>
    /// <param name="action">The command for that, or null. Not used for "done".</param>
    public void Complete(BatchResult result, string? next = null, PageStatusAction? action = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var changed = result.SucceededCount;
        var failed = result.FailedCount;
        var objects = PageStatusText.Objects(_localizer, changed);
        if (result.WasCancelled)
        {
            _scope.CompleteAttention(
                PageStatusText.Failure(
                    _localizer.Format(L.ChangeStatus.Stopped, _title, _domain), _localizer.Format(L.ChangeStatus.CountsStopped, objects, failed), next),
                new PageStatusLog(L.ChangeStatus.Stopped, changed, failed),
                action);
        }
        else if (failed == 0)
        {
            _scope.CompleteDone(_localizer.Format(L.ChangeStatus.Executed, _title, _domain, objects), new PageStatusLog(L.ChangeStatus.Executed, changed));
        }
        else if (changed > 0)
        {
            _scope.CompleteAttention(
                PageStatusText.Failure(
                    _localizer.Format(L.ChangeStatus.ExecutedPartly, _title, _domain), _localizer.Format(L.ChangeStatus.Counts, objects, failed), next),
                new PageStatusLog(L.ChangeStatus.ExecutedPartly, changed, failed),
                action);
        }
        else
        {
            _scope.CompleteError(
                PageStatusText.Failure(FailedLead(), _localizer.Format(L.ChangeStatus.NothingChanged, failed), next),
                new PageStatusLog(L.ChangeStatus.ExecuteFailed, changed, failed),
                action);
        }
    }

    /// <summary>The plan could not be executed at all: what was tried, why, and the result code.</summary>
    /// <param name="reason">The localized reason (a sentence).</param>
    /// <param name="code">The result code of the failure.</param>
    public void Fail(string reason, ResultCode code = default) =>
        _scope.CompleteError(PageStatusText.Failure(FailedLead(), reason, null, code), new PageStatusLog(L.ChangeStatus.ExecuteFailed).WithCode(code));

    /// <summary>
    /// Ends the operation: without a result the window shows its summary again. The activity is not cancelled by that.
    /// </summary>
    public void Dispose()
    {
        if (_activity is { } activity)
        {
            activity.PropertyChanged -= OnActivityChanged;
            _activity = null;
        }

        // First the operation, then the registration: a token that is cancelled because the operation ended must not
        // be taken for a Stop (see OnTokenCancelled).
        _scope.Dispose();
        _stop.Dispose();
    }

    /// <summary>
    /// The token of the operation is cancelled by Stop, and also when the operation ends or is taken off the window. Only
    /// Stop - the operation has not ended - reaches the activity.
    /// </summary>
    private void OnTokenCancelled()
    {
        if (_scope.IsEnded)
        {
            return;
        }

        _status.Post(() =>
        {
            _stopRequested = true;
            StopActivity();
        });
    }

    private void StopActivity()
    {
        if (_activity is { } activity && activity.CancelCommand.CanExecute(null))
        {
            activity.CancelCommand.Execute(null);
        }
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_activity is not { } activity || !ReferenceEquals(sender, activity))
        {
            return;
        }

        if (e.PropertyName == nameof(ActivityViewModel.IsCancelling))
        {
            if (activity.IsCancelling)
            {
                // Cancelled here or in the list of background operations: the window says "Stopping…" at once.
                _scope.Cancel();
            }
        }
        else if (e.PropertyName == nameof(ActivityViewModel.Fraction) && activity.IsRunning)
        {
            // Raised once per progress report of the executor - and once more when the activity ended, with the
            // counters of its outcome: what happened then is said by the result.
            Report();
        }
    }

    private void Report()
    {
        _done = _doneBefore + (_activity?.Completed ?? 0);
        _failed = _failedBefore + (_activity?.Failed ?? 0);
        _scope.Report(Text(_done, _failed), _done > 0 ? PageProgress.Of(_done, Total(_done)) : PageProgress.Indeterminate, Log(_done, _failed));
    }

    /// <summary>The total never falls behind what was done (a plan that grew is not a reason for "5 of 4").</summary>
    private int Total(int done) => Math.Max(_total, done);

    private string Text(int done, int failed)
    {
        var progress = PageStatusText.Progress(_localizer, done, Total(done));
        if (_domain.Length == 0)
        {
            return _localizer.Format(L.ChangeStatus.ExecutingAnywhere, _title, progress);
        }

        return failed > 0
            ? _localizer.Format(L.ChangeStatus.ExecutingFailures, _title, _domain, progress, failed)
            : _localizer.Format(L.ChangeStatus.Executing, _title, _domain, progress);
    }

    // One key for every report - with or without failures it is the same message, said once.
    private PageStatusLog Log(int done, int failed) => new(L.ChangeStatus.Executing, done, Total(done), failed);

    private string FailedLead() => _domain.Length == 0
        ? _localizer.Format(L.ChangeStatus.ExecuteFailedAnywhere, _title)
        : _localizer.Format(L.ChangeStatus.ExecuteFailed, _title, _domain);
}
