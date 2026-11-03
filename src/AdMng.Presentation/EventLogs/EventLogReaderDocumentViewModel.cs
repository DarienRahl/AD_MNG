using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.EventLogs;

/// <summary>
/// "Dzienniki zdarzeń" (track E1, design note e-event-logs.md 5): one tab per connection window. Sources (every DC of the
/// domain with the PDC emulator marked, computers typed or picked from AD, a computer from its context menu or "Stan" page,
/// <c>.evtx</c> files), the query (channel, time preset, IDs, levels, providers, outcome, account/SID/IP/workstation as data
/// conditions of the Security channel, or a raw XPath checked live), the run with per-source progress and "Anuluj", the merged
/// virtualized timeline (Ctrl+F filters it in memory), the details of the selected record (fields with the meaning of codes,
/// SIDs and accounts resolved to AD objects through the window's DC, the rendered message on demand), coverage and access per
/// source, saved queries (settings without values read from a log) and the export to CSV or HTML with the personal-data note.
/// Records stay in memory only while the tab is open (rule 1).
/// </summary>
public sealed partial class EventLogReaderDocumentViewModel : EventLogDocumentBase, ISearchableDocument
{
    private readonly SerialCancellation _details = new();
    private IReadOnlyList<EventLogRecordRow> _all = [];
    private EventLogRunResult? _result;
    private DateTimeOffset? _lastFrom;
    private Task? _initialization;

    public EventLogReaderDocumentViewModel(EventLogServices services)
        : base(services)
    {
        ChannelOptions = [.. EventLogText.CommonChannels];
        LevelOptions = [.. EventLogText.Levels.Select(l => new EventLogLevelOption(l, EventLogText.Level(Localizer, l)))];
        TimePresetOptions = PresetOptions();
        SelectedTimePreset = TimePresetOptions.First(o => o.Value == EventLogTimePreset.Last24Hours);
        OutcomeOptions = OutcomeOptionsOf();
        SelectedOutcome = OutcomeOptions[0];
        foreach (var level in LevelOptions)
        {
            level.PropertyChanged += (_, _) => ValidateQuery();
        }

        Sources.CollectionChanged += (_, _) => RefreshCommands();
        LoadSavedQueryNames();
        LoadedText = IsSupported ? Localizer[L.EventLogs.NeverRun] : string.Empty;
    }

    public override string Title => Localizer[L.EventLogs.Title];

    public override ObjectIcon Icon => ObjectIcon.Query;

    public override string SelectionText => SelectedRow is { } row ? row.Summary : string.Empty;

    // ---- Results ----------------------------------------------------------------------------------------------------------

    /// <summary>The records of the last run that match the search box, newest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    public partial IReadOnlyList<EventLogRecordRow> Rows { get; private set; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>The records read by the last run (before the search box).</summary>
    public int TotalCount => _all.Count;

    /// <summary>"Pokazano najnowsze 50 000 z 61 200 – limit pamięci" when records were dropped, else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDroppedNote))]
    public partial string? DroppedNote { get; private set; }

    public bool HasDroppedNote => DroppedNote is not null;

