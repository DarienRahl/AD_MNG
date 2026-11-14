using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.Diagnostics;

/// <summary>
/// "Diagnostyka zasad grupy" (K6, design note docs/dev/k-computers-and-gpo.md 20.2): the logical errors and weak security
/// settings of every GPO of the window's domain, one tab per connection window. "Szybkie sprawdzenie" (LDAP and GPT.INI)
/// runs when the tab opens; "Pełne sprawdzenie" also reads the SYSVOL folders, the folder ACLs, the users and computers
/// below the links, the SIDs and the conflicts. Progress, cancel, summary chips per severity, filters (severity, area, GPO,
/// text, Ctrl+F), a virtualized list and the details of the selected finding with its repair ("Napraw…" through the change
/// plan preview, rule 5 - a successful repair checks again at the same depth), "Otwórz GPO", "Edytuj to ustawienie",
/// "Pokaż w modelu" and "Kopiuj"; export to CSV or HTML into a file the operator picks. Everything is read on the session's
/// DC and its SYSVOL (rule 4) and kept in memory only while the tab is open (rule 1). A change of Group Policy made
/// elsewhere marks the results as stale instead of starting a (possibly long) check by itself.
/// </summary>
/// <remarks>
/// <b>What the tab says</b> (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built"): a check is the
/// operation of the document - "Reading SYSVOL folders: 3 of 8" with Stop in the status line, where the buttons of the
/// tab are its action buttons -, the summary is the number of findings with when and how deep the GPOs were checked, and
/// "Group Policy has changed since the last check" is the state of the tab that lasts, with <i>Check again</i>. What the
/// check could not evaluate (<see cref="Notes"/>) is a note above the findings: it is about how complete they are.
/// "Napraw…" is a plan with the preview; it speaks through the line of the document while its plan is made, and the
/// check after it is the operation of the document again.
/// </remarks>
public sealed partial class GroupPolicyDiagnosticsDocumentViewModel : GroupPolicyDocumentBase, ISearchableDocument, IShellDocument
{
    private readonly ICsvCodec _csv;
    private readonly GpoQuickFixRunner _fixes;

    /// <summary>The check the next load runs; null: a load with results shown only marks them stale (without results: a quick check).</summary>
    private GpoDiagnosticsDepth? _requested;

    private CancellationTokenSource? _running;
    private CheckResult? _check;

    /// <summary>The check that was stopped or failed: "Refresh" and "Retry" of the status line ask for it again, nothing else does.</summary>
    private GpoDiagnosticsDepth? _interrupted;

    /// <summary>The check that runs (or was asked for last) is the full one: <see cref="IsFullChecking"/>.</summary>
    private bool _fullCheck;

    /// <summary>The selection to restore after the next check (the key of the row, and its index as a fallback).</summary>
    private (string Key, int Index)? _restoreSelection;

    public GroupPolicyDiagnosticsDocumentViewModel(GroupPolicyServices services, GroupPolicyActions actions, ICsvCodec? csv = null)
        : base(services, actions)
    {
        _csv = csv ?? new CsvCodec();
        _fixes = new GpoQuickFixRunner(Actions);
        SeverityOptions = [];
        AreaOptions = [];
        GpoOptions = [];
        Chips = [];
        BuildFilterOptions();
        LoadedText = Localizer[L.GpoDiagnostics.NeverRun];
        LastRunText = LoadedText;
    }

    public override string Title => Localizer[L.GpoDiagnostics.Title];

    public override ObjectIcon Icon => ObjectIcon.GroupPolicy;

    /// <summary>The progress while a check runs, else the summary of the last one.</summary>
    public override string StatusText => Error ?? (IsLoading ? ProgressText : LoadedText);

    public override string SelectionText => SelectedRow is { } row ? row.Title : string.Empty;

    /// <summary>When and how deep the GPOs were checked, after the number of findings ("Ostatnie sprawdzenie: pełne, 3.11.2026 14:32").</summary>
    protected override string SummaryDetail => LastRunText;

    /// <summary>"Pełne sprawdzenie" is what runs: its action button shows Cancel (the other one is disabled meanwhile).</summary>
    public bool IsFullChecking => IsLoading && _fullCheck;

    /// <summary>Any other check runs ("Szybkie sprawdzenie", the first one, one after a repair): the action button of "Szybkie sprawdzenie" shows Cancel.</summary>
    public bool IsQuickChecking => IsLoading && !_fullCheck;

