using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using AdMng.Core.Model;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.EventLogs;

/// <summary>
/// "Skąd blokada konta – &lt;konto&gt;" (track E2, design note e-event-logs.md 4.1, 5): one tab per account per connection
/// window. Reads the account and its lockout policy (domain policy or the resultant PSO), the state of the account on every DC
/// (<see cref="Services"/>' DomainControllerReader, rule 4), the DC that wrote lockoutTime (msDS-ReplAttributeMetaData),
/// then the Security logs of every DC (PDC emulator and originating DC first) and shows the analysis in the K6 layout:
/// summary, ranked sources with codes and proxy hints, timeline, per-DC table, notes and audit evidence with "Pokaż w modelu"
/// and "Diagnostyka zasad grupy". The pivot "Sprawdź komputer źródłowy" asks the source computer (K1, read only) for the
/// account's sessions, services and scheduled tasks. "Odblokuj konto" goes through the change plan (rule 5).
/// </summary>
public sealed partial class LockoutDocumentViewModel : EventLogDocumentBase
{
    private static readonly string[] AccountAttributes =
    [
        Attr.SamAccountName, Attr.ObjectSid, Attr.UserPrincipalName, Attr.ObjectGuid, Attr.Name, Attr.DisplayName, Attr.ObjectClass, ResultantPsoAttribute,
    ];

    private static readonly string[] DomainControllerAttributes = [Attr.BadPwdCount, Attr.BadPasswordTime, Attr.LockoutTime, Attr.LastLogon];

    private const string ResultantPsoAttribute = "msDS-ResultantPSO";
    private const string ObservationWindowAttribute = "lockOutObservationWindow";

    private LockoutReport? _report;
    private string _accountName;

