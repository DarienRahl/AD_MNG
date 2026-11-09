using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.Search;

/// <summary>A note of the search (what the index could not read or search) with the details for its tool tip.</summary>
public sealed record GpoSearchNote(string Text, string? Detail = null)
{
    /// <summary>For diagnostics: lengths only (the details name GPOs).</summary>
    public override string ToString() => $"note ({Text.Length} characters)";
}

/// <summary>
/// "Wyszukiwarka ustawień GPO" (K7, docs/dev/k-computers-and-gpo.md 21.4): which GPOs of the domain set a setting, a value or
/// an account, in GPMC's words – one tab per connection window. It reads the window's settings index
/// (<see cref="IGpoSettingsIndexService"/>, built once and shared with "Porządki w GPO") with progress and "Anuluj" (the
/// shown results stay), F5 / "Odśwież" re-reads only the changed GPOs, "Odczytaj wszystko ponownie" every folder. A change of
/// Group Policy elsewhere in the window only marks the results stale. Matching runs off the UI thread with a 250 ms debounce
/// (<see cref="GpoSettingsMatcher"/>); results are grouped by setting with every GPO that sets it and whether their values
/// agree; an empty or one-character query shows a summary. Row actions open the GPO or the editor of the setting, compare two
/// GPOs, copy the settings into another GPO through the change plan (W5) and export the results to a file the operator picks.
/// Everything lives in the window's memory only (rule 1).
/// </summary>
public sealed partial class GpoSettingsSearchDocumentViewModel : GroupPolicyDocumentBase, ISearchableDocument, IShellDocument
{
    private readonly ICsvCodec _csv;

    /// <summary>The index read the next load asks for; null: a load with results shown only checks whether they are stale.</summary>
    private GpoIndexRefresh? _requested;

    private CancellationTokenSource? _running;
    private bool _cancelRequested;
    private GpoSearchCorpus? _corpus;

    public GpoSettingsSearchDocumentViewModel(GroupPolicyServices services, GroupPolicyActions actions, IGpoSettingsIndexService index,
        string? searchText = null, ICsvCodec? csv = null)
        : base(services, actions)
    {
        Index = index ?? throw new ArgumentNullException(nameof(index));
        _csv = csv ?? new CsvCodec();
        FieldOptions = [];
        ScopeOptions = [];
        AreaOptions = [];
        BuildOptions();
        SearchText = searchText?.Trim() ?? string.Empty;
        LoadedText = Localizer[L.GpoSearch.NeverRead];
        Index.StateChanged += OnIndexStateChanged;
    }

    /// <summary>The window's settings index (scoped, shared with the cleanup advisor).</summary>
    public IGpoSettingsIndexService Index { get; }

    public override string Title => Localizer[L.GpoSearch.Title];

    public override ObjectIcon Icon => ObjectIcon.GroupPolicy;

    /// <summary>The progress while the index is read, else the result of the query.</summary>
    public override string StatusText => Error ?? (IsLoading && ProgressText.Length > 0 ? ProgressText : LoadedText);

    public override string SelectionText => SelectedRow is { } row ? row.GpoName + " – " + row.Name : string.Empty;

    /// <summary>The index view the results come from (null before the first read).</summary>
    public GpoIndexView? View => _corpus?.View;

    /// <summary>The searchable records of <see cref="View"/>.</summary>
    public GpoSearchCorpus? Corpus => _corpus;

    /// <summary>Settings were read (the search and the exports have something to work on).</summary>
    public bool HasIndex => _corpus is not null;

    // ---- Index state --------------------------------------------------------------------------------------------------------

    /// <summary>"Odczyt folderów GPO: 3 z 8" while the index is read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; } = true;

    /// <summary>"Przeszukiwane obiekty GPO: 16 · odczyt 3.11.2026 14:32" (empty before the first read).</summary>
    [ObservableProperty]
    public partial string IndexInfoText { get; private set; } = string.Empty;

    /// <summary>Group Policy changed in this window after the shown read, or the index was dropped (another DC, reconnect).</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; }

    /// <summary>What the index could not read or search: GPOs not read, read in part, no SYSVOL, no ADMX, hidden values, other domains.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial IReadOnlyList<GpoSearchNote> Notes { get; private set; } = [];

    public bool HasNotes => Notes.Count > 0;

    // ---- Loading ------------------------------------------------------------------------------------------------------------

