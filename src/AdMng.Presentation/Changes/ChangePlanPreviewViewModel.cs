using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Changes;

/// <summary>What <see cref="ChangePlanPreviewViewModel"/> shows.</summary>
/// <param name="Plan">A ready plan (GPO operations: sequential, with semantic PowerShell).</param>
/// <param name="Title">Localized title of the change (activity center, audit, PowerShell history).</param>
public sealed record ChangePlanPreviewRequest(ChangePlan Plan, string Title)
{
    /// <summary>Localized notes shown above the rows (e.g. "changes are made on DC X, the PDC emulator is Y").</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>When set, Execute asks the operator to type this text first (destructive operations: deleting a GPO).</summary>
    public string? ConfirmationText { get; init; }

    /// <summary>The message of the typed confirmation (names the domain).</summary>
    public string? ConfirmationMessage { get; init; }
}

/// <summary>Step of the preview window.</summary>
public enum PlanPreviewStep
{
    Preview,
    Executing,
    Results,
}

/// <summary>
/// The change plan preview window (design note docs/dev/k-computers-and-gpo.md 3.3): every GPO operation opens it - no
/// threshold-based direct execution. Rows of the batch editor's preview (SYSVOL rows with the path and sizes, the version
/// row "komputer 3 → 4"), "Pokaż różnice" for a selected file row (settings of the shared parsers with secrets masked, text
/// lines, or "binary"), the PowerShell tab, Execute (typed confirmation when requested) as a background activity in the
/// window's own session, then the results with Undo (the rollback plan is shown here again, executed the same way).
/// Contents of files stay in memory with the plan (rule 1).
/// </summary>
public sealed partial class ChangePlanPreviewViewModel : WindowViewModelBase
{
    private readonly BatchEngines _engines;
    private readonly ChangePlanPreviewRequest _request;
    private readonly SerialCancellation _diff = new();
    private ChangePlan _plan;
    private bool _executedAny;

    public ChangePlanPreviewViewModel(DialogServices services, BatchEngines engines, ChangePlanPreviewRequest request)
        : base(services)
    {
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _plan = request.Plan ?? throw new ArgumentException("The request has no plan.", nameof(request));
        Notes = request.Notes;
        Rebuild();
    }

    public override string WindowTitle => Localizer.Format(L.PlanPreview.Title, _request.Title);

    /// <summary>Title of the change.</summary>
    public string ChangeTitle => IsRollbackPreview ? _plan.Title : _request.Title;

    /// <summary>The plan shown (after Undo: the rollback plan).</summary>
    public ChangePlan Plan => _plan;

    public IReadOnlyList<string> Notes { get; }

