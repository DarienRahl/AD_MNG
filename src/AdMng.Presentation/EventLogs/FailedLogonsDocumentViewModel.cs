using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using AdMng.Core.Ldap;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.EventLogs;

/// <summary>
/// "Nieudane logowania" (track E2, design note e-event-logs.md 4.2, 5): one tab per connection window. Reads the failures on
/// every DC (step 1), then the successes of the accounts that could show "success after failures" (step 2), and shows the
/// findings – brute force, password spraying, account enumeration, success after failures – with severity chips and filters
/// (K6 style), the details of the selected finding with its records (drill-down), the top accounts, sources and codes, the
/// audit evidence and the export. The thresholds are editable; the DC names and addresses are passed so relayed failures
/// are counted but never findings.
/// </summary>
public sealed partial class FailedLogonsDocumentViewModel : EventLogDocumentBase, Shell.ISearchableDocument
{
    private FailedLogonReport? _report;
    private IReadOnlyList<EventLogRecord> _records = [];
    private IReadOnlyList<FailedLogonFindingRow> _all = [];
    private DateTimeOffset _from;
    private DateTimeOffset _to;
    private bool _successesRead;

    public FailedLogonsDocumentViewModel(EventLogServices services)
        : base(services)
    {
        TimePresetOptions = PresetOptions();
        SelectedTimePreset = TimePresetOptions[1];
        var defaults = FailedLogonOptions.Default;
        BruteForceThreshold = defaults.BruteForceThreshold;
        SprayAccounts = defaults.SprayAccounts;
        SprayWindowMinutes = (int)defaults.SprayWindow.TotalMinutes;
        SprayMaxPerAccount = defaults.SprayMaxPerAccount;
        EnumerationThreshold = defaults.EnumerationThreshold;
        SuccessAfterFailures = defaults.SuccessAfterFailures;
        BuildFilterOptions();
        LoadedText = IsSupported ? Localizer[L.EventLogsFailedLogons.NeverRun] : string.Empty;
    }

    public override string Title => Localizer[L.EventLogsFailedLogons.Title];

    public override ObjectIcon Icon => ObjectIcon.Query;

    public override string SelectionText => SelectedFinding?.Title ?? string.Empty;

    // ---- Window and thresholds -----------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<Option<EventLogTimePreset>> TimePresetOptions { get; private set; } = [];

    [ObservableProperty]
    public partial Option<EventLogTimePreset> SelectedTimePreset { get; set; } = new(EventLogTimePreset.Last24Hours, string.Empty);

    [ObservableProperty]
    public partial int BruteForceThreshold { get; set; }

    [ObservableProperty]
    public partial int SprayAccounts { get; set; }

    [ObservableProperty]
    public partial int SprayWindowMinutes { get; set; }

    [ObservableProperty]
    public partial int SprayMaxPerAccount { get; set; }

    [ObservableProperty]
    public partial int EnumerationThreshold { get; set; }

    [ObservableProperty]
    public partial int SuccessAfterFailures { get; set; }

    // ---- Report ---------------------------------------------------------------------------------------------------------

    public FailedLogonReport? Report => _report;

    public bool HasReport => _report is not null;

    /// <summary>"124 niepowodzenia, 3 udane logowania, 3 ustalenia · od … do …".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<FailedLogonSeverityChip> Chips { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<Option<FailedLogonSeverity?>> SeverityOptions { get; private set; } = [];

    [ObservableProperty]
    public partial Option<FailedLogonSeverity?> SelectedSeverity { get; set; } = new(null, string.Empty);

    [ObservableProperty]
    public partial IReadOnlyList<Option<FailedLogonFindingKind?>> KindOptions { get; private set; } = [];

    [ObservableProperty]
    public partial Option<FailedLogonFindingKind?> SelectedKind { get; set; } = new(null, string.Empty);

    /// <summary>Ctrl+F: filters the findings by their texts.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public event EventHandler? SearchFocusRequested;

    public void FocusSearch() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    public bool IsFiltered => SelectedSeverity.Value is not null || SelectedKind.Value is not null || !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>The findings that pass the filters.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<FailedLogonFindingRow> Findings { get; private set; } = [];