    protected override async Task LoadCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken)
    {
        // A superseded load can arrive late (its SYSVOL logon does not stop once started): it must not touch the newer read.
        cancellationToken.ThrowIfCancellationRequested();

        // Nothing shown yet (the first load, a reconnect after "not connected"): the index as it is (or a build). Results shown
        // and Group Policy changed (another tab, another DC): they are kept and marked, the index is not rebuilt behind the
        // operator's back.
        var refresh = _requested ?? (_corpus is null ? GpoIndexRefresh.IfMissing : (GpoIndexRefresh?)null);
        if (refresh is null)
        {
            UpdateStale();
            return;
        }

        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = running;
        _cancelRequested = false;
        RefreshCommands();
        var token = running.Token;
        try
        {
            ReportProgress(Index.Progress ?? new GpoIndexProgress(GpoIndexStage.Directory));
            var view = await Index.GetAsync(refresh.Value, token).ConfigureAwait(true);
            ProgressText = Localizer[L.GpoSearch.Preparing];
            IsProgressIndeterminate = true;
            var localizer = Localizer;
            var corpus = await Task.Run(() => GpoSearchCorpus.Build(view, GpoIndexTexts.For(localizer, view), token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            _requested = null;
            Show(corpus);
            await SearchCoreAsync(TimeSpan.Zero).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_cancelRequested && !cancellationToken.IsCancellationRequested)
        {
            // "Anuluj": the shown results (if any) stay; the status says the read was cancelled.
            _requested = null;
            _restoreSelection = null;
            LoadedText = Localizer[L.GpoSearch.Cancelled];
        }
        catch (ObjectDisposedException) when (IsDisposed || cancellationToken.IsCancellationRequested)
        {
            // The window closed while the index was read.
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
                ProgressText = string.Empty;
                ProgressValue = 0;
                IsProgressIndeterminate = true;
            }

            RefreshCommands();
        }
    }

    protected override void OnNotConnected()
    {
        _search.Cancel();
        _corpus = null;
        IsStale = false;
        Notes = [];
        IndexInfoText = string.Empty;
        ClearResults();
        OnPropertyChanged(nameof(HasIndex));
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(Corpus));
    }

    /// <summary>
    /// The texts follow the new language from memory: the index service re-renders the names and definitions of the view for
    /// the culture (no GPO folder is read) and the search runs again.
    /// </summary>
    protected override void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Title));
        BuildOptions();
        if (_corpus is not null)
        {
            _requested = GpoIndexRefresh.IfMissing;
            LoadAsync().Forget();
        }
        else
        {
            LoadedText = Localizer[L.GpoSearch.NeverRead];
        }
    }

    protected override void OnLoadingChanged() => RefreshCommands();

    protected override void RefreshCommands()
    {
        RefreshIndexCommand.NotifyCanExecuteChanged();
        ReadAllCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        OpenGpoCommand.NotifyCanExecuteChanged();
        EditSettingCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        CopyToCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
    }

    /// <summary>F5: re-reads the GPOs that changed since the shown read.</summary>
    Task IShellDocument.RefreshAsync() => ReadAsync(GpoIndexRefresh.Incremental);

    /// <summary>"Odśwież" (also of the stale bar): only the GPOs whose versions changed are read again.</summary>
    [RelayCommand(CanExecute = nameof(CanRead))]
    private Task RefreshIndexAsync() => ReadAsync(GpoIndexRefresh.Incremental);

    /// <summary>"Odczytaj wszystko ponownie": every GPO folder is read again.</summary>
    [RelayCommand(CanExecute = nameof(CanRead))]
    private Task ReadAllAsync() => ReadAsync(GpoIndexRefresh.Full);

    /// <summary>"Anuluj": this tab stops waiting; the build stops when no other tab waits for it. The shown results stay.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        if (_running is { } running)
        {
            _cancelRequested = true;
            running.Cancel();
        }
    }

    /// <summary>Reads the index (<paramref name="refresh"/>) and searches again; the selection stays on the same occurrence when it can.</summary>
    public Task ReadAsync(GpoIndexRefresh refresh)
    {
        _requested = refresh;
        if (SelectedRow is { } selected)
        {
            _restoreSelection ??= KeyOf(selected);
        }

        return LoadAsync();
    }

    private bool CanRead() => HasSession && !IsLoading;

    private bool CanCancel() => IsLoading && _running is not null;

    private void ReportProgress(GpoIndexProgress progress)
    {
        var stage = Localizer[L.GpoIndex.StageKey(progress.Stage)];
        ProgressText = progress.Total > 0 ? Localizer.Format(L.GpoIndex.Progress, stage, progress.Done, progress.Total) : stage + "…";
        IsProgressIndeterminate = progress.Total <= 0;
        ProgressValue = progress.Total > 0 ? Math.Clamp(100.0 * progress.Done / progress.Total, 0, 100) : 0;
    }

    /// <summary>The index service's progress and staleness (raised on the UI dispatcher; the progress is shown while this tab waits).</summary>
    private void OnIndexStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        if (_running is not null && Index.Progress is { } progress)
        {
            ReportProgress(progress);
        }

        UpdateStale();
    }

    /// <summary>Stale: the index changed after the shown read, or the view was replaced or dropped (another DC, reconnect).</summary>
    private void UpdateStale() => IsStale = _corpus is { } corpus && (Index.IsStale || !ReferenceEquals(Index.Current, corpus.View));

    /// <summary>Shows a read: the notes, the facets of its preference types, the read line; the search follows.</summary>
    private void Show(GpoSearchCorpus corpus)
    {
        _corpus = corpus;
        var view = corpus.View;
        IndexInfoText = Localizer.Format(L.GpoSearch.IndexInfo, view.Index.Entries.Count,
            TimeZoneInfo.ConvertTime(view.Index.BuiltAt, TimeZoneInfo.Local).ToString("g", Localizer.Culture));
        Notes = NotesOf(corpus);
        SummaryLines = SummaryOf(corpus);
        BuildAreaOptions();
        UpdateStale();
        OnPropertyChanged(nameof(HasIndex));
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(Corpus));
        RefreshCommands();
    }

    /// <summary>What the index could not read or search, in the operator's words (names of the GPOs in the tool tips).</summary>
    private List<GpoSearchNote> NotesOf(GpoSearchCorpus corpus)
    {
        var view = corpus.View;
        var index = view.Index;
        var notes = new List<GpoSearchNote>();
        var compare = StringComparer.Create(Localizer.Culture, ignoreCase: true);
        string Names(IEnumerable<GpoIndexEntry> entries) => string.Join(Environment.NewLine, entries
            .OrderBy(e => e.Gpo.DisplayName, compare)
            .Select(e => Localizer.Format(L.GpoSearch.NoteGpo, e.Gpo.DisplayName, Localizer[L.GpoIndex.StatusKey(e.Status)])));

        if (!index.SysvolRead)
        {
            var reason = view.SysvolReason is { } why ? Gp.SysvolText(why, index.Server) : Localizer[L.GpoIndex.StatusKey(GpoIndexStatus.NotRead)];
            notes.Add(new GpoSearchNote(Localizer.Format(L.GpoIndex.NoteNoSysvol, reason)));
        }
        else
        {
            var notRead = index.Entries.Where(e => e.Status is not (GpoIndexStatus.Read or GpoIndexStatus.Incomplete)).ToList();
            if (notRead.Count > 0)
            {
                notes.Add(new GpoSearchNote(Localizer.Format(L.GpoIndex.NoteNotRead, notRead.Count), Names(notRead)));
            }
        }

        var incomplete = index.Entries.Where(e => e.Status == GpoIndexStatus.Incomplete).ToList();
        if (incomplete.Count > 0)
        {
            notes.Add(new GpoSearchNote(Localizer.Format(L.GpoIndex.NoteIncomplete, incomplete.Count), Names(incomplete)));
        }

        if (view.DefinitionsFailed)
        {
            notes.Add(new GpoSearchNote(Localizer[L.GpoSearch.NoteDefinitionsFailed]));
        }
        else if (index.Definitions is not { PolicyCount: > 0 } && index.Entries.Any(e => e.MachineRegistry is not null || e.UserRegistry is not null))
        {
            notes.Add(new GpoSearchNote(Localizer[L.GpoSearch.NoteNoDefinitions]));
        }

        if (corpus.MaskedCount > 0)
        {
            notes.Add(new GpoSearchNote(Localizer.Format(L.GpoSearch.NoteMasked, corpus.MaskedCount)));
        }

        if (index.OtherDomains.Count > 0)
        {
            notes.Add(new GpoSearchNote(Localizer.Format(L.GpoIndex.NoteOtherDomains, string.Join(", ", index.OtherDomains))));
        }

        return notes;
    }

    /// <summary>The summary of an empty or one-character query: the settings per area and in total.</summary>
    private List<string> SummaryOf(GpoSearchCorpus corpus)
    {
        var lines = new List<string>();
        foreach (var area in corpus.Areas.Where(a => a.Occurrences > 0))
        {
            lines.Add(Localizer.Format(L.GpoSearch.SummaryArea, Localizer[L.GpoSearch.AreaKey(area.Area)], area.Settings, area.Occurrences, area.Gpos));
        }

        lines.Add(Localizer.Format(L.GpoSearch.SummaryTotal, corpus.Groups.Count, corpus.Records.Count, corpus.GposWithSettings));
        return lines;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Index.StateChanged -= OnIndexStateChanged;
            _running?.Cancel();
            _search.Cancel();
            _corpus = null;
            ClearResults();
        }

        base.Dispose(disposing);
    }
}
