using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Templates;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.BatchEditing;

/// <summary>Wizard step of the batch editor.</summary>
public enum BatchEditorStep
{
    /// <summary>Operations (objects mode) or file and column mapping (CSV mode).</summary>
    Edit,

    /// <summary>The plan as a diff: before → after per attribute, findings, include check boxes.</summary>
    Preview,

    /// <summary>The plan runs as a background activity (progress, cancel).</summary>
    Executing,

    /// <summary>Per-object outcome, export, retry failed, undo.</summary>
    Results,
}

/// <summary>Where the batch editor's objects come from.</summary>
public enum BatchSourceMode
{
    /// <summary>Selected objects (DNs) with operations.</summary>
    Objects,

    /// <summary>Rows of a CSV file mapped to attributes.</summary>
    Csv,
}

/// <summary>
/// Batch editor (Ctrl+Shift+B) and CSV import: 1. operations (or CSV mapping), 2. preview of the plan with filters and
/// per-object include check boxes, export of the preview and the equivalent PowerShell, 3. execution as a background
/// activity with progress and cancel (typed confirmation for large or risky batches), 4. results with export (passwords
/// only after a warning), retry of failed objects and undo (rollback plan → back to the preview).
/// </summary>
/// <remarks>
/// Opened with <see cref="BatchEditorRequest"/>: an empty <see cref="BatchEditorRequest.TargetDns"/> starts the CSV
/// import mode. With <see cref="BatchEditorRequest.OpenPreview"/> the pre-filled operations are planned right after
/// <see cref="InitializeAsync"/>.
/// </remarks>
public sealed partial class BatchEditorViewModel : WindowViewModelBase
{
    private readonly BatchEngines _engines;
    private readonly BatchEditorRequest _request;
    private readonly OperationEditorContext _editorContext;
    private readonly SerialCancellation _planning = new();
    private readonly List<PreviewObjectViewModel> _previewObjects = [];
    private readonly List<PreviewRowViewModel> _previewRows = [];
    private readonly List<ResultRowViewModel> _resultRows = [];
    private readonly HashSet<string> _sampleAttributes = new(StringComparer.OrdinalIgnoreCase);

    // Steps of the current operations that already succeeded, per object (DnKey of its current DN; see StepKeys): a
    // retry runs only the failed and not executed steps - never a second password reset, which would invalidate the
    // password already handed out. Accumulated over retries, cleared when new operations are previewed.
    private readonly Dictionary<string, HashSet<string>> _succeededSteps = new(StringComparer.Ordinal);

    private DirectoryObject? _sampleTarget;
    private ChangePlan? _plan;
    private PlanSource _planSource;
    private IReadOnlyList<BatchOperation> _plannedOperations = [];
    private bool _passwordsExported;

    // Whether Result came from operations that generate passwords. Captured when the results are shown: the planned
    // operations change with Undo, Back + Preview or Retry while Result still holds the generated passwords.
    private bool _resultHasGeneratedPasswords;

    public BatchEditorViewModel(DialogServices services, BatchEngines engines, BatchEditorRequest request)
        : base(services)
    {
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _editorContext = new OperationEditorContext(services, engines.Templates, RenderSampleAsync, () => Session);
        Mode = request.TargetDns.Count == 0 ? BatchSourceMode.Csv : BatchSourceMode.Objects;
        OperationTypes = [.. Enum.GetValues<OperationType>().Select(t => new OperationTypeOption(
            t, Localizer[DisplayNames.OperationTypeKey(t)], Localizer.Format(DisplayNames.OperationDescriptionKey(t))))];
        PreviewFilters = [.. Enum.GetValues<PreviewFilter>().Select(f => new Option<PreviewFilter>(f, Localizer[DisplayNames.PreviewFilterKey(f)]))];
        ResultFilters = [.. Enum.GetValues<ResultFilter>().Select(f => new Option<ResultFilter>(f, Localizer[DisplayNames.ResultFilterKey(f)]))];
        if (Mode == BatchSourceMode.Csv)
        {
            CsvImport = new CsvImportViewModel(services, engines, () => Session);
            CsvImport.Changed += OnCsvImportChanged;
        }
        else
        {
            Targets.ReplaceAll(request.TargetDns.Select(dn => DirectoryItem.Unresolved(dn, Localizer)));
        }

        Operations.CollectionChanged += OnOperationsChanged;
        foreach (var operation in request.Operations)
        {
            Operations.Add(OperationEditorViewModel.From(operation, _editorContext));
        }
    }

    private enum PlanSource
    {
        Operations,
        Csv,
        Rollback,
    }

