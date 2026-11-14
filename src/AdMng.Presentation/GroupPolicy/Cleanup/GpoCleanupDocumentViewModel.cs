using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Cleanup;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.Cleanup;

/// <summary>
/// "Porządki w GPO" (K7, design note docs/dev/k-computers-and-gpo.md 21.6): the cleanup advisor of the window's domain, one
/// tab per connection window. The window's settings index (<see cref="IGpoSettingsIndexService"/>, read on the session's
/// DC and its SYSVOL, rule 4) is analysed by <see cref="GpoCleanupAnalyzer"/>; the proposals are listed with chips per kind,
/// filters (kind, GPO, text, Ctrl+F), a virtualized list and the details of the selected one with its actions – "Usuń
/// GPO…" (guarded by what the analysis saw, backup first), "Odłącz wszędzie…", "Wyłącz GPO", "Kopiuj do innego GPO…",
/// "Usuń duplikat z tego GPO", "Scal z…" – each a plan with preview and undo (rule 5); a successful action refreshes the
/// index incrementally and analyses again. Everything stays in the window's memory (rule 1); exports go only to files the
/// operator picks. A change of Group Policy made elsewhere marks the results stale instead of reading every GPO again.
/// </summary>
/// <remarks>
/// <b>What the tab says</b> (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built"): the read and the
/// analysis are the operation of the document - "Reading GPO folders: 12 of 300…", "Analysing the GPOs…" with Stop in the
/// status line, where the buttons of the tab are its action buttons -, the summary is the number of proposals with when
/// the GPOs were read, and "Group Policy changed after the analysis" is the state of the tab that lasts, with the button
/// that analyses again. What the analysis could not see (<see cref="Notes"/>) is a note above the proposals: it is about
/// how complete they are. The actions of a proposal are plans with the preview; they speak through the line of the
/// document while their plan is made, and the read after them is the operation of the document again.
/// </remarks>
public sealed partial class GpoCleanupDocumentViewModel : GroupPolicyDocumentBase, ISearchableDocument, IShellDocument
{
    private readonly IGpoSettingsIndexService _index;
    private readonly ICsvCodec _csv;

    /// <summary>The refresh the next load asks the index for; null: a load with results shown only marks them stale.</summary>
    private GpoIndexRefresh? _requested;

    private CancellationTokenSource? _running;
    private Analysis? _shown;

    /// <summary>The read that runs (or was asked for last) reads every folder again: <see cref="IsReadingAll"/>.</summary>
    private bool _readingAll;

    /// <summary>The selection to restore after the next analysis (the key of the row, and its index as a fallback).</summary>
    private (string Key, int Index)? _restoreSelection;

