using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Diagnostics;
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
public sealed partial class GroupPolicyDiagnosticsDocumentViewModel : GroupPolicyDocumentBase, ISearchableDocument, IShellDocument
{
    private readonly ICsvCodec _csv;
    private readonly GpoQuickFixRunner _fixes;

    /// <summary>The check the next load runs; null: a load with results shown only marks them stale (without results: a quick check).</summary>
    private GpoDiagnosticsDepth? _requested;

    private CancellationTokenSource? _running;
    private bool _cancelRequested;
    private CheckResult? _check;

    /// <summary>The selection to restore after the next check (the key of the row, and its index as a fallback).</summary>
    private (string Key, int Index)? _restoreSelection;

    public GroupPolicyDiagnosticsDocumentViewModel(GroupPolicyServices services, GroupPolicyActions actions, ICsvCodec? csv = null)
        : base(services, actions)
    {
        _csv = csv ?? new CsvCodec();
        _fixes = new GpoQuickFixRunner(actions);
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
    public partial string LastRunText { get; private set; } = string.Empty;

    /// <summary>The depth of the shown results (null before the first check).</summary>
    [ObservableProperty]
    public partial GpoDiagnosticsDepth? LastDepth { get; private set; }

    /// <summary>Group Policy changed in this window after the shown check ("wyniki mogą być nieaktualne").</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; }

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
        // Nothing shown yet (the first load, a reconnect after "not connected"): a quick check. Results shown and Group Policy
        // changed (another tab, another DC): they are kept but marked, a full check is not started behind the operator's back.
        var depth = _requested ?? (_check is null ? GpoDiagnosticsDepth.Quick : (GpoDiagnosticsDepth?)null);
        if (depth is null)
        {
            IsStale = true;
            return;
        }

        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = running;
        _cancelRequested = false;
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
            var names = await Task.Run(() => GpoFindingRows.ResolveNamesAsync(session, result, Localizer, token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            _requested = null;
            Show(new CheckResult(snapshot, result, names, depth.Value, Gp.Time.GetLocalNow(), sysvol is null ? SysvolWarning : null, session.Info.Server));
        }
        catch (OperationCanceledException) when (_cancelRequested && !cancellationToken.IsCancellationRequested)
        {
            // "Anuluj": the shown results (if any) stay; the status says the check was cancelled.
            _requested = null;
            _restoreSelection = null;
            LoadedText = Localizer[L.GpoDiagnostics.Cancelled];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _requested = null;
            _restoreSelection = null;
            throw;
        }
        finally
        {
            if (ReferenceEquals(_running, running))
            {
                _running = null;
            }

            ProgressText = string.Empty;
            ProgressValue = 0;
            IsProgressIndeterminate = true;
            RefreshCommands();
        }
    }

    protected override void OnNotConnected()
    {
        _check = null;
        _all = [];
        IsStale = false;
        Notes = [];
        ApplyFilters();
        UpdateSummary();
    }

    /// <summary>The texts are rebuilt from the shown check in the new language (nothing is read again).</summary>
    protected override void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Title));
        BuildFilterOptions();
        if (_check is { } check)
        {
            Show(check, keepSelection: true);
        }
        else
        {
            LoadedText = Localizer[L.GpoDiagnostics.NeverRun];
        }
    }

    protected override void OnLoadingChanged() => RefreshCommands();

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

    /// <summary>"Anuluj": stops the running check (the shown results stay).</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        if (_running is { } running)
        {
            _cancelRequested = true;
            running.Cancel();
        }
    }

    /// <summary>Runs a check of <paramref name="depth"/> (a running one is replaced).</summary>
    public Task CheckAsync(GpoDiagnosticsDepth depth)
    {
        _requested = depth;
        if (SelectedRow is { } selected)
        {
            _restoreSelection ??= (selected.Key, IndexOf(Rows, selected));
        }

        return LoadAsync();
    }

    private bool CanCheck() => HasSession && !IsLoading;

    private bool CanCancel() => IsLoading && _running is not null;

    private void ReportProgress(GpoDiagnosticsProgress progress)
    {
        ProgressText = GpoFindingText.Stage(Localizer, progress);
        IsProgressIndeterminate = progress.Total <= 0;
        ProgressValue = progress.Total > 0 ? Math.Clamp(100.0 * progress.Done / progress.Total, 0, 100) : 0;
    }

    /// <summary>Shows a check: rows, chips, filters, notes, the last-run line and the status text.</summary>
    private void Show(CheckResult check, bool keepSelection = false)
    {
        var rows = new GpoFindingRows(Localizer, check.Snapshot, check.Names);
        var restore = keepSelection && SelectedRow is { } selected ? (selected.Key, IndexOf(Rows, selected)) : _restoreSelection;
        _restoreSelection = null;
        _check = check with { Rows = rows.Rows(check.Result) };
        _all = _check.Rows;
        LastDepth = check.Depth;
        IsStale = false;
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
