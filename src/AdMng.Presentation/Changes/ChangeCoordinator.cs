using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Schema;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Changes;

/// <summary>A change requested by a simple action (enable, move, rename, add to group, delete...).</summary>
/// <param name="TargetDns">Objects to change.</param>
/// <param name="Operations">Declarative operations (see <see cref="BatchOperation"/>).</param>
/// <param name="Title">Localized title, e.g. "Disable account".</param>
public sealed record ChangeRequest(IReadOnlyList<string> TargetDns, IReadOnlyList<BatchOperation> Operations, string Title)
{
    /// <summary>Always open the batch editor preview instead of executing directly.</summary>
    public bool ForcePreview { get; init; }
}

/// <summary>How a <see cref="ChangeRequest"/> ended.</summary>
public enum ChangeOutcome
{
    /// <summary>Executed; every executable object succeeded.</summary>
    Executed,

    /// <summary>Executed; some objects failed (the operator was shown the failures).</summary>
    PartiallyFailed,

    /// <summary>Executed; all executed objects failed, or the execution failed as a whole.</summary>
    Failed,

    /// <summary>Handed over to the batch editor (large or risky change).</summary>
    OpenedInEditor,

    /// <summary>The plan contained no effective change.</summary>
    NothingToDo,

    /// <summary>Validation errors prevent the change (the operator was shown why).</summary>
    Blocked,

    /// <summary>The operator declined, or the request was cancelled.</summary>
    Cancelled,

    NotConnected,
}