    /// <summary>"Batch edit – 120 objects" / "Import from CSV".</summary>
    public override string WindowTitle => Mode == BatchSourceMode.Csv
        ? Localizer[L.Batch.CsvWindowTitle]
        : Localizer.Format(L.Batch.WindowTitle, TargetCount);

    /// <summary>Title of the change (activity center, audit, notifications).</summary>
    public string ChangeTitle => !string.IsNullOrWhiteSpace(_request.Title) ? _request.Title
        : Mode == BatchSourceMode.Csv ? Localizer.Format(L.Batch.CsvChangeTitle, CsvImport?.FileName ?? string.Empty)
        : Localizer.Format(L.Batch.DefaultChangeTitle, TargetCount);

    public BatchSourceMode Mode { get; }

    public bool IsCsvMode => Mode == BatchSourceMode.Csv;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditStep), nameof(IsPreviewStep), nameof(IsExecutingStep), nameof(IsResultsStep))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(ExecuteCommand), nameof(BackToEditCommand), nameof(ExportPreviewCommand),
        nameof(CopyPowerShellCommand), nameof(ExportResultsCommand), nameof(RetryFailedCommand), nameof(UndoCommand))]
    public partial BatchEditorStep Step { get; private set; }

    public bool IsEditStep => Step == BatchEditorStep.Edit;

    public bool IsPreviewStep => Step == BatchEditorStep.Preview;

    public bool IsExecutingStep => Step == BatchEditorStep.Executing;

    public bool IsResultsStep => Step == BatchEditorStep.Results;

    // ---- Targets ------------------------------------------------------------------------------------------

    /// <summary>Target objects (names and types resolved by <see cref="InitializeAsync"/>).</summary>
    public BulkObservableCollection<DirectoryItem> Targets { get; } = [];

    public int TargetCount => _request.TargetDns.Count;

    /// <summary>"Użytkownik: 10, Grupa: 2".</summary>
    [ObservableProperty]
    public partial string TargetSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoadingTargets { get; private set; }

    // ---- Operations ---------------------------------------------------------------------------------------

    public ObservableCollection<OperationEditorViewModel> Operations { get; } = [];

    /// <summary>Entries of the "Add operation" menu.</summary>
    public IReadOnlyList<OperationTypeOption> OperationTypes { get; }

    /// <summary>Writable, non-binary attributes of the targets' classes (attribute autocomplete).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AttributeOption> AttributeOptions { get; private set; } = [];

    /// <summary>Localized problem of the operation list as a whole (e.g. delete combined with other operations).</summary>
    [ObservableProperty]
    public partial string? OperationsError { get; private set; }

    /// <summary>The CSV import (CSV mode only).</summary>
    public CsvImportViewModel? CsvImport { get; }

    // ---- Planning / errors -------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(ExecuteCommand), nameof(RetryFailedCommand), nameof(UndoCommand), nameof(BackToEditCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Objects planned so far / to plan (progress bar while planning).</summary>
    [ObservableProperty]
    public partial int PlanningCompleted { get; private set; }

    [ObservableProperty]
    public partial int PlanningTotal { get; private set; }

    /// <summary>Localized error of the last planning or execution attempt.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    // ---- Preview ------------------------------------------------------------------------------------------

    /// <summary>Visible preview rows (filtered).</summary>
    public BulkObservableCollection<PreviewRowViewModel> PreviewRows { get; } = [];

    /// <summary>All objects of the preview (include check boxes).</summary>
    public IReadOnlyList<PreviewObjectViewModel> PreviewObjects => _previewObjects;

    public IReadOnlyList<Option<PreviewFilter>> PreviewFilters { get; }

    [ObservableProperty]
    public partial PreviewFilter PreviewFilter { get; set; }

    /// <summary>The preview shows an undo (rollback) plan.</summary>
    [ObservableProperty]
    public partial bool IsRollbackPreview { get; private set; }

    [ObservableProperty]
    public partial int ObjectCount { get; private set; }

    /// <summary>Objects that will change (included).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    public partial int ChangeCount { get; private set; }

    [ObservableProperty]
    public partial int WarningCount { get; private set; }

    [ObservableProperty]
    public partial int ErrorCount { get; private set; }

    [ObservableProperty]
    public partial int NoChangeCount { get; private set; }

    /// <summary>Objects excluded by the operator.</summary>
    [ObservableProperty]
    public partial int ExcludedCount { get; private set; }

    /// <summary>"120 objects: 100 will change, 15 without change, 5 with errors (2 warnings)".</summary>
    [ObservableProperty]
    public partial string PreviewSummary { get; private set; } = string.Empty;

    // ---- Execution and results ----------------------------------------------------------------------------

    /// <summary>The running (or last) execution: progress, current object, cancel.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelExecutionCommand))]
    public partial ActivityViewModel? CurrentActivity { get; private set; }

    [ObservableProperty]
    public partial BatchResult? Result { get; private set; }

    /// <summary>Visible result rows (filtered).</summary>
    public BulkObservableCollection<ResultRowViewModel> ResultRows { get; } = [];

    public IReadOnlyList<Option<ResultFilter>> ResultFilters { get; }

    [ObservableProperty]
    public partial ResultFilter ResultFilter { get; set; }

    /// <summary>"Succeeded: 118, failed: 2, skipped: 5".</summary>
    [ObservableProperty]
    public partial string ResultSummary { get; private set; } = string.Empty;

    public override async Task InitializeAsync()
    {
        try
        {
            if (Mode == BatchSourceMode.Objects)
            {
                await LoadTargetsAsync().ConfigureAwait(true);
            }

            foreach (var editor in Operations)
            {
                editor.RefreshSample();
            }

            if (_request.OpenPreview && Operations.Count > 0 && CanPreview())
            {
                // Through the command, so that its Stop button (PreviewCancelCommand) can cancel planning thousands of objects.
                await PreviewCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = Errors.Translate(ex);
        }
    }

    public override async Task<bool> CanCloseAsync()
    {
        // Generated passwords exist only in this window: closing without exporting them (or while they are being
        // set) leaves the accounts with passwords nobody knows.
        if ((HasUnsavedGeneratedPasswords || (IsExecutingStep && GeneratesPasswords))
            && !await Services.Dialogs.ConfirmAsync(WindowTitle, Localizer[L.Batch.DiscardPasswords], destructive: true).ConfigureAwait(true))
        {
            return false;
        }

        // Planning is cancelled; an execution keeps running as a background activity (activity center).
        _planning.Cancel();
        return true;
    }

    /// <summary>
    /// The results contain generated passwords that were not exported (closing or executing again asks first). Stays
    /// true while an undo plan or new operations are previewed: the results are only replaced by the next execution.
    /// </summary>
    public bool HasUnsavedGeneratedPasswords =>
        !_passwordsExported
        && _resultHasGeneratedPasswords
        && Result is { } result
        && result.Objects.Any(o => o.Changes.Any(c => c.Succeeded && c.Change.Action == ChangeAction.ResetPassword));

    [RelayCommand]
    private void AddOperation(OperationType type)
    {
        var editor = OperationEditorViewModel.Create(type, _editorContext);
        Operations.Add(editor);
        editor.RefreshSample();
    }

    [RelayCommand]
    private void RemoveOperation(OperationEditorViewModel? editor)
    {
        if (editor is not null)
        {
            Operations.Remove(editor);
        }
    }

    [RelayCommand]
    private void MoveOperationUp(OperationEditorViewModel? editor) => MoveOperation(editor, -1);

    [RelayCommand]
    private void MoveOperationDown(OperationEditorViewModel? editor) => MoveOperation(editor, 1);

    /// <summary>Plans the operations (or the CSV import) and shows the preview.</summary>
    [RelayCommand(CanExecute = nameof(CanPreview), IncludeCancelCommand = true)]
    private async Task PreviewAsync(CancellationToken cancellationToken)
    {
        if (Mode == BatchSourceMode.Csv)
        {
            var csv = CsvImport!;
            await RunPlanningAsync((progress, token) => csv.PlanAsync(progress, token), csv.RowCount, PlanSource.Csv, cancellationToken).ConfigureAwait(true);
            return;
        }

        var operations = Operations.Select(e => e.Build()).ToList();
        if (operations.Any(o => o is null) || ValidateOperations() is not null)
        {
            return;
        }

        IReadOnlyList<BatchOperation> built = [.. operations.OfType<BatchOperation>()];
        await RunPlanningAsync(
            (progress, token) => PlanOperationsAsync(_request.TargetDns, built, new Dictionary<string, HashSet<string>>(), progress, token),
            TargetCount,
            PlanSource.Operations,
            cancellationToken).ConfigureAwait(true);
    }

    private bool CanPreview() =>
        Step == BatchEditorStep.Edit && !IsBusy && !IsSessionEnded
        && (Mode == BatchSourceMode.Csv
            ? CsvImport!.IsValid
            : TargetCount > 0 && Operations.Count > 0 && Operations.All(o => o.IsValid) && OperationsError is null);

    [RelayCommand(CanExecute = nameof(CanBackToEdit))]
    private void BackToEdit()
    {
        _planning.Cancel();
        IsRollbackPreview = false;
        Step = BatchEditorStep.Edit;
    }

    private bool CanBackToEdit() => Step is BatchEditorStep.Preview or BatchEditorStep.Results;

    /// <summary>Includes or excludes every object that would change.</summary>
    [RelayCommand]
    private void SetAllIncluded(bool included)
    {
        foreach (var obj in _previewObjects.Where(o => o.CanToggle))
        {
            obj.IsIncluded = included;
        }
    }

    [RelayCommand(CanExecute = nameof(IsPreviewStep))]
    private async Task ExportPreviewAsync()
    {
        if (_plan is null)
        {
            return;
        }

        var plan = IncludedPlan(_plan);
        var schema = OpenedSessionSchema;
        await ExportAsync("preview", writer => _engines.Reports.Write(writer, plan, includePasswords: false, CsvFiles.ListSeparator(Services.Regional.Culture), schema))
            .ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(IsPreviewStep))]
    private void CopyPowerShell()
    {
        if (_plan is null)
        {
            return;
        }

        var session = Session;
        var script = _engines.PowerShell.Export(IncludedPlan(_plan), session?.Info.Server ?? _plan.Server, session?.Schema);
        ClipboardCopy.Text(Services.Clipboard, Services.Notifications, script, Localizer[L.Batch.PowerShellCopied]);
    }

    /// <summary>Executes the included objects (typed confirmation for large or risky batches).</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        if (_plan is null)
        {
            return;
        }

        var plan = IncludedPlan(_plan);
        var executable = plan.ExecutableCount;
        if (executable == 0)
        {
            Services.Notifications.Show(NotificationSeverity.Info, Localizer.Format(L.Changes.NothingToDo, ChangeTitle));
            return;
        }

        // The new results replace the current ones (retry, undo, edited operations): generated passwords that were
        // not exported would be gone for good.
        if (HasUnsavedGeneratedPasswords
            && !await Services.Dialogs.ConfirmAsync(ChangeTitle, Localizer[L.Batch.ReplacePasswords], destructive: true).ConfigureAwait(true))
        {
            return;
        }

        var risky = plan.Objects.Where(o => !o.HasErrors && !o.IsNoOp)
            .SelectMany(o => o.Changes)
            .Any(c => c.Action is ChangeAction.Delete or ChangeAction.Move or ChangeAction.ResetPassword);
        if (executable > Services.Settings.Current.ConfirmationThreshold || risky)
        {
            var expected = executable.ToString(CultureInfo.InvariantCulture);
            var message = Localizer.Format(L.Batch.ConfirmExecute, executable, ChangeTitle, expected);
            if (!await Services.Dialogs.ConfirmTypedAsync(ChangeTitle, message, expected).ConfigureAwait(true))
            {
                return;
            }
        }

        Error = null;
        Step = BatchEditorStep.Executing;
        BatchResult result;
        try
        {
            // The coordinator hands over the activity of this execution: progress and Cancel must never follow another
            // operation the operator starts meanwhile (another batch editor, a disable from the main window...).
            // Only in the session the plan was made in - never with the identity of a newer session.
            result = await Services.Coordinator.ExecutePlanAsync(plan, ChangeTitle, RequireSession(), activity => CurrentActivity = activity)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = Errors.Translate(ex);
            Step = BatchEditorStep.Preview;
            return;
        }

        // Closed while executing: the activity center reports the outcome and no results (passwords) are kept.
        if (!IsDisposed)
        {
            ShowResults(result);
        }
    }

    private bool CanExecute() => Step == BatchEditorStep.Preview && !IsBusy && ChangeCount > 0 && !IsSessionEnded;

    [RelayCommand(CanExecute = nameof(CanCancelExecution))]
    private void CancelExecution()
    {
        if (CurrentActivity is { } activity && activity.CancelCommand.CanExecute(null))
        {
            activity.CancelCommand.Execute(null);
        }
    }

    private bool CanCancelExecution() => CurrentActivity is { IsRunning: true };

    /// <summary>
    /// Exports the results; the passwords of the resets that succeeded and the values of secret attributes only when the
    /// operator confirms the warning (masked otherwise).
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsResultsStep))]
    private async Task ExportResultsAsync()
    {
        if (Result is not { } result)
        {
            return;
        }

        var schema = OpenedSessionSchema;
        var includePasswords = false;
        if (BatchReportExporter.ContainsSecrets(result, schema))
        {
            includePasswords = await Services.Dialogs.ConfirmAsync(
                Localizer[L.Batch.ExportPasswordsTitle], Localizer[L.Batch.ExportPasswordsWarning], destructive: true).ConfigureAwait(true);
        }

        if (await ExportAsync("results", writer => _engines.Reports.Write(writer, result, includePasswords, CsvFiles.ListSeparator(Services.Regional.Culture), schema))
                .ConfigureAwait(true) && includePasswords)
        {
            _passwordsExported = true;
        }
    }

    /// <summary>Plans the failed (or cancelled) objects again with fresh values and shows the preview.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private async Task RetryFailedAsync()
    {
        if (Result is not { } result)
        {
            return;
        }

        var failed = result.Objects.Where(IsRetryable).ToList();
        if (failed.Count == 0)
        {
            return;
        }

        if (_planSource == PlanSource.Csv && CsvImport is { } csv)
        {
            var rows = failed.Select(o => o.Plan.SourceRow).OfType<int>().ToList();
            await RunPlanningAsync((progress, token) => csv.PlanAsync(progress, token, rows), rows.Count, PlanSource.Csv, CancellationToken.None)
                .ConfigureAwait(true);
            return;
        }

        // Only the failed and not executed steps run again (steps that succeeded in this or an earlier run are left
        // out): e.g. an object whose password was reset keeps it - a new reset would invalidate the one handed out.
        var dns = failed.Select(o => o.FinalDn).ToList();
        var succeeded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var obj in failed)
        {
            var steps = new HashSet<string>(StringComparer.Ordinal);
            if (_succeededSteps.TryGetValue(DnKey.For(obj.Plan.DistinguishedName), out var earlier))
            {
                steps.UnionWith(earlier);
            }

            steps.UnionWith(obj.Changes.Where(c => c.Succeeded).SelectMany(c => StepKeys(c.Change)));
            if (steps.Count > 0)
            {
                succeeded[DnKey.For(obj.FinalDn)] = steps;
            }
        }

        var operations = _plannedOperations;
        await RunPlanningAsync((progress, token) => PlanOperationsAsync(dns, operations, succeeded, progress, token), dns.Count, PlanSource.Operations, CancellationToken.None)
            .ConfigureAwait(true);
    }

    private bool CanRetryFailed() =>
        Step == BatchEditorStep.Results && !IsBusy && !IsSessionEnded && _planSource != PlanSource.Rollback
        && Result is { } result && result.Objects.Any(IsRetryable);

    /// <summary>Creates the rollback plan of the results and shows it in the preview (execute to undo).</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (Result is not { } result)
        {
            return;
        }

        var rollback = _engines.Rollback.CreateRollbackPlan(result);
        if (rollback.Objects.Count == 0)
        {
            Services.Notifications.Show(NotificationSeverity.Info, Localizer[L.Batch.NothingToUndo]);
            return;
        }

        ShowPreview(rollback, PlanSource.Rollback);
    }

    private bool CanUndo() => Step == BatchEditorStep.Results && !IsBusy && Result is { SucceededCount: > 0 } or { FailedCount: > 0 };

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (await CanCloseAsync().ConfigureAwait(true))
        {
            RequestClose(Result is not null);
        }
    }

    partial void OnPreviewFilterChanged(PreviewFilter value) => ApplyPreviewFilter();

    partial void OnResultFilterChanged(ResultFilter value) => ApplyResultFilter();

    partial void OnOperationsErrorChanged(string? value) => PreviewCommand.NotifyCanExecuteChanged();

    /// <remarks>
    /// Results and previews stay readable (export, PowerShell, the undo plan), but nothing is planned or executed any
    /// more; a running execution was cancelled by the disconnect and reports its outcome as usual.
    /// </remarks>
    protected override void OnSessionEnded()
    {
        _planning.Cancel();
        Error = SessionEndedMessage;
        PreviewCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _planning.Cancel();
            Operations.CollectionChanged -= OnOperationsChanged;
            foreach (var editor in Operations)
            {
                editor.Changed -= OnOperationChanged;
            }

            if (CsvImport is not null)
            {
                CsvImport.Changed -= OnCsvImportChanged;
            }

            foreach (var obj in _previewObjects)
            {
                obj.PropertyChanged -= OnPreviewObjectChanged;
            }

            CurrentActivity = null;

            // Drop plan, operations and results (they may hold the fixed or generated passwords) as soon as the
            // window is gone.
            _plan = null;
            _plannedOperations = [];
            Result = null;
            _previewObjects.Clear();
            _previewRows.Clear();
            _resultRows.Clear();
            _sampleTarget = null;
        }
    }

    private bool GeneratesPasswords =>
        _planSource == PlanSource.Operations && _plannedOperations.OfType<ResetPasswordOperation>().Any(o => o.Mode == PasswordMode.GenerateUnique);

    private static bool IsRetryable(ObjectResult result) =>
        result.Status is ObjectResultStatus.Failed or ObjectResultStatus.PartiallySucceeded or ObjectResultStatus.Cancelled;

    /// <summary>
    /// Plans <paramref name="operations"/> for <paramref name="targets"/> with fresh values, leaving out the steps listed
    /// in <paramref name="succeeded"/> (per DnKey, see <see cref="StepKeys"/>) - they already ran.
    /// </summary>
    private async Task<ChangePlan> PlanOperationsAsync(
        IReadOnlyList<string> targets,
        IReadOnlyList<BatchOperation> operations,
        Dictionary<string, HashSet<string>> succeeded,
        IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        var session = RequireSession();
        var request = new BatchRequest(targets, operations) { Title = ChangeTitle };
        var options = new PlanningOptions { MaxDegreeOfParallelism = Services.Settings.Current.BatchMaxParallelism };
        var planner = _engines.Planner;
        var plan = await Task.Run(() => planner.PlanAsync(session, request, options, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(true);
        if (succeeded.Count > 0)
        {
            plan = plan with
            {
                Objects = [.. plan.Objects.Select(o => succeeded.TryGetValue(DnKey.For(o.DistinguishedName), out var done) ? WithoutSteps(o, done) : o)],
            };
        }

        _succeededSteps.Clear();
        foreach (var (key, steps) in succeeded)
        {
            _succeededSteps[key] = steps;
        }

        _plannedOperations = operations;
        return plan;
    }

    /// <summary>
    /// Identifies what a step did, so that a retry can leave it out: one key per attribute of a modify step, the group
    /// of a membership step, the action otherwise (an object has at most one reset, rename and move).
    /// </summary>
    private static IEnumerable<string> StepKeys(PlannedChange change) => change.Action switch
    {
        ChangeAction.Modify => change.Attributes.Select(a => AttributeStepKey(a.Attribute)),
        ChangeAction.AddMember or ChangeAction.RemoveMember => [$"{change.Action}:{DnKey.For(change.TargetDn ?? string.Empty)}"],
        _ => [change.Action.ToString()],
    };

    private static string AttributeStepKey(string attribute) => "Modify:" + attribute.ToUpperInvariant();

    /// <summary>The object's plan without the steps (and attribute changes) in <paramref name="done"/>.</summary>
    private static ObjectPlan WithoutSteps(ObjectPlan plan, HashSet<string> done)
    {
        var changes = new List<PlannedChange>(plan.Changes.Count);
        foreach (var change in plan.Changes)
        {
            if (change.Action != ChangeAction.Modify)
            {
                if (!StepKeys(change).All(done.Contains))
                {
                    changes.Add(change);
                }

                continue;
            }

            var remaining = change.Attributes.Where(a => !done.Contains(AttributeStepKey(a.Attribute))).ToList();
            if (remaining.Count == change.Attributes.Count)
            {
                changes.Add(change);
            }
            else if (remaining.Count > 0)
            {
                changes.Add(change with
                {
                    Attributes = remaining,
                    Issues = [.. change.Issues.Where(i => i.Attribute is null || remaining.Any(a => string.Equals(a.Attribute, i.Attribute, StringComparison.OrdinalIgnoreCase)))],
                });
            }
        }

        return plan with { Changes = changes };
    }

    private async Task RunPlanningAsync(
        Func<IProgress<int>, CancellationToken, Task<ChangePlan>> plan,
        int total,
        PlanSource source,
        CancellationToken cancellationToken)
    {
        var token = _planning.Next();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
        IsBusy = true;
        Error = null;
        PlanningCompleted = 0;
        PlanningTotal = total;
        try
        {
            var progress = Services.Dispatcher.CreateProgress<int>(n =>
            {
                if (_planning.IsCurrent(token))
                {
                    PlanningCompleted = n;
                }
            });
            var result = await plan(progress, linked.Token).ConfigureAwait(true);
            if (!IsDisposed)
            {
                ShowPreview(result, source);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Cancelled by the operator, by Back or by closing the window.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = Errors.Translate(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowPreview(ChangePlan plan, PlanSource source)
    {
        foreach (var obj in _previewObjects)
        {
            obj.PropertyChanged -= OnPreviewObjectChanged;
        }

        var session = Session;
        var builder = new PreviewBuilder(Localizer, Errors, Services.Sessions.Formatter, session?.Schema ?? Core.Schema.SchemaCatalog.Empty);
        _plan = plan;
        _planSource = source;
        _previewObjects.Clear();
        _previewObjects.AddRange(builder.Build(plan));
        _previewRows.Clear();
        _previewRows.AddRange(_previewObjects.SelectMany(o => o.Rows));
        foreach (var obj in _previewObjects)
        {
            obj.PropertyChanged += OnPreviewObjectChanged;
        }

        IsRollbackPreview = source == PlanSource.Rollback;
        OnPropertyChanged(nameof(PreviewObjects));
        PreviewFilter = PreviewFilter.All;
        ApplyPreviewFilter();
        UpdateCounts();
        Step = BatchEditorStep.Preview;
    }

    private void ShowResults(BatchResult result)
    {
        var session = Session;
        var builder = new PreviewBuilder(Localizer, Errors, Services.Sessions.Formatter, session?.Schema ?? Core.Schema.SchemaCatalog.Empty);
        Result = result;
        _resultHasGeneratedPasswords = GeneratesPasswords;
        _passwordsExported = false;
        _resultRows.Clear();
        _resultRows.AddRange(result.Objects.Select(builder.BuildResult));
        ResultFilter = result.FailedCount > 0 ? ResultFilter.Failed : ResultFilter.All;
        ApplyResultFilter();
        ResultSummary = Localizer.Format(L.Batch.ResultSummary, result.SucceededCount, result.FailedCount, result.SkippedCount);
        Step = BatchEditorStep.Results;
        var severity = result.FailedCount > 0 ? NotificationSeverity.Warning : result.WasCancelled ? NotificationSeverity.Warning : NotificationSeverity.Success;
        Services.Notifications.Show(severity, Localizer.Format(L.Batch.ExecutedNotification, ChangeTitle, ResultSummary));
    }

    private ChangePlan IncludedPlan(ChangePlan plan) =>
        plan with { Objects = [.. _previewObjects.Where(o => !o.CanToggle || o.IsIncluded).Select(o => o.Plan)] };

    private void ApplyPreviewFilter()
    {
        PreviewRows.ReplaceAll(_previewRows.Where(r => PreviewFilter switch
        {
            PreviewFilter.Changes => r.IsChange && !r.Object.HasErrors,
            PreviewFilter.Warnings => r.Object.HasWarnings,
            PreviewFilter.Errors => r.Object.HasErrors,
            PreviewFilter.NoChange => r.Object.IsNoOp,
            _ => true,
        }));
    }

    private void ApplyResultFilter()
    {
        ResultRows.ReplaceAll(_resultRows.Where(r => ResultFilter switch
        {
            ResultFilter.Succeeded => r.Status == ObjectResultStatus.Succeeded,
            ResultFilter.Failed => r.IsFailure,
            ResultFilter.Skipped => r.Status == ObjectResultStatus.Skipped,
            _ => true,
        }));
    }

    private void UpdateCounts()
    {
        ObjectCount = _previewObjects.Count;
        ChangeCount = _previewObjects.Count(o => o.CanToggle && o.IsIncluded);
        ExcludedCount = _previewObjects.Count(o => o.CanToggle && !o.IsIncluded);
        WarningCount = _previewObjects.Count(o => !o.HasErrors && o.HasWarnings);
        ErrorCount = _previewObjects.Count(o => o.HasErrors);
        NoChangeCount = _previewObjects.Count(o => o.IsNoOp);
        PreviewSummary = Localizer.Format(L.Batch.PreviewSummary, ObjectCount, ChangeCount, NoChangeCount, ErrorCount, WarningCount, ExcludedCount);
    }

    private void OnPreviewObjectChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreviewObjectViewModel.IsIncluded))
        {
            UpdateCounts();
        }
    }

    partial void OnCurrentActivityChanged(ActivityViewModel? oldValue, ActivityViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnActivityChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnActivityChanged;
        }
    }

    private void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ActivityViewModel.IsRunning) or nameof(ActivityViewModel.IsCancelling))
        {
            CancelExecutionCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Asks for a file and writes it; true when the file was written.</summary>
    private async Task<bool> ExportAsync(string kind, Action<TextWriter> write)
    {
        var title = Localizer[kind == "preview" ? L.Batch.ExportPreview : L.Batch.ExportResults];
        var name = $"{kind}-{Services.Time.GetLocalNow():yyyyMMdd-HHmm}.csv";
        var path = await Services.Files.SaveFileAsync(title, name, [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (path is null)
        {
            return false;
        }

        try
        {
            await CsvFiles.WriteAsync(path, write).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Batch.Exported, Path.GetFileName(path)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
            return false;
        }
    }

    private void MoveOperation(OperationEditorViewModel? editor, int delta)
    {
        if (editor is null)
        {
            return;
        }

        var index = Operations.IndexOf(editor);
        var target = index + delta;
        if (index >= 0 && target >= 0 && target < Operations.Count)
        {
            Operations.Move(index, target);
        }
    }

    private void OnOperationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (OperationEditorViewModel editor in e.OldItems)
            {
                editor.Changed -= OnOperationChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (OperationEditorViewModel editor in e.NewItems)
            {
                editor.Changed += OnOperationChanged;
                if (editor is AttributeOperationEditorViewModel attributeEditor)
                {
                    attributeEditor.AttributeOptions = AttributeOptions;
                }
            }
        }

        OperationsError = ValidateOperations();
        PreviewCommand.NotifyCanExecuteChanged();
    }

    private void OnOperationChanged(object? sender, EventArgs e)
    {
        OperationsError = ValidateOperations();
        PreviewCommand.NotifyCanExecuteChanged();
    }

    private void OnCsvImportChanged(object? sender, EventArgs e)
    {
        PreviewCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ChangeTitle));
    }

    private string? ValidateOperations()
    {
        if (Operations.Count > 1 && Operations.Any(o => o.Type == OperationType.Delete))
        {
            return Localizer[L.Batch.DeleteAlone];
        }

        foreach (var type in new[] { OperationType.Move, OperationType.Rename, OperationType.ResetPassword })
        {
            if (Operations.Count(o => o.Type == type) > 1)
            {
                return Localizer.Format(L.Batch.OnlyOnce, Localizer[DisplayNames.OperationTypeKey(type)]);
            }
        }

        return null;
    }

    private async Task LoadTargetsAsync()
    {
        var session = Session;
        if (session is null)
        {
            Error = NoSessionMessage;
            return;
        }

        IsLoadingTargets = true;
        try
        {
            var dns = _request.TargetDns;
            var found = await Task.Run(() => DirectoryLookup.ReadManyAsync(session, dns, DirectoryItem.Attributes, CancellationToken.None))
                .ConfigureAwait(true);
            var items = dns.Select(dn => found.TryGetValue(DnKey.For(dn), out var obj) ? DirectoryItem.From(obj, Localizer) : DirectoryItem.Unresolved(dn, Localizer)).ToList();
            Targets.ReplaceAll(items);
            TargetSummary = string.Join(", ", items
                .GroupBy(i => i.Kind)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{Localizer.ObjectKindName(g.Key)}: {g.Count().ToString(Localizer.Culture)}"));
            var kinds = items.Select(i => i.Kind).Where(k => k != ObjectKind.Unknown).Distinct().ToList();
            AttributeOptions = AttributeOption.ForKinds(session.Schema, Localizer, kinds.Count > 0 ? kinds : [ObjectKind.User]);
            foreach (var editor in Operations.OfType<AttributeOperationEditorViewModel>())
            {
                editor.AttributeOptions = AttributeOptions;
                editor.Revalidate();
            }
        }
        catch (DirectoryException ex)
        {
            Error = Errors.Translate(ex);
        }
        finally
        {
            IsLoadingTargets = false;
        }
    }

    /// <summary>Renders a template for the first target (attributes are read on demand, once per new attribute).</summary>
    private async Task<string?> RenderSampleAsync(string template, CancellationToken cancellationToken)
    {
        var session = Session;
        if (session is null || _request.TargetDns.Count == 0)
        {
            return null;
        }

        var parsed = _engines.Templates.Parse(template);
        if (!parsed.IsValid)
        {
            return null;
        }

        try
        {
            var needed = parsed.ReferencedAttributes.Append(Attr.ObjectClass).Append(Attr.DisplayName).ToList();
            if (_sampleTarget is null || !needed.All(_sampleAttributes.Contains))
            {
                // The cache only claims the attributes once a read with them completed (a superseded read must
                // not leave attributes marked as read that the cached object does not have).
                var wanted = new HashSet<string>(_sampleAttributes, StringComparer.OrdinalIgnoreCase);
                wanted.UnionWith(needed);
                var dn = _request.TargetDns[0];
                string[] attributes = [.. wanted];
                var read = await Task.Run(() => session.GetAsync(dn, attributes, cancellationToken), cancellationToken).ConfigureAwait(true);
                if (read is null)
                {
                    return null;
                }

                _sampleTarget = read;
                _sampleAttributes.Clear();
                _sampleAttributes.UnionWith(wanted);
            }

            if (_sampleTarget is not { } target)
            {
                return null;
            }

            return _engines.Templates.Render(template, new TemplateContext
            {
                Attribute = target.GetStrings,
                DistinguishedName = target.DistinguishedName,
                Domain = session.Info.DomainDnsName,
                Counter = 1,
                Now = Services.Time.GetLocalNow(),
            });
        }
        catch (Exception ex) when (ex is FormatException or DirectoryException)
        {
            return null;
        }
    }
}