    [ObservableProperty]
    public partial string ListNote { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText), nameof(HasSelection))]
    public partial FailedLogonFindingRow? SelectedFinding { get; set; }

    public bool HasSelection => SelectedFinding is not null;

    /// <summary>The accounts of the selected finding (unknown names masked until "Pokaż").</summary>
    [ObservableProperty]
    public partial IReadOnlyList<FailedLogonAccountRow> SelectedAccounts { get; private set; } = [];

    /// <summary>The records of the selected finding (drill-down), newest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<EventLogRecordRow> SelectedRecords { get; private set; } = [];

    [ObservableProperty]
    public partial string RecordsHeading { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> TopAccounts { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> TopSources { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> CodeCounts { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial IReadOnlyList<AnalysisNoteRow> Notes { get; private set; } = [];

    public bool HasNotes => Notes.Count > 0;

    [ObservableProperty]
    public partial IReadOnlyList<AuditEvidenceRow> AuditRows { get; private set; } = [];

    // ---- Run --------------------------------------------------------------------------------------------------------------

    public override Task RefreshAsync() => RunAsync();

    /// <summary>"Sprawdź": step 1 (failures), step 2 (successes of the candidates), the analysis.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    public Task RunAsync() => RunExclusiveAsync(AnalyzeAsync);

    private bool CanRun() => IsSupported && HasSession && !IsLoading;

    /// <summary>The thresholds as the analysis takes them (values clamped to 1–10 000).</summary>
    public FailedLogonOptions Options(IReadOnlyList<string> domainControllers) => new()
    {
        BruteForceThreshold = Clamp(BruteForceThreshold),
        SprayAccounts = Clamp(SprayAccounts),
        SprayWindow = TimeSpan.FromMinutes(Clamp(SprayWindowMinutes)),
        SprayMaxPerAccount = Clamp(SprayMaxPerAccount),
        EnumerationThreshold = Clamp(EnumerationThreshold),
        SuccessAfterFailures = Clamp(SuccessAfterFailures),
        DomainControllers = domainControllers,
    };

    private static int Clamp(int value) => Math.Clamp(value, 1, 10_000);

    private async Task AnalyzeAsync(IDirectorySession session, CancellationToken token)
    {
        var now = Services.Ui.Time.GetUtcNow();
        (_from, _to) = EventLogTimeWindows.Of(SelectedTimePreset.Value, now) ?? (now - EventLogLimits.DefaultWindow, now);
        var controllers = await Task.Run(() => Services.ReadDomainControllersAsync(session, token), token).ConfigureAwait(true);
        var hosts = controllers.Select(c => c.Host).ToList();
        var addresses = await Task.Run(() => Services.ResolveAddressesAsync(hosts, token), token).ConfigureAwait(true);
        var options = Options([.. hosts, .. addresses.Values.SelectMany(a => a)]);
        ResetHosts(controllers.Select(c => (c.Host, Localizer[c.IsPdcEmulator ? L.EventLogs.KindPdc : L.EventLogs.KindDomainController])));

        // Step 1: the failures.
        var progress = HostProgress(_from, () => ProgressText = Localizer.Format(L.EventLogsFailedLogons.StageFailures, ReadingProgress()));
        ProgressText = Localizer.Format(L.EventLogsFailedLogons.StageFailures, ReadingProgress());
        var failures = FailedLogonAnalysis.Queries(_from, _to);
        var step1 = await Task.Run(() => Services.CreateRunner().RunAsync(session, [.. hosts.Select(h => new EventLogTarget(h, failures))], progress, token),
            CancellationToken.None).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();

        // Step 2: the successes of the accounts that could show "success after failures" (as logged and as stored in AD).
        var records = step1.Records.ToList();
        var dropped = step1.Dropped;
        var candidates = FailedLogonAnalysis.SuccessCandidates(step1.Records, options).ToList();
        _successesRead = false;
        if (candidates.Count > 0 && step1.Records.Count < EventLogLimits.MaxRecordsInMemory)
        {
            candidates.AddRange(await Task.Run(() => StoredNamesAsync(session, candidates, token), token).ConfigureAwait(true));
            var successes = FailedLogonAnalysis.SuccessQueries(candidates.Distinct(StringComparer.Ordinal), _from, _to);
            ProgressText = Localizer.Format(L.EventLogsFailedLogons.StageSuccesses, candidates.Count);
            var step2 = await Task.Run(() => Services.CreateRunner().RunAsync(session, [.. hosts.Select(h => new EventLogTarget(h, successes))], null, token,
                EventLogLimits.MaxRecordsInMemory - step1.Records.Count), CancellationToken.None).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            records.AddRange(step2.Records);
            dropped += step2.Dropped;
            _successesRead = step2.Hosts.Any(h => h.State == EventLogHostState.Done);
        }

        ProgressText = Localizer[L.EventLogsFailedLogons.StageAnalysis];
        var report = await Task.Run(() => FailedLogonAnalysis.Analyze(records, options), token).ConfigureAwait(true);
        var evidence = AuditEvidence.Evaluate(FailedLogonAnalysis.Requirements(_successesRead), records, step1.Coverage, _from, hosts);
        foreach (var host in step1.Hosts)
        {
            Hosts.FirstOrDefault(h => string.Equals(h.Host, host.Host, StringComparison.OrdinalIgnoreCase))?.Update(host, _from, now);
        }

        _records = records;
        _report = report;
        _evidence = evidence;
        _dropped = dropped;
        Rebuild();
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(Report));
        RefreshCommands();
    }

    private IReadOnlyList<AuditEvidenceEntry> _evidence = [];
    private int _dropped;

    /// <summary>The sAMAccountNames as stored in AD of the candidates (the event log compares names case-sensitively).</summary>
    private static async Task<IReadOnlyList<string>> StoredNamesAsync(IDirectorySession session, IReadOnlyList<string> candidates, CancellationToken token)
    {
        var names = new List<string>();
        foreach (var chunk in candidates.Where(c => c.Length is > 0 and <= 256).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(50))
        {
            var search = new DirectorySearch
            {
                BaseDn = session.Info.DefaultNamingContext,
                Scope = SearchScope.Subtree,
                Filter = LdapFilter.Or(chunk.Select(c => LdapFilter.Equal(Attr.SamAccountName, c))),
                Attributes = [Attr.SamAccountName],
                PageSize = 100,
            };
            try
            {
                await foreach (var found in session.SearchAsync(search, token).ConfigureAwait(false))
                {
                    if (found.GetString(Attr.SamAccountName) is { Length: > 0 } sam)
                    {
                        names.Add(sam);
                    }
                }
            }
            catch (DirectoryException)
            {
                // Best effort: the names as logged are read anyway.
            }
        }

        return names;
    }

    /// <summary>The texts and rows of the shown report (also after a language change).</summary>
    private void Rebuild()
    {
        if (_report is not { } report)
        {
            return;
        }

        var zone = Zone;
        var culture = Localizer.Culture;
        SummaryText = Localizer.Format(L.EventLogsFailedLogons.Summary, report.FailureCount.ToString("N0", culture), report.SuccessCount.ToString("N0", culture),
            report.Findings.Count, EventLogText.Moment(Localizer, _from, zone), EventLogText.Moment(Localizer, _to, zone));
        _all = [.. report.Findings.Select(f => new FailedLogonFindingRow(f, Localizer, zone))];
        TopAccounts = [.. report.TopAccounts.Select(a => FailedLogonFindingRow.AccountText(Localizer, a, revealed: false))];
        TopSources = [.. report.TopSources.Select(s => Localizer.Format(s.IsDomainController ? L.EventLogsFailedLogons.TopSourceRelayed : L.EventLogsFailedLogons.TopSource,
            EventLogText.Source(Localizer, s.Source), s.Count.ToString("N0", culture), s.AccountCount))];
        CodeCounts = [.. report.CodeCounts.Select(c => EventLogText.CodeCount(Localizer, c))];
        var notes = report.Notes.Select(n => new AnalysisNoteRow(EventLogText.FailedLogonNote(Localizer, n, zone), n.Severity == AnalysisNoteSeverity.Warning)).ToList();
        if (_dropped > 0)
        {
            notes.Insert(0, new AnalysisNoteRow(Localizer.Format(L.EventLogsFailedLogons.RecordsDropped, _dropped.ToString("N0", culture)), true));
        }

        Notes = notes;
        AuditRows = [.. _evidence.Select(e => new AuditEvidenceRow(e, Localizer))];
        BuildChips();
        ApplyFilters();
        LoadedText = SummaryText;
    }

    private void BuildChips()
    {
        var selected = Chips.FirstOrDefault(c => c.IsSelected)?.Severity;
        Chips = [.. Enum.GetValues<FailedLogonSeverity>().Reverse().Select(s =>
        {
            var count = _all.Count(f => f.Severity == s);
            var chip = new FailedLogonSeverityChip(s, Localizer.Format(L.EventLogsFailedLogons.Chip, EventLogText.Severity(Localizer, s), count), count) { IsSelected = s == selected };
            chip.PropertyChanged += OnChipChanged;
            return chip;
        })];
    }

    private void OnChipChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is FailedLogonSeverityChip { IsSelected: true } chip && e.PropertyName == nameof(FailedLogonSeverityChip.IsSelected))
        {
            SelectedSeverity = SeverityOptions.FirstOrDefault(o => o.Value == chip.Severity) ?? SelectedSeverity;
        }
        else if (sender is FailedLogonSeverityChip { IsSelected: false } off && SelectedSeverity.Value == off.Severity)
        {
            SelectedSeverity = SeverityOptions[0];
        }
    }

    partial void OnSelectedSeverityChanged(Option<FailedLogonSeverity?> value)
    {
        foreach (var chip in Chips)
        {
            chip.IsSelected = chip.Severity == value.Value;
        }

        ApplyFilters();
    }

    partial void OnSelectedKindChanged(Option<FailedLogonFindingKind?> value) => ApplyFilters();

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    private void ApplyFilters()
    {
        var selected = SelectedFinding;
        var text = SearchText?.Trim() ?? string.Empty;
        Findings = [.. _all.Where(f =>
            (SelectedSeverity.Value is not { } severity || f.Severity == severity)
            && (SelectedKind.Value is not { } kind || f.Kind == kind)
            && (text.Length == 0 || Matches(f, text)))];
        ListNote = _report is null
            ? string.Empty
            : _all.Count == 0 ? Localizer[L.EventLogsFailedLogons.NoFindings] : Findings.Count == 0 ? Localizer[L.EventLogsFailedLogons.NoMatches] : string.Empty;
        SelectedFinding = selected is not null && Findings.Contains(selected) ? selected : Findings.FirstOrDefault();
        OnPropertyChanged(nameof(IsFiltered));
        ClearFiltersCommand.NotifyCanExecuteChanged();
        RefreshCommands();
    }

    private static bool Matches(FailedLogonFindingRow row, string text) =>
        new[] { row.KindText, row.SourceText, row.SeverityText, row.CodesText, row.RelatedText }
            .Concat(row.Finding.Accounts.Where(a => !a.IsUnknownAccount).Select(a => a.Account))
            .Any(t => t.Contains(text, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>"Wyczyść filtry".</summary>
    [RelayCommand(CanExecute = nameof(IsFiltered))]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedKind = KindOptions[0];
        SelectedSeverity = SeverityOptions[0];
    }

    partial void OnSelectedFindingChanged(FailedLogonFindingRow? value)
    {
        if (value is null)
        {
            SelectedAccounts = [];
            SelectedRecords = [];
            RecordsHeading = string.Empty;
            return;
        }

        SelectedAccounts = [.. value.Finding.Accounts.Select(a => new FailedLogonAccountRow(a, Localizer))];
        var index = _records.ToLookup(r => (EventHostNames.Normalize(r.Host), r.RecordId));
        SelectedRecords = [.. value.Finding.Records
            .SelectMany(r => index[(EventHostNames.Normalize(r.Host), r.RecordId)].Take(1))
            .OrderByDescending(r => r.TimeCreated)
            .Select(r => new EventLogRecordRow(r, Localizer, Zone))];
        RecordsHeading = value.Finding.RecordsTruncated
            ? Localizer.Format(L.EventLogsFailedLogons.RecordsTruncated, SelectedRecords.Count)
            : Localizer.Format(L.EventLogsFailedLogons.RecordsHeading, SelectedRecords.Count);
    }

    /// <summary>"Pokaż" of a masked account name (a password typed into the user name field may be revealed on purpose only).</summary>
    [RelayCommand]
    private static void RevealAccount(FailedLogonAccountRow? row)
    {
        if (row is not null)
        {
            row.IsRevealed = true;
        }
    }

    /// <summary>"Pokaż w modelu" of the DC of an audit evidence row.</summary>
    [RelayCommand(CanExecute = nameof(CanUseGroupPolicyCommand))]
    private async Task ShowInModelAsync(AuditEvidenceRow? row)
    {
        if (row is not null && Session is { } session)
        {
            try
            {
                await Services.ShowInModelAsync(session, row.Host, CancellationToken.None).ConfigureAwait(true);
            }
            catch (DirectoryException ex)
            {
                Error = Services.Ui.Errors.Translate(ex);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseGroupPolicyCommand))]
    private void OpenGroupPolicyDiagnostics(AuditEvidenceRow? row) => Services.OpenGroupPolicyDiagnostics();

    private bool CanUseGroupPolicyCommand(AuditEvidenceRow? row) => Services.GroupPolicy is not null && HasSession;

    public bool CanUseGroupPolicy => Services.GroupPolicy is not null;

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        RunCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        var preset = SelectedTimePreset.Value;
        TimePresetOptions = PresetOptions();
        SelectedTimePreset = TimePresetOptions.First(o => o.Value == preset);
        BuildFilterOptions();
        Rebuild();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _report = null;
            _records = [];
            _all = [];
            Findings = [];
            SelectedRecords = [];
        }
    }

    private void BuildFilterOptions()
    {
        var severity = SelectedSeverity.Value;
        var kind = SelectedKind.Value;
        SeverityOptions = [new(null, Localizer[L.EventLogsFailedLogons.All]), .. Enum.GetValues<FailedLogonSeverity>().Reverse()
            .Select(s => new Option<FailedLogonSeverity?>(s, EventLogText.Severity(Localizer, s)))];
        KindOptions = [new(null, Localizer[L.EventLogsFailedLogons.All]), .. Enum.GetValues<FailedLogonFindingKind>()
            .Select(k => new Option<FailedLogonFindingKind?>(k, EventLogText.FindingKind(Localizer, k)))];
        SelectedSeverity = SeverityOptions.First(o => o.Value == severity);
        SelectedKind = KindOptions.First(o => o.Value == kind);
    }

    private List<Option<EventLogTimePreset>> PresetOptions() =>
        [.. new[] { EventLogTimePreset.LastHour, EventLogTimePreset.Last24Hours, EventLogTimePreset.Last7Days }
            .Select(p => new Option<EventLogTimePreset>(p, EventLogText.TimePreset(Localizer, p)))];

    // ---- Export -----------------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasReport))]
    private Task ExportCsvAsync() =>
        EventLogExport.ExportCsvAsync(Services.Ui, Services.Csv, Localizer[L.EventLogs.ExportCsv], Title, FindingsTable(null));

    [RelayCommand(CanExecute = nameof(HasReport))]
    private Task ExportHtmlAsync()
    {
        var tables = new List<EventLogExportTable>
        {
            FindingsTable(Localizer[L.EventLogsFailedLogons.FindingsHeading]),
            new(Localizer[L.EventLogsFailedLogons.TopAccountsHeading], [Localizer[L.EventLogsFailedLogons.TopAccountsHeading]], [.. TopAccounts.Select(a => (IReadOnlyList<string>)[a])]),
            new(Localizer[L.EventLogsFailedLogons.TopSourcesHeading], [Localizer[L.EventLogsFailedLogons.TopSourcesHeading]], [.. TopSources.Select(a => (IReadOnlyList<string>)[a])]),
            new(Localizer[L.EventLogsFailedLogons.CodesHeading], [Localizer[L.EventLogsFailedLogons.CodesHeading]], [.. CodeCounts.Select(a => (IReadOnlyList<string>)[a])]),
            new(Localizer[L.EventLogsLockout.AuditHeading],
                [.. new[] { L.EventLogsLockout.ColumnDc, L.EventLogs.ColumnId, L.EventLogsLockout.ColumnSubcategory, L.EventLogs.ColumnState }.Select(k => Localizer[k])],
                [.. AuditRows.Select(a => (IReadOnlyList<string>)[a.Host, a.EventText, a.SubcategoryText, a.StateText])]),
        };
        return EventLogExport.ExportHtmlAsync(Services.Ui, Localizer[L.EventLogs.ExportHtml], Title, [SummaryText], [.. Notes.Select(n => n.Text)], tables);
    }

    /// <summary>The findings that pass the filters (unknown account names masked).</summary>
    private EventLogExportTable FindingsTable(string? heading) => new(
        heading,
        [.. new[]
        {
            L.EventLogsFailedLogons.ColumnSeverity, L.EventLogsFailedLogons.ColumnKind, L.EventLogsLockout.ColumnSource, L.EventLogsFailedLogons.ColumnAccounts,
            L.EventLogsFailedLogons.ColumnCount, L.EventLogsLockout.ColumnFirst, L.EventLogsLockout.ColumnLast, L.EventLogsLockout.ColumnCodes,
        }.Select(k => Localizer[k])],
        [.. Findings.Select(f => (IReadOnlyList<string>)
        [
            f.SeverityText, f.KindText, f.SourceText,
            string.Join(", ", f.Finding.Accounts.Select(a => FailedLogonFindingRow.AccountText(Localizer, a, revealed: false))),
            f.CountText, f.FirstText, f.LastText, f.CodesText,
        ])]);

}
