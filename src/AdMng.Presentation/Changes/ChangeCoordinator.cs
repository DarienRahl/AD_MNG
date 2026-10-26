using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Schema;
using AdMng.Presentation.Errors;
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
    private readonly PowerShellHistory? _history;

    /// <remarks>The optional <see cref="PowerShellHistory"/> records the equivalent PowerShell of every executed plan.</remarks>
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
        PowerShellHistory? history = null)
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
        _history = history;
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
            var batch = new BatchRequest(request.TargetDns, request.Operations) { Title = request.Title };
            var options = new PlanningOptions { MaxDegreeOfParallelism = settings.BatchMaxParallelism };
            plan = await Task.Run(() => _planner.PlanAsync(session, batch, options, null, cancellationToken), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ChangeOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync(request.Title, _errors.Translate(ex)).ConfigureAwait(true);
            return ChangeOutcome.Failed;
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
            var message = _localizer.Format(L.Changes.ContinueWithValid, DescribeIssues(plan, session.Schema, IssueSeverity.Error), plan.ExecutableCount);
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
        try
        {
            // Pinned to the session the plan was made in: planning is not an activity, so "Rozłącz" does not wait for it,
            // and a reconnect (another identity, or another domain) may have replaced the session meanwhile. Then nothing
            // is written (SessionEndedException, shown as "the session ended").
            result = await ExecutePlanAsync(plan, request.Title, session, started: null, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the operator (activity center) - the activity shows it; no error dialog.
            return ChangeOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync(request.Title, _errors.Translate(ex)).ConfigureAwait(true);
            return ChangeOutcome.Failed;
        }

        return await ReportAsync(request.Title, result).ConfigureAwait(true);
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
        var result = await execution.ConfigureAwait(true);
        _changes.Publish(DirectoryChanges.FromResult(result));
        _history?.Record(result, title, session.Schema);
        return result;
    }

    private async Task<ChangeOutcome> ReportAsync(string title, BatchResult result)
    {
        if (result.FailedCount == 0)
        {
            var severity = result.WasCancelled ? NotificationSeverity.Warning : NotificationSeverity.Success;
            var text = result.WasCancelled
                ? _localizer.Format(L.Changes.Cancelled, title, result.SucceededCount)
                : _localizer.Format(L.Changes.Succeeded, title, result.SucceededCount);
            _notifications.Show(severity, text);
            return result.WasCancelled ? ChangeOutcome.Cancelled : ChangeOutcome.Executed;
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