    public GpoCleanupDocumentViewModel(GroupPolicyServices services, GroupPolicyActions actions, IGpoSettingsIndexService index, ICsvCodec? csv = null)
        : base(services, actions)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _csv = csv ?? new CsvCodec();
        KindOptions = [];
        GpoOptions = [];
        Chips = [];
        BuildFilterOptions();
        LoadedText = Localizer[L.GpoCleanup.NeverRun];
        LastRunText = LoadedText;
        _index.StateChanged += OnIndexStateChanged;
    }

    public override string Title => Localizer[L.GpoCleanup.Title];

    public override ObjectIcon Icon => ObjectIcon.GroupPolicy;

    /// <summary>The progress while the index is read and analysed, else the summary of the last analysis.</summary>
    public override string StatusText => Error ?? (IsLoading ? ProgressText : LoadedText);

    /// <summary>When the GPOs were read, after the number of proposals ("Przeanalizowano obiektów GPO: 16, ustawienia odczytane 9.11.2026 14:32").</summary>
    protected override string SummaryDetail => LastRunText;

    /// <summary>"Odczytaj wszystko ponownie" is what reads: its action button shows Cancel (the other one is disabled meanwhile).</summary>
    public bool IsReadingAll => IsLoading && _readingAll;

    /// <summary>Any other read and analysis runs ("Odśwież", the first one, one after an action): the action button of "Odśwież" shows Cancel.</summary>
    public bool IsAnalyzing => IsLoading && !_readingAll;

    public override string SelectionText => SelectedRow is { } row ? row.Title + " – " + row.GpoName : string.Empty;

    /// <summary>The window's settings index.</summary>
    public IGpoSettingsIndexService Index => _index;

    // ---- Analysis state -----------------------------------------------------------------------------------------------

    /// <summary>"Odczyt folderów GPO: 12 z 480" while the index is read; "Analiza propozycji…" while it is analysed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string ProgressText { get; private set; } = string.Empty;

    /// <summary>The progress of the current stage in percent (0 while it is not known: <see cref="IsProgressIndeterminate"/>).</summary>
    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; } = true;

    /// <summary>"Analiza: 16 obiektów GPO, odczytane 9.11.2026 14:32" (or "Jeszcze nie przeanalizowano.").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryDetail))]
    public partial string LastRunText { get; private set; } = string.Empty;

    /// <summary>Group Policy changed after the shown analysis ("wyniki mogą być nieaktualne").</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; }

    /// <summary>"Group Policy changed after the analysis - the proposals may be out of date": the state of the tab that lasts, with the analysis as its action.</summary>
    protected override PageStatus? DocumentLastingState => IsStale
        ? PageStatus.Attention(Localizer[L.GpoCleanup.Stale], new PageStatusLog(L.GpoCleanup.Stale), new PageStatusAction(Localizer[L.GpoCleanup.Analyze], AnalyzeCommand))
        : null;

    /// <summary>What the analysis could not see: GPOs not read completely, no SYSVOL, no templates, other domains.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial IReadOnlyList<string> Notes { get; private set; } = [];

    public bool HasNotes => Notes.Count > 0;

    /// <summary>An analysis is shown.</summary>
    public bool HasResult => _shown is not null;

    /// <summary>The number of proposals of the shown analysis (before the filters).</summary>
    public int TotalCount => _shown?.Rows.Count ?? 0;

    /// <summary>The index view of the shown analysis (null before the first one).</summary>
    public GpoIndexView? View => _shown?.View;

    /// <summary>The proposals and not-evaluated GPOs of the shown analysis.</summary>
    public GpoCleanupResult? Result => _shown?.Result;

    // ---- Loading ------------------------------------------------------------------------------------------------------

    protected override async Task LoadCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken)
    {
        // A superseded load can arrive late (its SYSVOL logon does not stop once started): it must not touch the newer one.
        cancellationToken.ThrowIfCancellationRequested();

        // The analysis of another session (another DC, a reconnect) is dropped with the index (21.7): it is never shown,
        // nor acted on, as this session's.
        if (_shown is { } previous && !ReferenceEquals(previous.Session, session))
        {
            DropAnalysis();
        }

        // Nothing shown yet: the index as it is (built when missing). Results shown and Group Policy changed elsewhere: they
        // are kept but marked, the GPOs are not read again behind the operator's back.
        var refresh = _requested ?? (_shown is null ? GpoIndexRefresh.IfMissing : (GpoIndexRefresh?)null);
        if (refresh is null)
        {
            UpdateStale();
            return;
        }

        // The read and the analysis are the operation of the document (the base began it): their stages go to the status line.
        var operation = LoadOperation;
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = running;
        RefreshCommands();
        var token = running.Token;
        try
        {
            ReportProgress(_index.Progress ?? new GpoIndexProgress(GpoIndexStage.Directory));
            var view = await _index.GetAsync(refresh.Value, token).ConfigureAwait(true);
            ProgressText = Localizer[L.GpoCleanup.AnalyzeStage];
            IsProgressIndeterminate = true;
            operation?.Report(ProgressText, PageProgress.Indeterminate, new PageStatusLog(L.GroupPolicyStatus.Reading));
            var result = await Task.Run(() => GpoCleanupAnalyzer.Analyze(view.Index), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            _requested = null;
            Show(new Analysis(view, result) { Session = session });
        }
        catch (OperationCanceledException) when (operation is { IsCancelling: true })
        {
            // The operator's Stop: the shown results (if any) stay, the status bar of a host without a line says the analysis
            // was cancelled, and the base says the state of the tab. The read stays requested: "Refresh" of the line repeats it.
            _restoreSelection = null;
            LoadedText = Localizer[L.GpoCleanup.Cancelled];
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The read failed (the base says why, with Retry): it stays requested, so that Retry reads the index again.
            _restoreSelection = null;
            throw;
        }
        finally
        {
            if (ReferenceEquals(_running, running))
            {
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
        _readingAll = false;
        DropAnalysis();
    }

    /// <summary>Forgets the shown analysis (disconnected, or made on another session).</summary>
    private void DropAnalysis()
    {
        _shown = null;
        _all = [];
        _restoreSelection = null;
        IsStale = false;
        Notes = [];
        LastRunText = Localizer[L.GpoCleanup.NeverRun];
        ApplyFilters();
        UpdateSummary();
        BuildGpoOptions();
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(Result));
        RefreshCommands();
    }

    /// <summary>The texts are made again in the new language from the index (no GPO folder is read again).</summary>
    protected override void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Title));
        BuildFilterOptions();
        UpdateLasting();
        if (_shown is null)
        {
            LoadedText = Localizer[L.GpoCleanup.NeverRun];
            LastRunText = LoadedText;
            return;
        }

        _requested ??= GpoIndexRefresh.IfMissing;
        RememberSelection();
        LoadAsync().Forget();
    }

    protected override void OnLoadingChanged()
    {
        if (!IsLoading)
        {
            _readingAll = false;
        }

        OnPropertyChanged(nameof(IsReadingAll));
        OnPropertyChanged(nameof(IsAnalyzing));
        RefreshCommands();
    }

    partial void OnIsStaleChanged(bool value)
    {
        UpdateLasting();
        RefreshCommands();
    }

    protected override void RefreshCommands()
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        ReadAllCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        UnlinkCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        RemoveSettingCommand.NotifyCanExecuteChanged();
        CopyToCommand.NotifyCanExecuteChanged();
        MergeCommand.NotifyCanExecuteChanged();
        OpenGpoCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        ShowInSearchCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
    }

    /// <summary>F5: the GPOs that changed are read again, then analysed.</summary>
    Task IShellDocument.RefreshAsync() => AnalyzeAsync(GpoIndexRefresh.Incremental);

    /// <summary>"Odśwież" (and the stale bar's button): the GPOs whose versions changed are read again, then analysed.</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task AnalyzeAsync() => AnalyzeAsync(GpoIndexRefresh.Incremental);

    /// <summary>"Odczytaj wszystko ponownie": every GPO folder is read again.</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task ReadAllAsync() => AnalyzeAsync(GpoIndexRefresh.Full);

    /// <summary>
    /// The Stop of the status line: this document stops waiting (the index build stops when no other document waits for it).
    /// The buttons of the tab are action buttons over the same operation; this command is kept for code that stops the read
    /// without the line.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel() => LoadOperation?.Cancel();

    /// <summary>Reads the index (<paramref name="refresh"/>) and analyses it; a running analysis is replaced.</summary>
    public Task AnalyzeAsync(GpoIndexRefresh refresh)
    {
        _requested = refresh;
        _readingAll = refresh == GpoIndexRefresh.Full;
        OnPropertyChanged(nameof(IsReadingAll));
        OnPropertyChanged(nameof(IsAnalyzing));
        RememberSelection();
        return LoadAsync();
    }

    private bool CanAnalyze() => HasSession && !IsLoading;

    private bool CanCancel() => IsLoading && _running is not null;

    private void RememberSelection()
    {
        if (SelectedRow is { } selected)
        {
            _restoreSelection ??= (selected.Key, IndexOf(Rows, selected));
        }
    }

    private void OnIndexStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        if (_running is not null && _index.Progress is { } progress)
        {
            ReportProgress(progress);
        }

        if (!IsLoading)
        {
            UpdateStale();
        }
    }

    /// <summary>The shown analysis is out of date: a GPO operation ran since its index was read, or the index was read again elsewhere.</summary>
    private void UpdateStale() => IsStale = _shown is { } shown
        && (_index.IsStale || _index.Current is null || !ReferenceEquals(_index.Current, shown.View));

    private void ReportProgress(GpoIndexProgress progress)
    {
        var stage = Localizer[L.GpoIndex.StageKey(progress.Stage)];
        ProgressText = progress.Total > 0 ? Localizer.Format(L.GpoIndex.Progress, stage, progress.Done, progress.Total) : stage;
        IsProgressIndeterminate = progress.Total <= 0;
        ProgressValue = progress.Total > 0 ? Math.Clamp(100.0 * progress.Done / progress.Total, 0, 100) : 0;

        // The status line says the same: "Reading GPO folders: 12 of 300", the ring showing the part done.
        LoadOperation?.Report(
            progress.Total > 0 ? ProgressText : stage + "…",
            progress.Total > 0 ? PageProgress.Of(progress.Done, progress.Total) : PageProgress.Indeterminate,
            new PageStatusLog(L.GroupPolicyStatus.Reading, progress.Done, progress.Total));
    }

    /// <summary>Shows an analysis: rows, chips, filters, notes, the last-run line and the status text.</summary>
    private void Show(Analysis analysis)
    {
        var texts = GpoIndexTexts.For(Localizer, analysis.View);
        var restore = _restoreSelection;
        _restoreSelection = null;
        _shown = analysis with { Rows = [.. analysis.Result.Proposals.Select(p => GpoCleanupRowViewModel.Create(p, texts))] };
        _all = _shown.Rows;
        UpdateStale();
        LastRunText = Localizer.Format(L.GpoCleanup.LastRun, analysis.View.Index.Entries.Count,
            analysis.View.Index.BuiltAt.ToLocalTime().ToString("g", Localizer.Culture));
        Notes = NotesOf(analysis);
        BuildGpoOptions();
        UpdateSummary();
        ApplyFilters(restore);
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(Result));
        RefreshCommands();
    }

    /// <summary>What the analysis could not see, in the operator's words.</summary>
    private List<string> NotesOf(Analysis analysis)
    {
        var notes = new List<string>();
        var view = analysis.View;
        var index = view.Index;
        if (view.SysvolReason is { } reason)
        {
            notes.Add(Localizer.Format(L.GpoIndex.NoteNoSysvol, Gp.SysvolText(reason, index.Server)));
        }

        if (analysis.Result.NotEvaluated.Count > 0)
        {
            var names = analysis.Result.NotEvaluated
                .Select(index.Find)
                .OfType<GpoIndexEntry>()
                .Select(e => e.Gpo.DisplayName + " (" + Localizer[L.GpoIndex.StatusKey(e.Status)] + ")");
            notes.Add(Localizer.Format(L.GpoCleanup.NoteNotEvaluated, analysis.Result.NotEvaluated.Count, string.Join(", ", names)));
        }

        if (view.DefinitionsFailed)
        {
            notes.Add(Localizer[L.GpoIndex.NoteDefinitionsFailed]);
        }
        else if (index.Definitions is null)
        {
            notes.Add(Localizer[L.GpoIndex.NoteNoDefinitions]);
        }

        if (index.OtherDomains.Count > 0)
        {
            notes.Add(Localizer.Format(L.GpoIndex.NoteOtherDomains, string.Join(", ", index.OtherDomains)));
        }

        return notes;
    }

    /// <summary>One analysis as shown: the index view and the result (in memory only) and the rows made of them.</summary>
    private sealed record Analysis(GpoIndexView View, GpoCleanupResult Result)
    {
        public IReadOnlyList<GpoCleanupRowViewModel> Rows { get; init; } = [];

        /// <summary>The session the index was read on (another one drops the analysis).</summary>
        public IDirectorySession? Session { get; init; }

        /// <summary>For diagnostics: counts only.</summary>
        public override string ToString() => Result.ToString();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _index.StateChanged -= OnIndexStateChanged;
            _running?.Cancel();
            _shown = null;
            _all = [];
        }

        base.Dispose(disposing);
    }
}