    /// <summary>A note above the list ("Brak zdarzeń spełniających zapytanie.").</summary>
    [ObservableProperty]
    public partial string ListNote { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText), nameof(HasSelection))]
    public partial EventLogRecordRow? SelectedRow { get; set; }

    public bool HasSelection => SelectedRow is not null;

    /// <summary>The fields of the selected record (system fields first).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<EventLogFieldRow> Fields { get; private set; } = [];

    /// <summary>The rendered message of the selected record ("Pokaż komunikat"), null before it was asked for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? MessageText { get; private set; }

    public bool HasMessage => MessageText is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowMessageCommand))]
    public partial bool IsMessageLoading { get; private set; }

    // ---- ISearchableDocument ----------------------------------------------------------------------------------------------

    /// <summary>Ctrl+F: filters the loaded records in memory (no new read).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public event EventHandler? SearchFocusRequested;

    public void FocusSearch() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    // ---- Lifecycle --------------------------------------------------------------------------------------------------------

    /// <summary>Loads the domain's DCs as sources (once) and applies a request (a computer to read).</summary>
    public async Task OpenAsync(EventLogDocumentRequest? request = null)
    {
        // A request arriving while the DCs are still being listed waits for them (its source must be the only one selected).
        _initialization ??= LoadDomainControllersAsync();
        await _initialization.ConfigureAwait(true);

        if (request is not null)
        {
            await ApplyRequestAsync(request).ConfigureAwait(true);
        }
    }

    /// <summary>F5: runs the query again.</summary>
    public override Task RefreshAsync() => RunAsync();

    /// <summary>"Wyszukaj": reads the selected sources with the query.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAsync()
    {
        if (!TryBuildQuery(out var query, out var error))
        {
            QueryError = error;
            return Task.CompletedTask;
        }

        var sources = Sources.Where(s => s.IsSelected).ToList();
        if (sources.Count == 0)
        {
            QueryError = Localizer[L.EventLogs.NoSourceSelected];
            return Task.CompletedTask;
        }

        return RunExclusiveAsync((session, token) => ReadAsync(session, query!, sources, token));
    }

    private bool CanRun() => IsSupported && HasSession && !IsLoading && Sources.Any(s => s.IsSelected);

    private async Task ReadAsync(IDirectorySession session, EventLogQuery query, IReadOnlyList<EventLogSourceViewModel> sources, CancellationToken token)
    {
        _lastFrom = query.RawXPath is null ? query.From : null;
        ResetHosts(sources.Select(s => (s.Host, s.KindText)));
        ClearDetails();
        var targets = sources.Select(s => new EventLogTarget(s.Host, [query]) { Files = s.IsFiles ? s.Files : null }).ToList();
        var progress = HostProgress(_lastFrom, () => ProgressText = ReadingProgress());
        ProgressText = ReadingProgress();
        var result = await Task.Run(() => Services.CreateRunner(MaxRecords).RunAsync(session, targets, progress, token), CancellationToken.None).ConfigureAwait(true);
        var rows = await Task.Run(() => result.Records.Select(r => new EventLogRecordRow(r, Localizer, Zone)).ToList(), CancellationToken.None).ConfigureAwait(true);
        if (IsDisposed)
        {
            return;
        }

        // The final state of every source (the progress reports may still be queued).
        foreach (var host in result.Hosts)
        {
            Hosts.FirstOrDefault(h => string.Equals(h.Host, host.Host, StringComparison.OrdinalIgnoreCase))?.Update(host, _lastFrom, Services.Ui.Time.GetUtcNow());
        }

        OnPropertyChanged(nameof(HasHostProblems));
        _result = result;
        _all = rows;
        ApplyFilter();
        var failed = result.Hosts.Count(h => h.State == EventLogHostState.Failed);
        DroppedNote = result.Dropped > 0
            ? Localizer.Format(L.EventLogs.RecordsDropped, result.Records.Count.ToString("N0", Localizer.Culture), result.Total.ToString("N0", Localizer.Culture))
            : null;
        LoadedText = result.Cancelled
            ? Localizer.Format(L.EventLogs.StatusCancelled, result.Total.ToString("N0", Localizer.Culture))
            : failed > 0
                ? Localizer.Format(L.EventLogs.StatusDoneWithFailures, result.Total.ToString("N0", Localizer.Culture), result.Hosts.Count, failed)
                : Localizer.Format(L.EventLogs.StatusDone, result.Total.ToString("N0", Localizer.Culture), result.Hosts.Count);
        OnPropertyChanged(nameof(TotalCount));
        RefreshCommands();
    }

    /// <summary>Records kept in memory by a run (tests lower it; default <see cref="EventLogLimits.MaxRecordsInMemory"/>).</summary>
    public int MaxRecords { get; set; } = EventLogLimits.MaxRecordsInMemory;

    private void ApplyFilter()
    {
        var selected = SelectedRow;
        Rows = string.IsNullOrWhiteSpace(SearchText) ? _all : [.. _all.Where(r => r.Matches(SearchText))];
        ListNote = _result is null
            ? string.Empty
            : _all.Count == 0
                ? Localizer[L.EventLogs.NoRecords]
                : Rows.Count == 0 ? Localizer[L.EventLogs.NoMatches] : string.Empty;
        SelectedRow = selected is not null && Rows.Contains(selected) ? selected : null;
        RefreshCommands();
    }

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        RunCommand.NotifyCanExecuteChanged();
        LoadChannelsCommand.NotifyCanExecuteChanged();
        PickComputersCommand.NotifyCanExecuteChanged();
        ShowMessageCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
        CopyDetailsCommand.NotifyCanExecuteChanged();
    }

    protected override void OnSessionChangedCore()
    {
        _initialization = null;
        Sources.Clear();
        if (HasSession)
        {
            OpenAsync().Forget();
        }
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        foreach (var level in LevelOptions)
        {
            level.Text = EventLogText.Level(Localizer, level.Level);
        }

        var preset = SelectedTimePreset.Value;
        TimePresetOptions = PresetOptions();
        SelectedTimePreset = TimePresetOptions.First(o => o.Value == preset);
        var outcome = SelectedOutcome.Value;
        OutcomeOptions = OutcomeOptionsOf();
        SelectedOutcome = OutcomeOptions.First(o => o.Value == outcome);
        if (_result is { } result)
        {
            _all = [.. result.Records.Select(r => new EventLogRecordRow(r, Localizer, Zone))];
            ApplyFilter();
        }

        ValidateQuery();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _details.Cancel();
            _all = [];
            _result = null;
            Rows = [];
            Fields = [];
        }
    }

    private List<Option<EventLogTimePreset>> PresetOptions() =>
        [.. new[] { EventLogTimePreset.LastHour, EventLogTimePreset.Last24Hours, EventLogTimePreset.Last7Days, EventLogTimePreset.Custom }
            .Select(p => new Option<EventLogTimePreset>(p, EventLogText.TimePreset(Localizer, p)))];

    private List<Option<EventAuditOutcome?>> OutcomeOptionsOf() =>
        [.. new EventAuditOutcome?[] { null, EventAuditOutcome.Success, EventAuditOutcome.Failure }
            .Select(o => new Option<EventAuditOutcome?>(o, EventLogText.Outcome(Localizer, o)))];
}

/// <summary>A level the reader can filter on.</summary>
public sealed partial class EventLogLevelOption(byte level, string text) : ObservableObject
{
    public byte Level { get; } = level;

    [ObservableProperty]
    public partial string Text { get; set; } = text;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public override string ToString() => Text;
}