/// <summary>
/// Single entry point of the presentation layer into the change pipeline (CLAUDE.md rule 5):
/// <see cref="IChangePlanner"/> → (preview) → <see cref="IBatchExecutor"/> → audit → <see cref="IDirectoryChangeNotifier"/>.
/// </summary>
public interface IChangeCoordinator
{
    /// <summary>
    /// Plans the request; small error-free plans (≤ <see cref="AppSettings.ConfirmationThreshold"/> objects) execute
    /// directly as a background activity, larger ones open the batch editor on the preview step. The plan executes only
    /// in the session it was planned in: when that session was closed or replaced meanwhile, nothing is written and the
    /// operator is told that the session ended. Must be called on the UI thread.
    /// </summary>
    Task<ChangeOutcome> ApplyAsync(ChangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an already reviewed plan as a background activity (activity center), then publishes the resulting
    /// <see cref="DirectoryChange"/>s. Used by the batch editor, property sheets and new-object wizards.
    /// Must be called on the UI thread.
    /// </summary>
    Task<BatchResult> ExecutePlanAsync(ChangePlan plan, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="ExecutePlanAsync(ChangePlan, string, CancellationToken)"/>; <paramref name="started"/> receives the
    /// activity of THIS execution right after it started (on the UI thread), e.g. for a window that shows its own
    /// progress and cancel - other activities started meanwhile are not confused with it.
    /// </summary>
    Task<BatchResult> ExecutePlanAsync(ChangePlan plan, string title, Action<ActivityViewModel>? started, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="ExecutePlanAsync(ChangePlan, string, Action{ActivityViewModel}?, CancellationToken)"/>, but only in
    /// <paramref name="session"/> - the session a window was opened (and planned) in. When it is no longer the current
    /// session (disconnected, or connected again - possibly as another identity) nothing is written and
    /// <see cref="SessionEndedException"/> is thrown: a window never writes with the identity of a newer session.
    /// </summary>
    Task<BatchResult> ExecutePlanAsync(
        ChangePlan plan,
        string title,
        IDirectorySession session,
        Action<ActivityViewModel>? started = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IChangeCoordinator"/>.</summary>
/// <remarks>
/// <para><b>A run whose window closed</b> (the owner's decision D-A): the execution of a window's plan is an activity of the
/// connection, which the window only follows. When the window closed with "Close and continue in the background" - the
/// activity left its window - the coordinator says how it ended, with the sentence the window's line would have said and the
/// domain: a notification when nothing failed, an error dialog with the failed objects and their reasons otherwise. For a run
/// whose result goes back to its window the same report is kept on the activity (<see cref="ActivityViewModel.KeepReport"/>):
/// the window hands it on if it closes before its line said it (Stop and close, a result that came back under the close
/// question) - once the window has closed, so that the dialog is never owned by the window that closes - and drops it
/// otherwise. Either way it is posted to a later turn of the UI thread, so the execution is never held by a modal dialog - the
/// real dialog service shows a dialog inline when it is asked on the UI thread. A run the connection window stopped because it
/// closes is not reported at all (<see cref="ActivityViewModel.IsReportSuppressed"/>).</para>
/// </remarks>
public sealed class ChangeCoordinator : IChangeCoordinator
{
    private const int MaxListedIssues = 10;

    private readonly ISessionManager _sessions;
    private readonly IChangePlanner _planner;
    private readonly IBatchExecutor _executor;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ISettingsStore _settings;
    private readonly ActivityCenterViewModel _activities;
    private readonly IDirectoryChangeNotifier _changes;
    private readonly ErrorTranslator _errors;
    private readonly ILocalizer _localizer;
    private readonly IUiDispatcher _dispatcher;
    private readonly PowerShellHistory? _history;
    private readonly ConnectionContext? _connection;

    /// <remarks>
    /// The optional <see cref="PowerShellHistory"/> records the equivalent PowerShell of every executed plan; with the
    /// window's <see cref="ConnectionContext"/>, planning ends when the window closes (it is not an activity).
    /// </remarks>
    public ChangeCoordinator(
        ISessionManager sessions,
        IChangePlanner planner,
        IBatchExecutor executor,
        IDialogService dialogs,
        INotificationService notifications,
        ISettingsStore settings,
        ActivityCenterViewModel activities,
        IDirectoryChangeNotifier changes,
        ErrorTranslator errors,
        ILocalizer localizer,
        IUiDispatcher dispatcher,
        PowerShellHistory? history = null,
        ConnectionContext? connection = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _history = history;
        _connection = connection;
    }

    public async Task<ChangeOutcome> ApplyAsync(ChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = _sessions.Current;
        if (session is null)
        {
            return ChangeOutcome.NotConnected;
        }

        if (request.TargetDns.Count == 0 || request.Operations.Count == 0)
        {
            return ChangeOutcome.NothingToDo;
        }

        var settings = _settings.Current;
        if (request.ForcePreview || request.TargetDns.Count > settings.ConfirmationThreshold)
        {
            await _dialogs.ShowBatchEditorAsync(new BatchEditorRequest(request.TargetDns)
            {
                Operations = request.Operations,
                OpenPreview = true,
                Title = request.Title,
            }).ConfigureAwait(true);
            return ChangeOutcome.OpenedInEditor;
        }

        ChangePlan plan;
        try
        {
            // Planning is not an activity: closing the window cancels it through the window's lifetime.
            using var planning = _connection?.LinkLifetime(cancellationToken);
            var token = planning?.Token ?? cancellationToken;
            var batch = new BatchRequest(request.TargetDns, request.Operations) { Title = request.Title };
            var options = new PlanningOptions { MaxDegreeOfParallelism = settings.BatchMaxParallelism };
            plan = await Task.Run(() => _planner.PlanAsync(session, batch, options, null, token), token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _connection?.Lifetime.IsCancellationRequested == true)
        {
            return ChangeOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync(request.Title, _errors.Translate(ex)).ConfigureAwait(true);
            return ChangeOutcome.Failed;
        }

        if (_connection?.Lifetime.IsCancellationRequested == true)
        {
            // The window closed while a planner that does not observe cancellation was still running.
            return ChangeOutcome.Cancelled;
        }

        if (plan.ExecutableCount == 0)
        {
            if (plan.ErrorCount > 0)
            {
                await _dialogs.ShowErrorAsync(request.Title, DescribeIssues(plan, session.Schema, IssueSeverity.Error)).ConfigureAwait(true);
                return ChangeOutcome.Blocked;
            }

            _notifications.Show(NotificationSeverity.Info, _localizer.Format(L.Changes.NothingToDo, request.Title));
            return ChangeOutcome.NothingToDo;
        }

        if (plan.ErrorCount > 0)
        {
            var message = _localizer.Format(
                L.Changes.ContinueWithValid, DescribeIssues(plan, session.Schema, IssueSeverity.Error), plan.ExecutableCount, ConfirmationText.Domain(session));
            if (!await _dialogs.ConfirmAsync(request.Title, message, destructive: false).ConfigureAwait(true))
            {
                return ChangeOutcome.Cancelled;
            }
        }

        if (plan.Objects.Any(o => !o.HasErrors && o.HasWarnings))
        {
            _notifications.Show(NotificationSeverity.Warning, DescribeIssues(plan, session.Schema, IssueSeverity.Warning));
        }

        BatchResult result;
        ActivityViewModel? activity = null;
        try
        {
            // Pinned to the session the plan was made in: planning is not an activity, so "Rozłącz" does not wait for
            // it and a reconnect (another identity, or another domain) may have replaced the session meanwhile. Then
            // nothing is written (SessionEndedException, shown as "the session ended").
            result = await ExecutePlanAsync(plan, request.Title, session, started: a => activity = a, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the operator (activity center) - the activity shows it; no error dialog.
            return ChangeOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            if (activity is not { IsReportSuppressed: true })
            {
                await _dialogs.ShowErrorAsync(request.Title, _errors.Translate(ex)).ConfigureAwait(true);
            }

            return ChangeOutcome.Failed;
        }

        // Stopped because its connection window closes: nothing is shown over a window that goes.
        return activity is { IsReportSuppressed: true } ? OutcomeOf(result) : await ReportAsync(request.Title, result).ConfigureAwait(true);
    }

    public Task<BatchResult> ExecutePlanAsync(ChangePlan plan, string title, CancellationToken cancellationToken = default) =>
        ExecutePlanAsync(plan, title, started: null, cancellationToken);

    public async Task<BatchResult> ExecutePlanAsync(ChangePlan plan, string title, Action<ActivityViewModel>? started, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return await ExecuteCoreAsync(plan, title, _sessions.RequireSession(), started, cancellationToken).ConfigureAwait(true);
    }

    public async Task<BatchResult> ExecutePlanAsync(
        ChangePlan plan,
        string title,
        IDirectorySession session,
        Action<ActivityViewModel>? started = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_sessions.Current, session))
        {
            throw new SessionEndedException();
        }

        return await ExecuteCoreAsync(plan, title, session, started, cancellationToken).ConfigureAwait(true);
    }

    private async Task<BatchResult> ExecuteCoreAsync(
        ChangePlan plan,
        string title,
        IDirectorySession session,
        Action<ActivityViewModel>? started,
        CancellationToken cancellationToken)
    {
        var options = new BatchOptions { MaxDegreeOfParallelism = _settings.Current.BatchMaxParallelism };
        var (activity, execution) = _activities.Start(
            title, plan.ExecutableCount, (progress, token) => _executor.ExecuteAsync(session, plan, options, progress, token));
        started?.Invoke(activity);
        using var registration = cancellationToken.Register(activity.RequestCancellation);
        BatchResult result;
        try
        {
            result = await execution.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportWithoutWindow(activity, title, ConfirmationText.Domain(session), null, ex);
            throw;
        }

        _changes.Publish(DirectoryChanges.FromResult(result));
        _history?.Record(result, title, session.Schema, session.Info.DomainDnsName);
        ReportWithoutWindow(activity, title, ConfirmationText.Domain(session), result, null);
        return result;
    }

    /// <summary>
    /// The connection's side of how the run of <paramref name="activity"/> ended. A run that left its window is said now. A run
    /// whose result goes back to the window that follows it keeps the report on its activity: the window hands it on if it
    /// closes before its line said it (<see cref="ExecutionStatus.WindowClosed"/>). A run no window followed is its caller's
    /// (<see cref="ApplyAsync"/>, the property sheet...), and one stopped because its connection window closes is not said at
    /// all. The window state is decided once, atomically against <see cref="ExecutionStatus.LeaveWindow"/>, so exactly one side
    /// reports. The kept report can be made again from what the window's line says (<see cref="ExecutionStatus.Complete"/>,
    /// <see cref="ExecutionStatus.Fail"/>): for a plan the window executes in parts that is more than this activity did.
    /// </summary>
    private void ReportWithoutWindow(ActivityViewModel activity, string title, string domain, BatchResult? result, Exception? failure)
    {
        if (activity.IsReportSuppressed)
        {
            return;
        }

        if (!activity.TryReturnToWindow())
        {
            ReportLater(Describe(title, domain, result, failure));
        }
        else if (activity.HasReturnedToWindow)
        {
            var report = Describe(title, domain, result, failure);
            activity.KeepReport(
                () => ReportLater(report),
                whole =>
                {
                    var again = Describe(title, domain, whole, null);
                    return () => ReportLater(again);
                },
                reason =>
                {
                    var again = DescribeFailure(title, domain, reason);
                    return () => ReportLater(again);
                });
        }
    }

    /// <summary>
    /// Says a report in a later turn of the UI thread: the execution and its caller - or the window that closes - go on at once,
    /// whatever the operator does with a dialog. Toasts and dialogs of a connection window that closed meanwhile are dropped by
    /// their services.
    /// </summary>
    private void ReportLater(Action report) => _dispatcher.InvokeAsync(report).Forget();

    /// <summary>
    /// What the window's line would have said (<see cref="ExecutionOutcome"/>), made into the report of the connection: a
    /// notification when nothing failed - done, or stopped - and otherwise an error dialog with the failed objects, their
    /// reasons and where every change is (the dialog is not awaited). The texts are made now; the report holds only them, never
    /// the result with its plan (ADR-0003, CLAUDE.md rule 2).
    /// </summary>
    private Action Describe(string title, string domain, BatchResult? result, Exception? failure)
    {
        if (failure is not null)
        {
            if (failure is OperationCanceledException)
            {
                var stopped = PageStatusText.Failure(_localizer.Format(L.ChangeStatus.Stopped, title, domain), null);
                return () => _notifications.Show(NotificationSeverity.Warning, stopped);
            }

            return DescribeFailure(title, domain, _errors.Translate(failure));
        }

        var outcome = ExecutionOutcome.Of(_localizer, title, domain, result!);
        if (result!.FailedCount == 0)
        {
            var severity = outcome.Kind == PageStatusKind.Done ? NotificationSeverity.Success : NotificationSeverity.Warning;
            var message = outcome.Message;
            return () => _notifications.Show(severity, message);
        }

        var text = new StringBuilder(outcome.Message).AppendLine();
        var failed = result.Objects.Where(o => o.Status is ObjectResultStatus.Failed or ObjectResultStatus.PartiallySucceeded).ToList();
        foreach (var obj in failed.Take(MaxListedIssues))
        {
            var error = obj.Changes.FirstOrDefault(c => !c.Succeeded);
            var reason = error is null ? string.Empty : _errors.TranslateFailure(error.ErrorKind, error.ErrorMessage);
            text.AppendLine().Append("• ").Append(obj.Plan.DisplayName).Append(": ").Append(reason);
        }

        if (failed.Count > MaxListedIssues)
        {
            text.AppendLine().Append(_localizer.Format(L.Changes.MoreIssues, failed.Count - MaxListedIssues));
        }

        text.AppendLine().AppendLine().Append(_localizer[L.ChangeStatus.SessionLogNext]);
        var failures = text.ToString();
        return () => _dialogs.ShowErrorAsync(title, failures).Forget();
    }

    /// <summary>A failure as a whole, as the window's line says it: a dialog with the reason (not awaited).</summary>
    private Action DescribeFailure(string title, string domain, string reason)
    {
        var failedAsAWhole = PageStatusText.Failure(ExecutionOutcome.FailedLead(_localizer, title, domain), reason);
        return () => _dialogs.ShowErrorAsync(title, failedAsAWhole).Forget();
    }

    private static ChangeOutcome OutcomeOf(BatchResult result) => result.FailedCount == 0
        ? result.WasCutShort ? ChangeOutcome.Cancelled : ChangeOutcome.Executed
        : result.SucceededCount > 0 ? ChangeOutcome.PartiallyFailed : ChangeOutcome.Failed;

    private async Task<ChangeOutcome> ReportAsync(string title, BatchResult result)
    {
        if (result.FailedCount == 0)
        {
            // "Cancelled" only when the Stop left something undone: a plan whose every object was changed was executed,
            // whenever the Stop came, and the caller's follow-up (a re-read, "renamed") must happen.
            var cutShort = result.WasCutShort;
            var severity = cutShort ? NotificationSeverity.Warning : NotificationSeverity.Success;
            var text = cutShort
                ? _localizer.Format(L.Changes.Cancelled, title, result.SucceededCount)
                : _localizer.Format(L.Changes.Succeeded, title, result.SucceededCount);
            _notifications.Show(severity, text);
            return cutShort ? ChangeOutcome.Cancelled : ChangeOutcome.Executed;
        }

        var sb = new StringBuilder();
        sb.AppendLine(_localizer.Format(L.Changes.Failed, result.FailedCount, result.SucceededCount));
        foreach (var failed in result.Objects
                     .Where(o => o.Status is ObjectResultStatus.Failed or ObjectResultStatus.PartiallySucceeded)
                     .Take(MaxListedIssues))
        {
            var error = failed.Changes.FirstOrDefault(c => !c.Succeeded);
            var reason = error is null ? string.Empty : _errors.TranslateFailure(error.ErrorKind, error.ErrorMessage);
            sb.Append("• ").Append(failed.Plan.DisplayName).Append(": ").AppendLine(reason);
        }

        await _dialogs.ShowErrorAsync(title, sb.ToString().TrimEnd()).ConfigureAwait(true);
        return result.SucceededCount > 0 ? ChangeOutcome.PartiallyFailed : ChangeOutcome.Failed;
    }

    private string DescribeIssues(ChangePlan plan, ISchemaCatalog schema, IssueSeverity severity)
    {
        var lines = new List<string>();
        foreach (var obj in plan.Objects)
        {
            var issues = obj.Issues.Concat(obj.Changes.SelectMany(c => c.Issues)).Where(i => i.Severity == severity);
            foreach (var issue in issues)
            {
                lines.Add($"• {obj.DisplayName}: {_errors.TranslateIssue(issue, schema)}");
            }
        }

        var distinct = lines.Distinct(StringComparer.Ordinal).ToList();
        var text = string.Join(Environment.NewLine, distinct.Take(MaxListedIssues));
        return distinct.Count > MaxListedIssues
            ? text + Environment.NewLine + _localizer.Format(L.Changes.MoreIssues, distinct.Count - MaxListedIssues)
            : text;
    }
}