    public bool HasNotes => Notes.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewStep), nameof(IsExecutingStep), nameof(IsResultsStep))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand), nameof(UndoCommand), nameof(CloseCommand))]
    public partial PlanPreviewStep Step { get; private set; }

    public bool IsPreviewStep => Step == PlanPreviewStep.Preview;

    public bool IsExecutingStep => Step == PlanPreviewStep.Executing;

    public bool IsResultsStep => Step == PlanPreviewStep.Results;

    /// <summary>The rows of the plan (one per attribute, file, version or folder step).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PreviewRowViewModel> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial PreviewRowViewModel? SelectedRow { get; set; }

    /// <summary>"3 objects, 9 steps on dc01.contoso.test."</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>The equivalent PowerShell (semantic cmdlets; SYSVOL steps without one as comments, never file content).</summary>
    [ObservableProperty]
    public partial string PowerShellScript { get; private set; } = string.Empty;

    /// <summary>Header of the differences of the selected file row (or the hint to select one).</summary>
    [ObservableProperty]
    public partial string DiffHeader { get; private set; } = string.Empty;

    /// <summary>The lines of the differences of the selected file row.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GpoDiffLine> DiffLines { get; private set; } = [];

    /// <summary>The undo plan is shown (Execute reverts the change).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangeTitle))]
    public partial bool IsRollbackPreview { get; private set; }

    /// <summary>The last execution's result.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    public partial BatchResult? Result { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ResultRowViewModel> ResultRows { get; private set; } = [];

    [ObservableProperty]
    public partial string ResultText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool ResultHasFailures { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The activity of the running execution (progress, cancel).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelExecutionCommand))]
    public partial ActivityViewModel? CurrentActivity { get; private set; }

    /// <summary>Something was written to the directory or SYSVOL (the caller refreshes what it shows).</summary>
    public bool ChangedDirectory => _executedAny;

    public override Task<bool> CanCloseAsync() => Task.FromResult(Step != PlanPreviewStep.Executing);

    /// <summary>
    /// The localized text of a failed step: SYSVOL failures say whether the file changes were reverted or the GPO was left
    /// inconsistent; others go through <see cref="ErrorTranslator.TranslateFailure"/>.
    /// </summary>
    public static string TranslateFailure(ILocalizer localizer, ErrorTranslator errors, DirectoryErrorKind? kind, string? message)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(errors);
        if (string.Equals(message, BatchExecutor.FolderChangedMessage, StringComparison.Ordinal))
        {
            return localizer[L.PlanPreview.FolderChanged];
        }

        if (string.Equals(message, BatchExecutor.SysvolUnavailableMessage, StringComparison.Ordinal))
        {
            return localizer.Format(L.PlanPreview.SysvolUnavailable, localizer.ErrorKindName(kind ?? DirectoryErrorKind.Unknown));
        }

        var known = kind is null or DirectoryErrorKind.Unknown ? localizer.ErrorKindName(DirectoryErrorKind.Unknown) : errors.TranslateFailure(kind, message);
        if (message?.StartsWith(GpoTransaction.InconsistentMessage, StringComparison.Ordinal) == true)
        {
            return localizer.Format(L.PlanPreview.Inconsistent, known);
        }

        if (message?.StartsWith(GpoTransaction.RevertedMessage, StringComparison.Ordinal) == true)
        {
            return localizer.Format(L.PlanPreview.Reverted, known);
        }

        return errors.TranslateFailure(kind, message);
    }

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        var plan = _plan;
        if (plan.ExecutableCount == 0)
        {
            Services.Notifications.Show(NotificationSeverity.Info, Localizer.Format(L.Changes.NothingToDo, ChangeTitle));
            return;
        }

        if (!IsRollbackPreview && _request.ConfirmationText is { Length: > 0 } expected)
        {
            if (!await Services.Dialogs.ConfirmTypedAsync(ChangeTitle, _request.ConfirmationMessage ?? ChangeTitle, expected).ConfigureAwait(true))
            {
                return;
            }
        }

        Error = null;
        Step = PlanPreviewStep.Executing;
        BatchResult result;
        try
        {
            // Only in the session the plan was made in (its DC: CLAUDE.md rule 4) - never with a newer session's identity.
            result = await Services.Coordinator.ExecutePlanAsync(plan, ChangeTitle, RequireSession(), activity => CurrentActivity = activity)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = Errors.Translate(ex);
            Step = PlanPreviewStep.Preview;
            return;
        }
        finally
        {
            CurrentActivity = null;
        }

        _executedAny |= result.Objects.Any(o => o.Changes.Any(c => c.Succeeded));
        if (!IsDisposed)
        {
            ShowResults(result);
        }
    }

    private bool CanExecute() => Step == PlanPreviewStep.Preview && !IsSessionEnded && _plan.ExecutableCount > 0;

    [RelayCommand(CanExecute = nameof(CanCancelExecution))]
    private void CancelExecution()
    {
        if (CurrentActivity is { } activity && activity.CancelCommand.CanExecute(null))
        {
            activity.CancelCommand.Execute(null);
        }
    }

    private bool CanCancelExecution() => CurrentActivity is { IsRunning: true };

    /// <summary>Shows the rollback plan of the last result in the preview (Execute reverts the change).</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (Result is not { } result)
        {
            return;
        }

        var rollback = _engines.Rollback.CreateRollbackPlan(result);
        if (rollback.ExecutableCount == 0)
        {
            Services.Notifications.Show(NotificationSeverity.Info, Localizer[L.PlanPreview.NothingToUndo]);
            return;
        }

        _plan = rollback;
        IsRollbackPreview = true;
        Result = null;
        ResultRows = [];
        ResultText = Localizer[L.PlanPreview.RollbackShown];
        ResultHasFailures = false;
        Rebuild();
        Step = PlanPreviewStep.Preview;
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo() => Step == PlanPreviewStep.Results && !IsSessionEnded && Result is { } result && result.Objects.Any(o => o.Changes.Any(c => c.Succeeded));

    [RelayCommand]
    private void CopyPowerShell() =>
        ClipboardCopy.Text(Services.Clipboard, Services.Notifications, PowerShellScript, Localizer[L.Batch.PowerShellCopied]);

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() => RequestClose(_executedAny);

    private bool CanClose() => Step != PlanPreviewStep.Executing;

    protected override void OnSessionEnded()
    {
        Error = Localizer[L.PlanPreview.SessionEnded];
        ExecuteCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        Rebuild();
        if (Result is { } result)
        {
            ShowResults(result);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _diff.Cancel();
        }

        base.Dispose(disposing);
    }

    partial void OnSelectedRowChanged(PreviewRowViewModel? value) => UpdateDiffAsync(value).Forget();

    private void Rebuild()
    {
        var session = Session;
        var builder = new PreviewBuilder(Localizer, Errors, Services.Sessions.Formatter, session?.Schema ?? OpenedSessionSchema ?? Core.Schema.SchemaCatalog.Empty, Services.Time);
        Rows = [.. builder.Build(_plan).SelectMany(o => o.Rows)];
        var steps = _plan.Objects.Sum(o => o.Changes.Count);
        Summary = Localizer.Format(L.PlanPreview.Summary, _plan.Objects.Count, steps, _plan.Server ?? session?.Info.Server ?? string.Empty);
        PowerShellScript = _engines.PowerShell.Export(_plan, _plan.Server ?? session?.Info.Server, session?.Schema ?? OpenedSessionSchema);
        SelectedRow = Rows.FirstOrDefault(r => r.Sysvol is not null);
        if (SelectedRow is null)
        {
            DiffHeader = Localizer[L.PlanPreview.DiffNone];
            DiffLines = [];
        }
    }

    private void ShowResults(BatchResult result)
    {
        Result = result;
        var session = Session;
        var builder = new PreviewBuilder(Localizer, Errors, Services.Sessions.Formatter, session?.Schema ?? OpenedSessionSchema ?? Core.Schema.SchemaCatalog.Empty, Services.Time);
        ResultRows = [.. result.Objects.Select(o =>
        {
            var row = builder.BuildResult(o);
            var failed = o.Changes.FirstOrDefault(c => !c.Succeeded);
            return failed is null ? row : new ResultRowViewModel(o, row.StatusText, TranslateFailure(Localizer, Errors, failed.ErrorKind, failed.ErrorMessage));
        })];
        var failedCount = result.FailedCount;
        ResultHasFailures = failedCount > 0 || result.WasCancelled;
        ResultText = failedCount == 0 && !result.WasCancelled
            ? Localizer[L.PlanPreview.ResultSucceeded]
            : Localizer.Format(L.PlanPreview.ResultFailed, failedCount, result.Objects.Count);
        Step = PlanPreviewStep.Results;
        UndoCommand.NotifyCanExecuteChanged();
    }

    private async Task UpdateDiffAsync(PreviewRowViewModel? row)
    {
        var token = _diff.Next();
        if (row?.Sysvol is not { RelativePath: { } path } sysvol)
        {
            DiffHeader = Localizer[L.PlanPreview.DiffNone];
            DiffLines = [];
            return;
        }

        var newContent = row.Object.Plan.Changes.FirstOrDefault(c => ReferenceEquals(c.Sysvol, sysvol))?.Action == ChangeAction.DeleteGpoFile ? null : sysvol.NewContent;
        GpoFileDiffResult diff;
        try
        {
            diff = await Task.Run(() => GpoFileDiff.Compare(path, sysvol.OldContent, newContent), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        var header = diff.Kind switch
        {
            GpoFileDiffKind.Binary => Localizer.Format(L.PlanPreview.DiffBinary, Size(sysvol.OldContent), Size(newContent)),
            GpoFileDiffKind.Settings => Localizer.Format(L.PlanPreview.DiffSettings, diff.Added, diff.Removed),
            _ => Localizer.Format(L.PlanPreview.DiffText, diff.Added, diff.Removed),
        };
        DiffHeader = diff.Truncated ? header + " " + Localizer[L.PlanPreview.DiffTruncated] : header;
        DiffLines = diff.Lines;
    }

    private string Size(byte[]? content) => content is null
        ? Localizer[L.PlanPreview.Absent]
        : Localizer.Format(L.PlanPreview.Bytes, content.Length.ToString("N0", Localizer.Culture));
}