    public LockoutDocumentViewModel(EventLogServices services, string userDn, string? name)
        : base(services)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDn);
        UserDn = userDn;
        _accountName = !string.IsNullOrWhiteSpace(name)
            ? name
            : Core.Ldap.DistinguishedName.TryParse(userDn, out var dn) && !dn.IsRoot ? dn.Name : userDn;
        TimePresetOptions = PresetOptions();
        SelectedTimePreset = TimePresetOptions[0];
        LoadedText = IsSupported ? Localizer[L.EventLogsLockout.NeverRun] : string.Empty;
    }

    /// <summary>The investigated account.</summary>
    public string UserDn { get; }

    public override string Title => Localizer.Format(L.EventLogsLockout.Title, _accountName);

    public override ObjectIcon Icon => ObjectIcon.UserLocked;

    public override string SelectionText => SelectedSource?.Value ?? string.Empty;

    // ---- Window ---------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<Option<EventLogTimePreset>> TimePresetOptions { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomTime))]
    public partial Option<EventLogTimePreset> SelectedTimePreset { get; set; } = new(EventLogTimePreset.BeforeLastLockout, string.Empty);

    public bool IsCustomTime => SelectedTimePreset.Value == EventLogTimePreset.Custom;

    [ObservableProperty]
    public partial DateTime? CustomFrom { get; set; }

    [ObservableProperty]
    public partial DateTime? CustomTo { get; set; }

    // ---- Report ---------------------------------------------------------------------------------------------------------

    /// <summary>The report shown (null before the first run).</summary>
    public LockoutReport? Report => _report;

    public bool HasReport => _report is not null;

    /// <summary>The account's objectGUID (the Watchdog history link).</summary>
    public Guid? AccountGuid { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    public partial bool IsLockedNow { get; private set; }

    /// <summary>"Konto jest teraz zablokowane na: DC01, DC02" / "Konto nie jest teraz zablokowane."</summary>
    [ObservableProperty]
    public partial string LockStateText { get; private set; } = string.Empty;

    /// <summary>"Ostatnia blokada: 3.11.2026 08:34:01 na DC01".</summary>
    [ObservableProperty]
    public partial string LastLockoutText { get; private set; } = string.Empty;

    /// <summary>"Próg blokady: 5 złych haseł w ciągu 30 min; blokada na 30 min (zasady domeny)".</summary>
    [ObservableProperty]
    public partial string PolicyText { get; private set; } = string.Empty;

    /// <summary>"Nieudane próby: 5, w tym złe hasła: 5 · przedział od … do …".</summary>
    [ObservableProperty]
    public partial string CountsText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string WindowText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<LockoutSourceRow> Sources { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    [NotifyCanExecuteChangedFor(nameof(PivotCommand))]
    public partial LockoutSourceRow? SelectedSource { get; set; }

    /// <summary>"Nie znaleziono źródeł …" when the report has none.</summary>
    [ObservableProperty]
    public partial string SourcesNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<LockoutTimelineRow> Timeline { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<LockoutDcRowViewModel> DomainControllerRows { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial IReadOnlyList<AnalysisNoteRow> Notes { get; private set; } = [];

    public bool HasNotes => Notes.Count > 0;

    [ObservableProperty]
    public partial IReadOnlyList<AuditEvidenceRow> AuditRows { get; private set; } = [];

    /// <summary>The Group Policy links of the audit evidence are offered (the window has the Group Policy services).</summary>
    public bool CanUseGroupPolicy => Services.GroupPolicy is not null;

    /// <summary>"Historia w Watchdogu" is offered.</summary>
    public bool IsWatchdogHistoryAvailable => Services.History?.IsAvailable == true;

    // ---- Run --------------------------------------------------------------------------------------------------------------

    /// <summary>F5: investigates again.</summary>
    public override Task RefreshAsync() => RunAsync();

    /// <summary>"Sprawdź": the whole investigation (account, DCs, logs, analysis).</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    public Task RunAsync() => RunExclusiveAsync(InvestigateAsync);

    private bool CanRun() => IsSupported && HasSession && !IsLoading;

    private async Task InvestigateAsync(IDirectorySession session, CancellationToken token)
    {
        ProgressText = Localizer[L.EventLogsLockout.StageAccount];
        var account = await Task.Run(() => session.GetAsync(UserDn, AccountAttributes, token), token).ConfigureAwait(true);
        if (account is null)
        {
            Error = Localizer[L.EventLogsLockout.AccountNotFound];
            return;
        }

        if (account.Kind is not (ObjectKind.User or ObjectKind.InetOrgPerson) || account.GetString(Attr.SamAccountName) is not { Length: > 0 } sam)
        {
            Error = Localizer[L.EventLogsLockout.NotAUser];
            return;
        }

        _accountName = account.GetString(Attr.DisplayName) is { Length: > 0 } display ? display : account.Name;
        OnPropertyChanged(nameof(Title));
        AccountGuid = account.ObjectGuid;
        WatchdogHistoryCommand.NotifyCanExecuteChanged();
        var lockoutAccount = new LockoutAccount(sam, SidOf(account), account.GetString(Attr.UserPrincipalName), account.DistinguishedName);
        var (policy, policySource) = await Task.Run(() => ReadPolicyAsync(session, account.GetString(ResultantPsoAttribute), token), token).ConfigureAwait(true);

        ProgressText = Localizer[L.EventLogsLockout.StageDomainControllers];
        var controllers = await Task.Run(() => Services.ReadDomainControllersAsync(session, token), token).ConfigureAwait(true);
        var reads = await Task.Run(() => Services.DomainControllers.ReadAsync(session, [UserDn], DomainControllerAttributes, token), token).ConfigureAwait(true);
        var addresses = await Task.Run(() => Services.ResolveAddressesAsync(reads.Select(r => r.Server), token), token).ConfigureAwait(true);
        var states = reads.Select(r => StateOf(r, controllers, addresses)).ToList();
        var originating = await Task.Run(() => OriginatingDcAsync(session, token), token).ConfigureAwait(true);

        var now = Services.Ui.Time.GetUtcNow();
        if (WindowOf(states, now) is not { } window)
        {
            Error = Localizer[L.EventLogs.InvalidRange];
            return;
        }

        var (from, to) = window;

        var queries = LockoutInvestigation.Queries(lockoutAccount, from, to);
        var hosts = LockoutInvestigation.OrderHosts(states, originating);
        ResetHosts(hosts.Select(h => (h, KindText(h, states, originating))));
        var progress = HostProgress(from, () => ProgressText = Localizer.Format(L.EventLogsLockout.StageLogs, ReadingProgress()));
        ProgressText = Localizer.Format(L.EventLogsLockout.StageLogs, ReadingProgress());
        var result = await Task.Run(() => Services.CreateRunner().RunAsync(session, [.. hosts.Select(h => new EventLogTarget(h, queries))], progress, token),
            CancellationToken.None).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();

        ProgressText = Localizer[L.EventLogsLockout.StageAnalysis];
        var input = new LockoutInvestigationInput
        {
            Account = lockoutAccount,
            DomainControllers = states,
            OriginatingDc = originating,
            Policy = policy,
            From = from,
            To = to,
            Records = result.Records,
            Coverage = result.Coverage,
            ReadFailures = result.Failures,
        };
        var report = await Task.Run(() => LockoutInvestigation.Analyze(input), token).ConfigureAwait(true);
        foreach (var host in result.Hosts)
        {
            Hosts.FirstOrDefault(h => string.Equals(h.Host, host.Host, StringComparison.OrdinalIgnoreCase))?.Update(host, from, now);
        }

        _records = result.Records;
        Show(report, policy, policySource, result);
    }

    private IReadOnlyList<EventLogRecord> _records = [];

    private string? _policySource;
    private LockoutPolicy? _policy;
    private EventLogRunResult? _run;

    /// <summary>Shows a report: summary, sources, timeline, DCs, notes, audit evidence.</summary>
    private void Show(LockoutReport report, LockoutPolicy policy, string? policySource, EventLogRunResult run)
    {
        _report = report;
        _policy = policy;
        _policySource = policySource;
        _run = run;
        Rebuild();
        LoadedText = Localizer.Format(L.EventLogsLockout.StatusDone, report.Sources.Count, report.Lockouts.Count, run.Total.ToString("N0", Localizer.Culture));
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(Report));
        RefreshCommands();
    }

    /// <summary>Builds the texts and rows of the shown report (also after a language change).</summary>
    private void Rebuild()
    {
        if (_report is not { } report || _policy is not { } policy)
        {
            return;
        }

        var zone = Zone;
        var lockedOn = report.DomainControllers
            .Where(r => r.State?.LockoutTime is { } time && IsStillLocked(time, policy, report.To))
            .Select(r => EventHostNames.ShortName(r.Host) ?? r.Host)
            .ToList();
        IsLockedNow = report.IsLockedNow;
        LockStateText = report.IsLockedNow
            ? Localizer.Format(L.EventLogsLockout.LockedNow, lockedOn.Count > 0 ? string.Join(", ", lockedOn) : "–")
            : Localizer[L.EventLogsLockout.NotLockedNow];
        var last = report.Lockouts.LastOrDefault();
        LastLockoutText = last is not null
            ? Localizer.Format(L.EventLogsLockout.LastLockout, EventLogText.Moment(Localizer, last.Time, zone), EventHostNames.ShortName(last.DomainController) ?? last.DomainController)
            : report.LastLockout is { } lastTime
                ? Localizer.Format(L.EventLogsLockout.LastLockoutState, EventLogText.Moment(Localizer, lastTime, zone))
                : Localizer[L.EventLogsLockout.NoLockout];
        PolicyText = PolicyLine(policy, _policySource);
        CountsText = Localizer.Format(L.EventLogsLockout.Counts, report.FailureCount, report.BadPasswordCount);
        WindowText = Localizer.Format(L.EventLogsLockout.Window, EventLogText.Moment(Localizer, report.From, zone), EventLogText.Moment(Localizer, report.To, zone));
        var previous = SelectedSource?.Source.Source;
        Sources = [.. report.Sources.Select((s, i) => new LockoutSourceRow(i + 1, s, Localizer, zone))];
        SelectedSource = Sources.FirstOrDefault(s => s.Source.Source == previous) ?? Sources.FirstOrDefault();
        SourcesNote = Sources.Count == 0 ? Localizer[L.EventLogsLockout.NoSources] : string.Empty;
        Timeline = [.. report.Timeline.Reverse().Select(e => new LockoutTimelineRow(e, Localizer, zone))];
        var now = Services.Ui.Time.GetUtcNow();
        DomainControllerRows = [.. report.DomainControllers.Select(r => new LockoutDcRowViewModel(r, Localizer, zone, now, Describe))];
        Notes = [.. report.Notes.Select(n => new AnalysisNoteRow(EventLogText.LockoutNote(Localizer, n, zone), n.Severity == AnalysisNoteSeverity.Warning))];
        AuditRows = [.. report.AuditEvidence.Select(e => new AuditEvidenceRow(e, Localizer))];
    }

    private string Describe(EventLogCoverage coverage) =>
        new EventLogHostRow(coverage.Host, string.Empty, Localizer, Zone).Describe(coverage);

    private string PolicyLine(LockoutPolicy policy, string? source)
    {
        if (policy.Threshold <= 0)
        {
            return Localizer[L.EventLogsLockout.PolicyNever];
        }

        var window = policy.ObservationWindow is { } w ? EventLogText.Duration(Localizer, w) : Localizer[L.Common.Unknown];
        var duration = policy.Duration is { } d && d > TimeSpan.Zero ? EventLogText.Duration(Localizer, d) : Localizer[L.EventLogsLockout.UntilUnlocked];
        var origin = source is null ? Localizer[L.EventLogsLockout.PolicyDomain] : Localizer.Format(L.EventLogsLockout.PolicyPso, source);
        return Localizer.Format(L.EventLogsLockout.Policy, policy.Threshold, window, duration, origin);
    }

    private static bool IsStillLocked(DateTimeOffset lockoutTime, LockoutPolicy policy, DateTimeOffset at) =>
        policy.Duration is not { } duration || duration <= TimeSpan.Zero || lockoutTime + duration > at;

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        var preset = SelectedTimePreset.Value;
        TimePresetOptions = PresetOptions();
        SelectedTimePreset = TimePresetOptions.First(o => o.Value == preset);
        Rebuild();
    }

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        RunCommand.NotifyCanExecuteChanged();
        PivotCommand.NotifyCanExecuteChanged();
        UnlockCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
        WatchdogHistoryCommand.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pivot.Cancel();
            _report = null;
            _records = [];
            _run = null;
            Sources = [];
            Timeline = [];
        }
    }

    private List<Option<EventLogTimePreset>> PresetOptions() =>
        [.. new[] { EventLogTimePreset.BeforeLastLockout, EventLogTimePreset.LastHour, EventLogTimePreset.Last24Hours, EventLogTimePreset.Last7Days, EventLogTimePreset.Custom }
            .Select(p => new Option<EventLogTimePreset>(p, EventLogText.TimePreset(Localizer, p)))];

    // ---- Reads ------------------------------------------------------------------------------------------------------------

    private static string? SidOf(DirectoryObject account)
    {
        try
        {
            return account.ObjectSid;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The lockout policy: the resultant PSO's when the account has one, else the domain's (with its observation window).</summary>
    private async Task<(LockoutPolicy Policy, string? Source)> ReadPolicyAsync(IDirectorySession session, string? psoDn, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(psoDn))
        {
            var pso = await session.GetAsync(psoDn, ["msDS-LockoutThreshold", "msDS-LockoutObservationWindow", "msDS-LockoutDuration", Attr.Name], token).ConfigureAwait(false);
            if (pso is not null)
            {
                return (new LockoutPolicy(
                    pso.GetInt32("msDS-LockoutThreshold") ?? 0,
                    Interval(pso.GetInt64("msDS-LockoutObservationWindow")),
                    Interval(pso.GetInt64("msDS-LockoutDuration"))), pso.Name);
            }
        }

        var domain = await Services.Ui.Sessions.GetDomainPolicyAsync(token).ConfigureAwait(false);
        var head = await session.GetAsync(session.Info.DefaultNamingContext, [ObservationWindowAttribute], token).ConfigureAwait(false);
        return (new LockoutPolicy(domain.LockoutThreshold, Interval(head?.GetInt64(ObservationWindowAttribute)), domain.LockoutDuration), null);

        static TimeSpan? Interval(long? value) => value is { } v ? AdConvert.FromInterval(v) : null;
    }

    /// <summary>The DC whose NTDS Settings object wrote lockoutTime last (msDS-ReplAttributeMetaData), null when not known.</summary>
    private async Task<string?> OriginatingDcAsync(IDirectorySession session, CancellationToken token)
    {
        try
        {
            var user = await session.GetAsync(UserDn, [AttributeReplicationMetadata.AttributeName], token).ConfigureAwait(false);
            if (user is null || AttributeReplicationMetadata.Find(user.GetStrings(AttributeReplicationMetadata.AttributeName), Attr.LockoutTime) is not { OriginatingServerDn: { } serverDn })
            {
                return null;
            }

            var server = await session.GetAsync(serverDn, [Attr.DnsHostName], token).ConfigureAwait(false);
            return server?.GetString(Attr.DnsHostName);
        }
        catch (DirectoryException)
        {
            return null;
        }
    }

    private AccountLockoutState StateOf(DomainControllerRead read, IReadOnlyList<EventLogDomainController> controllers, IReadOnlyDictionary<string, IReadOnlyList<string>> addresses)
    {
        var isPdc = controllers.Any(c => c.IsPdcEmulator && DomainControllerCatalog.IsSameHost(c.Host, read.Server));
        var known = addresses.TryGetValue(read.Server, out var list) ? list : [];
        if (!read.Succeeded)
        {
            return new AccountLockoutState(read.Server, isPdc, ErrorKey: read.Error) { Addresses = known };
        }

        var obj = read.Find(UserDn);
        if (obj is null)
        {
            return new AccountLockoutState(read.Server, isPdc, ErrorKey: Localizer[L.EventLogsLockout.AccountNotOnDc]) { Addresses = known };
        }

        return new AccountLockoutState(
            read.Server,
            isPdc,
            obj.GetInt32(Attr.BadPwdCount),
            obj.GetFileTime(Attr.BadPasswordTime),
            obj.GetFileTime(Attr.LockoutTime),
            obj.GetFileTime(Attr.LastLogon)) { Addresses = known };
    }

    private (DateTimeOffset From, DateTimeOffset To)? WindowOf(IReadOnlyList<AccountLockoutState> states, DateTimeOffset now)
    {
        if (SelectedTimePreset.Value == EventLogTimePreset.BeforeLastLockout)
        {
            return LockoutInvestigation.DefaultWindow(states, now);
        }

        if (EventLogTimeWindows.Of(SelectedTimePreset.Value, now) is { } window)
        {
            return window;
        }

        var to = CustomTo is { } t ? Local(t) : now;
        var from = CustomFrom is { } f ? Local(f) : to - EventLogLimits.DefaultWindow;
        return from > to ? null : (from, to);
    }

    private DateTimeOffset Local(DateTime value)
    {
        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, Zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    private string KindText(string host, IReadOnlyList<AccountLockoutState> states, string? originating)
    {
        var parts = new List<string> { Localizer[L.EventLogs.KindDomainController] };
        if (states.Any(s => s.IsPdcEmulator && DomainControllerCatalog.IsSameHost(s.Server, host)))
        {
            parts.Add(Localizer[L.EventLogs.KindPdc]);
        }

        if (originating is not null && DomainControllerCatalog.IsSameHost(originating, host))
        {
            parts.Add(Localizer[L.EventLogsLockout.Originating]);
        }

        return string.Join(", ", parts);
    }

    // ---- Actions ----------------------------------------------------------------------------------------------------------

    /// <summary>"Odblokuj konto": the existing unlock operation through the change plan preview (rule 5).</summary>
    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task UnlockAsync()
    {
        var outcome = await Services.Ui.Coordinator.ApplyAsync(new ChangeRequest([UserDn], [new UnlockAccountOperation()], Localizer[L.Commands.Unlock])
        {
            ForcePreview = true,
        }).ConfigureAwait(true);
        if (outcome is ChangeOutcome.Executed)
        {
            await RunAsync().ConfigureAwait(true);
        }
    }

    private bool CanUnlock() => IsLockedNow && HasSession && !IsLoading;

    /// <summary>"Właściwości" of the account.</summary>
    [RelayCommand]
    private Task OpenAccountAsync() => Services.Ui.Dialogs.ShowPropertiesAsync(UserDn);

    /// <summary>"Historia w Watchdogu" of the account (when a Watchdog is connected).</summary>
    [RelayCommand(CanExecute = nameof(CanShowWatchdogHistory))]
    private void WatchdogHistory()
    {
        if (AccountGuid is { } guid)
        {
            Services.History?.Show(guid, _accountName);
        }
    }

    private bool CanShowWatchdogHistory() => Services.History?.CanShow(AccountGuid) == true;

    /// <summary>"Pokaż w modelu": the K5 model of the DC of an audit evidence row (or of a DC row).</summary>
    [RelayCommand(CanExecute = nameof(CanUseGroupPolicyCommand))]
    private async Task ShowInModelAsync(object? row)
    {
        var host = row switch
        {
            AuditEvidenceRow audit => audit.Host,
            LockoutDcRowViewModel dc => dc.Host,
            string text => text,
            _ => null,
        };
        if (host is not null && Session is { } session)
        {
            try
            {
                await Services.ShowInModelAsync(session, host, CancellationToken.None).ConfigureAwait(true);
            }
            catch (DirectoryException ex)
            {
                Error = Services.Ui.Errors.Translate(ex);
            }
        }
    }

    /// <summary>"Diagnostyka zasad grupy" (K6).</summary>
    [RelayCommand(CanExecute = nameof(CanUseGroupPolicyCommand))]
    private void OpenGroupPolicyDiagnostics(object? row) => Services.OpenGroupPolicyDiagnostics();

    private bool CanUseGroupPolicyCommand(object? row) => Services.GroupPolicy is not null && HasSession;

    // ---- Export -----------------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasReport))]
    private Task ExportCsvAsync() =>
        EventLogExport.ExportCsvAsync(Services.Ui, Services.Csv, Localizer[L.EventLogs.ExportCsv], Title, SourcesTable(null));

    [RelayCommand(CanExecute = nameof(HasReport))]
    private Task ExportHtmlAsync()
    {
        var subtitle = new List<string> { LockStateText, LastLockoutText, PolicyText, CountsText, WindowText };
        var tables = new List<EventLogExportTable>
        {
            SourcesTable(Localizer[L.EventLogsLockout.SourcesHeading]),
            new(Localizer[L.EventLogsLockout.TimelineHeading],
                [.. new[] { L.EventLogs.ColumnTime, L.EventLogsLockout.ColumnDc, L.EventLogsLockout.ColumnKind, L.EventLogs.ColumnId, L.EventLogsLockout.ColumnSource, L.EventLogsLockout.ColumnCodes }.Select(k => Localizer[k])],
                [.. Timeline.Select(t => (IReadOnlyList<string>)[t.TimeText, t.HostText, t.KindText, t.EventIdText, t.SourceText, t.CodeText])]),
            new(Localizer[L.EventLogsLockout.DomainControllersHeading],
                [.. new[] { L.EventLogsLockout.ColumnDc, L.EventLogsLockout.ColumnRoles, L.EventLogsLockout.ColumnLocked, L.EventLogsLockout.ColumnBadPwdCount, L.EventLogsLockout.ColumnBadPasswordTime, L.EventLogsLockout.ColumnLastLogon, L.EventLogs.ColumnCoverage }.Select(k => Localizer[k])],
                [.. DomainControllerRows.Select(d => (IReadOnlyList<string>)[d.Host, d.RolesText, d.LockedText, d.BadPasswordCountText, d.BadPasswordTimeText, d.LastLogonText, d.CoverageText])]),
            new(Localizer[L.EventLogsLockout.AuditHeading],
                [.. new[] { L.EventLogsLockout.ColumnDc, L.EventLogs.ColumnId, L.EventLogsLockout.ColumnSubcategory, L.EventLogs.ColumnState }.Select(k => Localizer[k])],
                [.. AuditRows.Select(a => (IReadOnlyList<string>)[a.Host, a.EventText, a.SubcategoryText, a.StateText])]),
        };
        return EventLogExport.ExportHtmlAsync(Services.Ui, Localizer[L.EventLogs.ExportHtml], Title, subtitle, [.. Notes.Select(n => n.Text)], tables);
    }

    private EventLogExportTable SourcesTable(string? heading) => new(
        heading,
        [.. new[]
        {
            L.EventLogsLockout.ColumnRank, L.EventLogsLockout.ColumnKind, L.EventLogsLockout.ColumnSource, L.EventLogsLockout.ColumnFailures,
            L.EventLogsLockout.ColumnBadPasswords, L.EventLogsLockout.ColumnContributing, L.EventLogsLockout.ColumnLockouts,
            L.EventLogsLockout.ColumnFirst, L.EventLogsLockout.ColumnLast, L.EventLogsLockout.ColumnDcs, L.EventLogsLockout.ColumnCodes, L.EventLogsLockout.ColumnHint,
        }.Select(k => Localizer[k])],
        [.. Sources.Select(s => (IReadOnlyList<string>)
        [
            s.Rank.ToString(Localizer.Culture), s.KindText, s.Value, s.FailuresText, s.BadPasswordsText, s.ContributingText, s.LockoutsText,
            s.FirstText, s.LastText, s.DomainControllersText, s.CodesText, string.Join(" ", new[] { s.ProxyText, s.DomainControllerText, s.RelatedText }.Where(t => !string.IsNullOrEmpty(t))),
        ])]);
}
