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
/// the list of background operations makes the window say "Stopping…". An operation that merely ends does <b>not</b>
/// cancel the activity.</para>
/// <para><b>Closing the window</b> (the owner's decision D-A): the execution is the run of the window's operation
/// (<see cref="IWindowRun"/>), so the close question can offer "Close and continue in the background". Then
/// <see cref="LeaveWindow"/> ends the operation without a word, the window stops following the activity, and the
/// coordinator says how the run ended (a notification, or a dialog with the failures); the window's code sees
/// <see cref="LeftWindow"/> after its await and leaves the result alone. A run whose result must stay with the window says
/// why (<see cref="StaysWithWindow"/>): the generated passwords, the caller that waits for the result, the first account
/// of the bulk wizard. A window that closes after the run ended - Stop and close, a Stop that came first, a result that came
/// back under the question - hands the connection's report of it on once it closed (<see cref="WindowClosed"/>): its line,
/// which said the result, goes with it.</para>
/// <para>Stop <b>finishes the object in progress</b> (V2b-5b, the executor's rule): nothing new is started, but the
/// objects that ran when it came get all their changes. The stopped result therefore says how many objects are done
/// and that the ones that ran were finished (<see cref="BatchResult.FinishedAfterStopCount"/>) - never "half done".</para>
/// <para>UI-thread object, like the view model that owns it; put it in a <c>using</c>: disposed without a result the
/// window shows its summary again, never "working".</para>
/// </remarks>
public sealed class ExecutionStatus : IWindowRun, IDisposable
{
    private readonly PageStatusHolder _status;
    private readonly OperationScope _scope;
    private readonly ILocalizer _localizer;
    private readonly string _title;
    private readonly string _domain;
    private readonly int _total;
    private readonly CancellationTokenRegistration _stop;
    private ActivityViewModel? _activity;

    // The activity followed last: its result went back to this window, with the report the connection would make (D-A).
    private ActivityViewModel? _last;

    // What was reported so far, and the part of it that the earlier activities of this execution did (see Follow).
    private int _done;
    private int _failed;
    private int _doneBefore;
    private int _failedBefore;
    private bool _stopRequested;

    // Closing the window (D-A): why the run must stay with it, whether it left it, whether a close asks about it or waits for
    // it (the connection's report is kept then), whether the window closes right after it, and whether the code ended it.
    private string? _mustStayKey;
    private bool _left;
    private bool _closePending;
    private bool _reportByConnection;
    private bool _disposed;

    private ExecutionStatus(PageStatusHolder status, ILocalizer localizer, string title, string domain, int total)
    {
        _status = status;
        _localizer = localizer;
        _title = title;
        _domain = domain;
        _total = Math.Max(total, 0);

        // How far is not known until the first object is done: the ring turns until then.
        _scope = status.Begin(Text(0, 0), Log(0, 0), writes: OperationWrites.Changes);
        _scope.Run = this;
        _stop = _scope.Token.Register(OnTokenCancelled);
    }

    /// <inheritdoc />
    public string? MustStayReason => _mustStayKey is { } key ? _localizer[key] : null;

    /// <inheritdoc />
    public bool CanLeave => !_left && !_scope.IsFinished && _activity is { HasWindow: true };

    /// <inheritdoc />
    public bool LeftWindow => _left;

    /// <inheritdoc />
    public bool HasReturned => !_left && !_scope.IsFinished && _activity is { HasReturnedToWindow: true };

    /// <inheritdoc />
    public bool IsStopping => _stopRequested || _scope.IsCancelling || _activity is { IsCancelling: true };

    /// <summary>
    /// True when the connection says how this run ended: it left the window, or the window closes right after it
    /// (<see cref="ReportByConnection"/>). A window then leaves out what only an open window needs: its own notification, a
    /// re-read of its list.
    /// </summary>
    public bool ReportsByConnection => _left || _reportByConnection;

    /// <summary>
    /// The window said the outcome itself outside its line, in a notification of the main window - the batch editor always,
    /// the Recycle Bin and Reset Password when they showed theirs: the connection says nothing more when the window closes.
    /// </summary>
    public bool WindowNotifies { get; set; }