    // ---- Check state --------------------------------------------------------------------------------------------------------

    /// <summary>"Odczyt folderów SYSVOL: 3 z 8" while a check runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string ProgressText { get; private set; } = string.Empty;

    /// <summary>The progress of the current stage in percent (0 while it is not known: <see cref="IsProgressIndeterminate"/>).</summary>
    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; } = true;

    /// <summary>"Ostatnie sprawdzenie: pełne, 3.11.2026 14:32" (or "Jeszcze nie sprawdzono.").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryDetail))]
    public partial string LastRunText { get; private set; } = string.Empty;

    /// <summary>The depth of the shown results (null before the first check).</summary>
    [ObservableProperty]
    public partial GpoDiagnosticsDepth? LastDepth { get; private set; }

    /// <summary>Group Policy changed in this window after the shown check ("wyniki mogą być nieaktualne").</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; }

    /// <summary>"Group Policy has changed since the last check - the results may be out of date": the state of the tab that lasts, with "Sprawdź ponownie" as its action.</summary>
    protected override PageStatus? DocumentLastingState => IsStale
        ? PageStatus.Attention(Localizer[L.GpoDiagnostics.Stale], new PageStatusLog(L.GpoDiagnostics.Stale), new PageStatusAction(Localizer[L.GpoDiagnostics.CheckAgain], CheckAgainCommand))
        : null;

    partial void OnIsStaleChanged(bool value) => UpdateLasting();

    /// <summary>What the shown check could not evaluate: the rule groups not evaluated (and why) and the folders not read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial IReadOnlyList<string> Notes { get; private set; } = [];

    public bool HasNotes => Notes.Count > 0;

    /// <summary>A check result is shown (the exports and the chips have something to work on).</summary>
    public bool HasResult => _check is not null;

    /// <summary>The number of findings of the shown check (before the filters).</summary>
    public int TotalCount => _check?.Rows.Count ?? 0;

    // ---- Loading --------------------------------------------------------------------------------------------------------------

    protected override async Task LoadCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken)
    {
        // A superseded load can arrive here late (its SYSVOL logon does not stop once started): it must not touch the newer
        // check's state (its cancellation source, progress and "Anuluj").
        cancellationToken.ThrowIfCancellationRequested();

        // Nothing shown yet (the first load, a reconnect after "not connected"): a quick check. Results shown and Group Policy
        // changed (another tab, another DC): they are kept but marked, a full check is not started behind the operator's back.
        var depth = _requested ?? (_check is null ? GpoDiagnosticsDepth.Quick : (GpoDiagnosticsDepth?)null);
        if (depth is null)
        {
            IsStale = true;
            return;
        }

        // The check is the operation of the document (the base began it): its stages go to the status line.
        var operation = LoadOperation;
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = running;
        RefreshCommands();
        var token = running.Token;
        try
        {
            ReportProgress(new GpoDiagnosticsProgress(GpoDiagnosticsStage.Directory));
            var progress = Gp.Dispatcher.CreateProgress<GpoDiagnosticsProgress>(p =>
            {
                if (ReferenceEquals(_running, running))
                {
                    ReportProgress(p);
                }
            });
            var request = new GpoDiagnosticsRequest(null, depth.Value);
            var (snapshot, result) = await Task.Run(async () =>
            {
                var read = await GroupPolicyDiagnosticsReader.ReadAsync(session, sysvol, request, progress, token).ConfigureAwait(false);
                return (read, GroupPolicyDiagnostics.Analyze(read));
            }, token).ConfigureAwait(true);
            ProgressText = Localizer[L.GpoDiagnostics.NamesStage];
            operation?.Report(ProgressText, PageProgress.Indeterminate, new PageStatusLog(L.GroupPolicyStatus.Reading));
            var names = await Task.Run(() => GpoFindingRows.ResolveNamesAsync(session, result, Localizer, token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            _requested = null;
            _interrupted = null;
            Show(new CheckResult(snapshot, result, names, depth.Value, Gp.Time.GetLocalNow(), sysvol is null ? SysvolWarning : null, session.Info.Server));
        }
        catch (OperationCanceledException) when (operation is { IsCancelling: true })
        {
            // The operator's Stop: the shown results (if any) stay, the status bar of a host without a line says the check was
            // cancelled, and the base says the state of the tab. "Refresh" of the line repeats the check (RefreshAsync).
            _requested = null;
            _interrupted = depth;
            _restoreSelection = null;
            LoadedText = Localizer[L.GpoDiagnostics.Cancelled];
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The check failed (the base says why, with Retry): Retry repeats it (RefreshAsync).
            _requested = null;
            _interrupted = depth;
            _restoreSelection = null;
            throw;
        }
        finally
        {
            if (ReferenceEquals(_running, running))
            {
                // The progress is this check's only while no newer check runs.
                _running = null;
                ProgressText = string.Empty;
                ProgressValue = 0;
                IsProgressIndeterminate = true;
            }

            RefreshCommands();
        }
    }

    protected override void OnNotConnected()
    {
        _fullCheck = false;
        _check = null;
        _all = [];
        IsStale = false;
        Notes = [];
        ApplyFilters();
        UpdateSummary();
    }

    /// <summary>The texts are rebuilt from the shown check in the new language (nothing is read again, so stale results stay marked).</summary>
    protected override void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Title));
        BuildFilterOptions();
        UpdateLasting();
        if (_check is { } check)
        {
            Show(check, keepSelection: true, fresh: false);
        }
        else
        {
            LoadedText = Localizer[L.GpoDiagnostics.NeverRun];
        }
    }

    protected override void OnLoadingChanged()
    {
        if (!IsLoading)
        {
            _fullCheck = false;
        }

        OnPropertyChanged(nameof(IsFullChecking));
        OnPropertyChanged(nameof(IsQuickChecking));
        RefreshCommands();
    }

    protected override void RefreshCommands()
    {
        QuickCheckCommand.NotifyCanExecuteChanged();
        FullCheckCommand.NotifyCanExecuteChanged();
        CheckAgainCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        FixCommand.NotifyCanExecuteChanged();
        OpenGpoCommand.NotifyCanExecuteChanged();
        EditSettingCommand.NotifyCanExecuteChanged();
        ShowInModelCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
    }

    /// <summary>F5 checks again at the depth of the shown results (quick before the first check).</summary>
    Task IShellDocument.RefreshAsync() => CheckAsync(LastDepth ?? GpoDiagnosticsDepth.Quick);

    /// <summary>"Szybkie sprawdzenie": LDAP and GPT.INI.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task QuickCheckAsync() => CheckAsync(GpoDiagnosticsDepth.Quick);

    /// <summary>"Pełne sprawdzenie": also the SYSVOL folders and settings, the object counts, the SIDs and the conflicts.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task FullCheckAsync() => CheckAsync(GpoDiagnosticsDepth.Full);

    /// <summary>"Sprawdź ponownie" of the stale bar: the same depth as the shown results.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAgainAsync() => CheckAsync(LastDepth ?? GpoDiagnosticsDepth.Quick);

    /// <summary>
    /// The Stop of the status line: stops the running check (the shown results stay). The buttons of the tab are action
    /// buttons over the same operation; this command is kept for code that stops the check without the line.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel() => LoadOperation?.Cancel();

    /// <summary>Runs a check of <paramref name="depth"/> (a running one is replaced).</summary>
    public Task CheckAsync(GpoDiagnosticsDepth depth)
    {
        Request(depth);
        if (SelectedRow is { } selected)
        {
            _restoreSelection ??= (selected.Key, IndexOf(Rows, selected));
        }

        return LoadAsync();
    }

    /// <summary>
    /// "Refresh" and "Retry" of the status line: a check that was stopped or failed is run again at its depth (a plain reload
    /// only marks the shown results stale, and checks nothing).
    /// </summary>
    public override Task RefreshAsync()
    {
        if (_interrupted is { } again)
        {
            Request(again);
        }

        return base.RefreshAsync();
    }

    /// <summary>The next load runs a check of <paramref name="depth"/>; the buttons know which check runs.</summary>
    private void Request(GpoDiagnosticsDepth depth)
    {
        _requested = depth;
        _interrupted = null;
        _fullCheck = depth == GpoDiagnosticsDepth.Full;
        OnPropertyChanged(nameof(IsFullChecking));
        OnPropertyChanged(nameof(IsQuickChecking));
    }

    private bool CanCheck() => HasSession && !IsLoading;

    private bool CanCancel() => IsLoading && _running is not null;

    private void ReportProgress(GpoDiagnosticsProgress progress)
    {
        ProgressText = GpoFindingText.Stage(Localizer, progress);
        IsProgressIndeterminate = progress.Total <= 0;
        ProgressValue = progress.Total > 0 ? Math.Clamp(100.0 * progress.Done / progress.Total, 0, 100) : 0;

        // The status line says the same: "Reading SYSVOL folders: 3 of 8", the ring showing the part done.
        LoadOperation?.Report(
            ProgressText,
            progress.Total > 0 ? PageProgress.Of(progress.Done, progress.Total) : PageProgress.Indeterminate,
            new PageStatusLog(L.GroupPolicyStatus.Reading, progress.Done, progress.Total));
    }

    /// <summary>
    /// Shows a check: rows, chips, filters, notes, the last-run line and the status text. <paramref name="fresh"/>: the check
    /// was just read (it clears "wyniki mogą być nieaktualne"); false when the shown check is only rebuilt (a language change).
    /// </summary>
    private void Show(CheckResult check, bool keepSelection = false, bool fresh = true)
    {
        var rows = new GpoFindingRows(Localizer, check.Snapshot, check.Names);
        var restore = keepSelection && SelectedRow is { } selected ? (selected.Key, IndexOf(Rows, selected)) : _restoreSelection;
        _restoreSelection = null;
        _check = check with { Rows = rows.Rows(check.Result) };
        _all = _check.Rows;
        LastDepth = check.Depth;
        if (fresh)
        {
            IsStale = false;
        }

        LastRunText = Localizer.Format(L.GpoDiagnostics.LastRun,
            Localizer[check.Depth == GpoDiagnosticsDepth.Full ? L.GpoDiagnostics.DepthFull : L.GpoDiagnostics.DepthQuick],
            check.At.ToString("g", Localizer.Culture));
        Notes = NotesOf(check, rows);
        BuildGpoOptions();
        UpdateSummary();
        ApplyFilters(restore);
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(TotalCount));
        RefreshCommands();
    }

    /// <summary>What the check could not evaluate, in the operator's words.</summary>
    private List<string> NotesOf(CheckResult check, GpoFindingRows rows)
    {
        var notes = new List<string>();
        var groups = check.Result.NotEvaluated.Where(g => g != GpoRuleGroup.Directory).ToList();
        var sysvolGroups = new[] { GpoRuleGroup.SysvolVersions, GpoRuleGroup.Folders, GpoRuleGroup.Conflicts };
        string List(IEnumerable<GpoRuleGroup> list) => string.Join(", ", list.Select(g => GpoFindingText.Group(Localizer, g)));
        if (check.SysvolWarning is { } warning)
        {
            var missing = groups.Where(g => sysvolGroups.Contains(g) && (g == GpoRuleGroup.SysvolVersions || check.Depth == GpoDiagnosticsDepth.Full)).ToList();
            if (missing.Count > 0)
            {
                notes.Add(Localizer.Format(L.GpoDiagnostics.NoteSysvol, List(missing), warning));
            }

            groups.RemoveAll(missing.Contains);
        }

        if (check.Depth == GpoDiagnosticsDepth.Quick)
        {
            if (groups.Count > 0)
            {
                notes.Add(Localizer.Format(L.GpoDiagnostics.NoteQuick, List(groups)));
            }
        }
        else if (groups.Count > 0)
        {
            notes.Add(Localizer.Format(L.GpoDiagnostics.NoteFailed, List(groups)));
        }

        if (check.Result.FoldersNotRead.Count > 0)
        {
            notes.Add(Localizer.Format(L.GpoDiagnostics.NoteFoldersNotRead, string.Join(", ", check.Result.FoldersNotRead.Select(id =>
                rows.GpoName(id, null) + " (" + GpoFindingText.FolderStatus(Localizer, check.Snapshot.Folders.GetValueOrDefault(id)?.Status) + ")"))));
        }

        return notes;
    }

    /// <summary>One check as shown: the snapshot and result (in memory only), the names of its SIDs, its depth and time.</summary>
    private sealed record CheckResult(
        GpoDiagnosticsSnapshot Snapshot,
        GpoDiagnosticsResult Result,
        IReadOnlyDictionary<string, string> Names,
        GpoDiagnosticsDepth Depth,
        DateTimeOffset At,
        string? SysvolWarning,
        string Server)
    {
        public IReadOnlyList<GpoFindingRowViewModel> Rows { get; init; } = [];

        /// <summary>For diagnostics: counts only.</summary>
        public override string ToString() => $"{Depth}: {Result}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _running?.Cancel();
            _check = null;
            _all = [];
        }

        base.Dispose(disposing);
    }
}