    /// <summary>
    /// The run must stay with its window, and why - a key of <c>L.CloseWork.NoBackground…</c> - or null when it may continue
    /// in the background (the default). The close question offers "Close and continue in the background" only without one.
    /// </summary>
    public void StaysWithWindow(string? reasonKey) => _mustStayKey = reasonKey;

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
        if (_left)
        {
            // A part that starts after the run left its window (the next account of the bulk wizard after a forced close):
            // nobody follows it, and the connection says how it ended.
            activity.MarkLeftWithoutWindow();
            return;
        }

        if (ReferenceEquals(activity, _activity))
        {
            return;
        }

        activity.AttachWindow();
        if (_activity is { } previous)
        {
            // The numbers go on from what the earlier activity reported while it ran (its counters of afterwards are
            // those of its outcome, which counts differently). What the connection would say of that part alone is dropped.
            previous.PropertyChanged -= OnActivityChanged;
            previous.TakeReport();
            _doneBefore = _done;
            _failedBefore = _failed;
        }

        _activity = activity;
        _last = activity;
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
    /// when a Stop cut it short (<see cref="BatchResult.WasCutShort"/>) or some objects failed, an error when none was
    /// changed.
    /// </summary>
    /// <param name="result">The result of this execution.</param>
    /// <param name="next">What the operator can do about what was not changed (a sentence), or null.</param>
    /// <param name="action">The command for that, or null. Not used for "done".</param>
    public void Complete(BatchResult result, string? next = null, PageStatusAction? action = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_left)
        {
            // The connection says it: this window is gone.
            return;
        }

        var outcome = ExecutionOutcome.Of(_localizer, _title, _domain, result, next);
        switch (outcome.Kind)
        {
            case PageStatusKind.Done:
                _scope.CompleteDone(outcome.Message, outcome.Log);
                break;
            case PageStatusKind.Attention:
                _scope.CompleteAttention(outcome.Message, outcome.Log, action);
                break;
            default:
                _scope.CompleteError(outcome.Message, outcome.Log, action);
                break;
        }
    }

    /// <summary>The plan could not be executed at all: what was tried, why, and the result code.</summary>
    /// <param name="reason">The localized reason (a sentence).</param>
    /// <param name="code">The result code of the failure.</param>
    public void Fail(string reason, ResultCode code = default)
    {
        if (!_left)
        {
            _scope.CompleteError(
                PageStatusText.Failure(ExecutionOutcome.FailedLead(_localizer, _title, _domain), reason, null, code),
                new PageStatusLog(L.ChangeStatus.ExecuteFailed).WithCode(code));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The operation ends without a word for the window (its line shows the summary again, the holder no longer counts it
    /// as a write) and the window stops following the activity; from now on nothing of the window is touched. The token
    /// of the operation is not taken for a Stop.
    /// </remarks>
    public bool LeaveWindow()
    {
        if (_left)
        {
            return true;
        }

        if (_scope.IsFinished || _activity is not { } activity || !activity.TryLeaveWindow())
        {
            return false;
        }

        _left = true;
        activity.PropertyChanged -= OnActivityChanged;
        _activity = null;
        _stop.Dispose();
        _scope.Complete(null);
        return true;
    }

    /// <inheritdoc />
    public void WindowMayClose() => _closePending = !_left;

    /// <inheritdoc />
    public void ReportByConnection()
    {
        if (WindowNotifies || _left)
        {
            return;
        }

        _closePending = true;
        _reportByConnection = true;
    }

    /// <inheritdoc />
    public void WindowStays()
    {
        _closePending = false;
        _reportByConnection = false;
        if (_disposed)
        {
            // The window's code said the result in the line already: nothing is kept for the connection.
            _last?.TakeReport();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Told only for a run a close looked at (<see cref="WindowMayClose"/>, <see cref="ReportByConnection"/>): the report is
    /// still kept then - it is dropped as soon as the line said the result with no close waiting (<see cref="Dispose"/>) or the
    /// window stayed (<see cref="WindowStays"/>).
    /// </remarks>
    public void WindowClosed()
    {
        var hand = !_left && !WindowNotifies;
        _closePending = false;
        _reportByConnection = false;
        var report = _last?.TakeReport();
        if (hand)
        {
            report?.Invoke();
        }
    }

    /// <inheritdoc />
    public void Stop() => _status.Post(() =>
    {
        _stopRequested = true;
        StopActivity();
    });

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

        _disposed = true;
        if (!_closePending)
        {
            // The line said how the run ended, and no close of the window waits for it: the connection keeps nothing.
            _last?.TakeReport();
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
}
