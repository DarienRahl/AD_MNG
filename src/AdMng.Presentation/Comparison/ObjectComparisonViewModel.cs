using System.Collections.ObjectModel;
using AdMng.Core.Comparison;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Comparison;

/// <summary>
/// "Porównaj z obiektem w innym połączeniu…" (design note docs/dev/multi-window.md 8): the selected user, group, computer
/// or contact of this window (side A) against the matching object of another open connection (side B), read-only and
/// side by side - attributes, membership and, for groups, members.
/// </summary>
/// <remarks>
/// Each side is read through a <see cref="ConnectionReadContext"/> of ONE session (reader, formatter, schema and policy
/// captured together): this window's pinned session for A, the other window's current session for B (rule 4). The
/// readers forward only reads (<see cref="ReadOnlyDirectoryReader"/>), so nothing here can write (rule 5 is not involved).
/// Results live in memory while the window is open; CSV is written only on "Eksportuj CSV…" (rule 1). Closing the window,
/// Stop and a new run cancel every read; nested membership runs last, in the background.
/// <para>The window has one status line (<see cref="Status"/>; track V, stage V2b, design note
/// docs/dev/v-interface-renewal.md "V2b-1 as built"): the comparison is the operation of the window ("Porównaj" is its
/// Cancel), the reads of nested membership and nested members that follow it are operations as well - one at a time -
/// and the line says how they ended. Why the tabs are empty - no match, several candidates, a read that failed, the
/// session of the window that ended - is the state of the window for as long as it lasts; at rest the line says which
/// object was found. That a side B connection was closed or changed is a note of that side, and that no other
/// connection is open a note of the connection box.</para>
/// </remarks>
public sealed partial class ObjectComparisonViewModel : WindowViewModelBase, IHasPageStatus
{
    private readonly ConnectionRegistry? _registry;
    private readonly ICsvCodec _csv;
    private readonly string _sourceDn;
    private readonly SerialCancellation _runs = new();
    private readonly SerialCancellation _nestedRuns = new();
    private readonly SerialCancellation _nestedMemberRuns = new();
    private readonly Guid _ownId;
    private ConnectionReadContext? _sideA;
    private ConnectionReadContext? _sideB;
    private IConnectionHandle? _observedTarget;
    private ObjectSnapshot? _snapshotA;
    private ObjectSnapshot? _snapshotB;
    private IReadOnlyList<AttributeComparison> _attributeComparisons = [];
    private List<AttributeComparisonRowViewModel> _attributeRows = [];
    private MembershipRead _directA = MembershipRead.Empty;
    private MembershipRead _directB = MembershipRead.Empty;
    private MembershipRead? _nestedA;
    private MembershipRead? _nestedB;
    private MembershipRead _membersA = MembershipRead.Empty;
    private MembershipRead _membersB = MembershipRead.Empty;
    private MembershipRead? _nestedMembersA;
    private MembershipRead? _nestedMembersB;
    private List<MembershipComparisonRowViewModel> _membershipRows = [];
    private List<MembershipComparisonRowViewModel> _memberRows = [];
    private bool _membershipComplete;
    private bool _membersComplete;
    private string? _matchedDn;
    private string? _targetDomain;
    private Task _lastRun = Task.CompletedTask;
    private Task _nestedLoad = Task.CompletedTask;
    private Task _nestedMembersLoad = Task.CompletedTask;
    private bool _initializing = true;

    /// <summary>Why the last comparison could not be read (an error), for as long as nothing else was compared.</summary>
    private PageStatus? _failure;

    /// <summary>Why the last comparison shows no pair of objects (an attention), for as long as nothing else was compared.</summary>
    private PageStatus? _outcome;

    public ObjectComparisonViewModel(DialogServices services, ICsvCodec csv, string distinguishedName, ConnectionRegistry? registry = null)
        : base(services)
    {
        Status = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        _csv = csv ?? throw new ArgumentNullException(nameof(csv));
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        _sourceDn = distinguishedName;
        _registry = registry;
        _ownId = services.Connection?.Id ?? Guid.Empty;
        Name = Core.Ldap.DistinguishedName.TryParse(distinguishedName, out var dn) && !dn.IsRoot ? dn.Name : distinguishedName;

        var settings = Services.Settings.Current.Comparison;
        OnlyDifferences = settings.OnlyDifferences;
        ShowVolatile = settings.ShowVolatile;
        CompareAddressesWithoutDomain = settings.CompareAddressesWithoutDomain;

        SideA = new ComparisonSideViewModel("A");
        SideB = new ComparisonSideViewModel("B");
        var identity = services.Connection?.Identity;
        var session = services.Sessions.Current;
        SideA.SetIdentity(identity?.Label ?? SessionLabel(session), identity?.Color);
        RebuildTargets();
        if (_registry is not null)
        {
            _registry.ConnectionsChanged += OnConnectionsChanged;
        }

        _initializing = false;
    }

    /// <summary>The source object's name (its RDN value until the object was read).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial string Name { get; private set; }

    /// <summary>"Porównanie: Łukasz Wiśniewski – contoso.test ↔ fabrikam.test".</summary>
    public override string WindowTitle => _targetDomain is { } target
        ? Localizer.Format(L.Comparison.WindowTitle, Name, SourceDomain, target)
        : Localizer.Format(L.Comparison.WindowTitleNoTarget, Name);

    /// <summary>The DN of the source object.</summary>
    public string SourceDistinguishedName => _sourceDn;

    /// <summary>
    /// The status of the window - what its one status line shows: the running comparison or the read of nested
    /// membership that follows it, how they ended, which object was found, and - as the state that lasts - why the tabs
    /// are empty or that the session of the window ended.
    /// </summary>
    public PageStatusHolder Status { get; }

    /// <summary>The object kind of the source (after the first read; Unknown before).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroup))]
    public partial ObjectKind Kind { get; private set; }

    /// <summary>The source is a group: the "Członkowie" tab is shown.</summary>
    public bool IsGroup => Kind == ObjectKind.Group;

    /// <summary>Side A (this window).</summary>
    public ComparisonSideViewModel SideA { get; }

    /// <summary>Side B (the other connection).</summary>
    public ComparisonSideViewModel SideB { get; }

    // ---- Target and matching --------------------------------------------------------------------------------

    /// <summary>The OTHER open connections (colour dot, domain, account).</summary>
    public ObservableCollection<ComparisonTargetViewModel> Targets { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    public partial ComparisonTargetViewModel? SelectedTarget { get; set; }

    /// <summary>Another connection is open.</summary>
    public bool HasTargets => Targets.Count > 0;

    /// <summary>"Brak innego otwartego połączenia. Otwórz drugie połączenie (Ctrl+Shift+N)…" while none is open, else null.</summary>
    public string? NoTargetsMessage => HasTargets ? null : Localizer[L.Comparison.NoOtherConnection];

    /// <summary>"Dopasuj według": the rules of the object kind.</summary>
    public ObservableCollection<Option<ObjectMatchRule>> MatchRules { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExplicit))]
    public partial Option<ObjectMatchRule>? SelectedRule { get; set; }

    /// <summary>"Wskazany obiekt": the DN or search text box is shown.</summary>
    public bool IsExplicit => SelectedRule?.Value == ObjectMatchRule.Explicit;

    /// <summary>A DN of the other connection, or a search text (ANR).</summary>
    [ObservableProperty]
    public partial string ExplicitText { get; set; } = string.Empty;

    /// <summary>The candidates of an ambiguous match; choosing one compares with it.</summary>
    public ObservableCollection<MatchCandidateViewModel> Candidates { get; } = [];

    [ObservableProperty]
    public partial MatchCandidateViewModel? SelectedCandidate { get; set; }

    /// <summary>The result of matching ("Znaleziono: CN=…", "Nie znaleziono", …) or the run's state.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>A failure of the last run (translated), or null.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(ExportCsvCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>Nested membership (or nested members) is being read in the background.</summary>
    [ObservableProperty]
    public partial bool IsNestedLoading { get; private set; }

    /// <summary>Both objects are in (the tabs show a comparison).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    public partial bool HasResult { get; private set; }

    /// <summary>Both sides read the same object (same objectGUID): differences come from read permissions.</summary>
    [ObservableProperty]
    public partial bool IsSameObject { get; private set; }

    // ---- Attributes tab ---------------------------------------------------------------------------------------

    /// <summary>The rows the toggles and the filter show.</summary>
    public BulkObservableCollection<AttributeComparisonRowViewModel> AttributeRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedValuesHeader), nameof(ValuesListName))]
    [NotifyCanExecuteChangedFor(nameof(CopyRowCommand))]
    public partial AttributeComparisonRowViewModel? SelectedAttributeRow { get; set; }

    /// <summary>"Wartości: Miejscowość" for the details pane, or null.</summary>
    public string? SelectedValuesHeader => SelectedAttributeRow is { } row ? Localizer.Format(L.Comparison.ValuesOf, row.DisplayName) : null;

    /// <summary>
    /// The accessible name of the values list: <see cref="SelectedValuesHeader"/>, or "Wartości zaznaczonego atrybutu" while
    /// no row is selected. Never null (WPF rejects a null <c>AutomationProperties.Name</c> as a binding error).
    /// </summary>
    public string ValuesListName => SelectedValuesHeader ?? Localizer[L.Comparison.ValuesOfSelection];

    /// <summary>"Tylko różnice" (on by default; remembered).</summary>
    [ObservableProperty]
    public partial bool OnlyDifferences { get; set; }

    /// <summary>"Pokaż atrybuty zmienne i identyfikujące" (off by default; remembered).</summary>
    [ObservableProperty]
    public partial bool ShowVolatile { get; set; }

    /// <summary>"Porównuj adresy bez domeny" (on by default; remembered).</summary>
    [ObservableProperty]
    public partial bool CompareAddressesWithoutDomain { get; set; }

    /// <summary>Filters the attribute rows by LDAP or display name.</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>"Atrybuty (2)": the tab with its number of differences.</summary>
    public string AttributesTabHeader => TabHeader(L.Comparison.TabAttributes, _attributeRows.Count(r => IsShownByVolatileToggle(r) && r.IsDifference), HasResult);

    // ---- Membership and members tabs ---------------------------------------------------------------------------

    public BulkObservableCollection<MembershipComparisonRowViewModel> MembershipRows { get; } = [];

    /// <summary>"Tylko różnice" of the membership tab (on by default).</summary>
    [ObservableProperty]
    public partial bool OnlyMembershipDifferences { get; set; } = true;

    /// <summary>"Pokaż zagnieżdżone" (on by default).</summary>
    [ObservableProperty]
    public partial bool ShowNested { get; set; } = true;

    /// <summary>"Członkostwo (1)"; without a count until the membership of both objects is read.</summary>
    public string MembershipTabHeader => TabHeader(L.Comparison.TabMembership, _membershipRows.Count(r => r.IsDifference), HasResult && _membershipComplete);

    /// <summary>Notes under the membership tab: the footnote, truncation and skipped groups.</summary>
    [ObservableProperty]
    public partial string MembershipNotes { get; private set; } = string.Empty;

    public BulkObservableCollection<MembershipComparisonRowViewModel> MemberRows { get; } = [];

    /// <summary>"Tylko różnice" of the members tab (on by default).</summary>
    [ObservableProperty]
    public partial bool OnlyMemberDifferences { get; set; } = true;

    /// <summary>"Pokaż członków zagnieżdżonych" (off by default; read on demand).</summary>
    [ObservableProperty]
    public partial bool ShowNestedMembers { get; set; }

    /// <summary>"Członkowie (3)"; without a count until the members of both groups are read.</summary>
    public string MembersTabHeader => TabHeader(L.Comparison.TabMembers, _memberRows.Count(r => r.IsDifference), HasResult && _membersComplete);

    /// <summary>Notes under the members tab: skipped foreign principals, truncation.</summary>
    [ObservableProperty]
    public partial string MemberNotes { get; private set; } = string.Empty;

    /// <summary>The run, nested membership and nested members started last (tests wait for the background reads).</summary>
    internal Task Pending => Task.WhenAll(_lastRun, _nestedLoad, _nestedMembersLoad);

    private string SourceDomain => _sideA?.Info.DomainDnsName ?? Services.Connection?.Identity?.DomainDnsName
        ?? Services.Sessions.Current?.Info.DomainDnsName ?? string.Empty;

    // ---- Lifetime ---------------------------------------------------------------------------------------------

    /// <summary>
    /// With the only other connection preselected, compares at once; otherwise reads the source object (side A and the
    /// rules of its kind) and waits for the operator's choice.
    /// </summary>
    public override async Task InitializeAsync()
    {
        if (SelectedTarget is null)
        {
            StatusText = HasTargets ? Localizer[L.Comparison.StatusChooseTarget] : string.Empty;
            UpdateSummary();
            await (_lastRun = LoadSourceAsync()).ConfigureAwait(true);
            return;
        }

        await (_lastRun = RunAsync(targetDn: null, CancellationToken.None)).ConfigureAwait(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runs.Cancel();
            _nestedRuns.Cancel();
            _nestedMemberRuns.Cancel();
            if (_registry is not null)
            {
                _registry.ConnectionsChanged -= OnConnectionsChanged;
            }

            ObserveTarget(null);
            _sideA?.Dispose();
            _sideB?.Dispose();
            _sideA = null;
            _sideB = null;
            Status.Operation?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnSessionEnded()
    {
        _runs.Cancel();
        _nestedRuns.Cancel();
        _nestedMemberRuns.Cancel();

        // What still runs ends without a word: the line says at once, and for good, that the session ended. Side A is
        // marked as ended without a note of its own - the window says it once. The cancelled run is no longer the
        // current one and does not end "running" itself: "Porównaj" must not stay the Cancel of nothing.
        Status.Operation?.Dispose();
        IsRunning = false;
        IsNestedLoading = false;
        SideA.MarkEnded(null);
        UpdateLasting();
        CompareCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        UpdateLasting();
        UpdateSummary();
        var selectedRule = SelectedRule?.Value;
        RebuildRules(selectedRule);
        RebuildRows();
        OnPropertyChanged(nameof(NoTargetsMessage));
        OnPropertyChanged(nameof(SelectedValuesHeader));
        OnPropertyChanged(nameof(ValuesListName));
    }

    // ---- Commands ---------------------------------------------------------------------------------------------

    /// <summary>"Porównaj": match by the chosen rule and compare ("Przerwij" cancels).</summary>
    [RelayCommand(CanExecute = nameof(CanCompare), IncludeCancelCommand = true)]
    private Task CompareAsync(CancellationToken cancellationToken) => _lastRun = RunAsync(targetDn: null, cancellationToken);

    private bool CanCompare() => SelectedTarget is not null && !IsSessionEnded;

    /// <summary>"Odśwież": reads both objects again (the matched object of the other connection, through its current session).</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => _lastRun = RunAsync(_matchedDn, CancellationToken.None);

    private bool CanRefresh() => SelectedTarget is not null && !IsSessionEnded && !IsRunning;

    /// <summary>"Eksportuj CSV…": only now, and only into the file the operator chooses (rule 1).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsvAsync()
    {
        if (_snapshotA is not { } a || _snapshotB is not { } b || _sideA is not { } sideA || _sideB is not { } sideB)
        {
            return;
        }

        var title = Localizer[L.Comparison.ExportTitle];
        var fileName = ComparisonCsv.SuggestedFileName(Localizer[L.Comparison.FileNamePrefix], Name, sideA.Info.DomainDnsName, sideB.Info.DomainDnsName);
        var path = await Services.Files.SaveFileAsync(title, fileName, [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var export = new ComparisonCsv(Localizer, sideA.Label, sideB.Label)
        {
            DistinguishedNameA = a.DistinguishedName,
            DistinguishedNameB = b.DistinguishedName,
            Attributes = [.. AttributeRows],
            Membership = [.. MembershipRows],
            Members = IsGroup ? [.. MemberRows] : [],
        };
        try
        {
            var delimiter = CsvFiles.ListSeparator(Services.Regional.Culture);
            await CsvFiles.WriteAsync(path, writer => export.Write(_csv, writer, delimiter)).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Files.Exported, export.RowCount, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    private bool CanExport() => HasResult && !IsRunning;

    /// <summary>Ctrl+C: the selected row as text (attribute, A, B, state); secrets stay masked.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyRow))]
    private void CopyRow(AttributeComparisonRowViewModel? row)
    {
        if ((row ?? SelectedAttributeRow) is not { } target)
        {
            return;
        }

        var text = string.Join(
            Environment.NewLine,
            $"{Localizer[L.Comparison.ColumnAttribute]}: {target.DisplayName} ({target.LdapName})",
            $"{SideA.Label}: {target.AllValuesTextA}",
            $"{SideB.Label}: {target.AllValuesTextB}",
            $"{Localizer[L.Comparison.ColumnState]}: {target.StateText}");
        ClipboardCopy.Value(Services.Clipboard, Services.Notifications, Localizer, text, Localizer[L.Comparison.CopiedRow]);
    }

    private bool CanCopyRow(AttributeComparisonRowViewModel? row) => (row ?? SelectedAttributeRow) is not null;

    /// <summary>"Zamknij": the window closes; every read still running is cancelled.</summary>
    [RelayCommand]
    private void Close() => RequestClose(true);

    // ---- Property changes -------------------------------------------------------------------------------------

    partial void OnSelectedTargetChanged(ComparisonTargetViewModel? value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        UpdateSummary();
        if (_initializing || value is null || IsDisposed)
        {
            return;
        }

        // Another connection: what was matched in the previous one means nothing there.
        _matchedDn = null;
        ForgetCandidates();
    }

    partial void OnSelectedRuleChanged(Option<ObjectMatchRule>? value)
    {
        if (value is null || _initializing || IsDisposed || Kind == ObjectKind.Unknown)
        {
            return;
        }

        var kind = Kind;
        _ = Services.Settings.UpdateAsync(s => s.Comparison.Rules[kind] = value.Value);
    }

    partial void OnSelectedCandidateChanged(MatchCandidateViewModel? value)
    {
        if (value is not null && !IsDisposed && !IsSessionEnded)
        {
            _lastRun = RunAsync(value.DistinguishedName, CancellationToken.None);
        }
    }

    partial void OnOnlyDifferencesChanged(bool value)
    {
        ApplyAttributeFilter();
        Remember(s => s.Comparison.OnlyDifferences = value);
    }

    partial void OnShowVolatileChanged(bool value)
    {
        ApplyAttributeFilter();
        Remember(s => s.Comparison.ShowVolatile = value);
    }

    partial void OnCompareAddressesWithoutDomainChanged(bool value)
    {
        // Only the diff changes: the objects are not read again.
        RebuildAttributeRows();
        Remember(s => s.Comparison.CompareAddressesWithoutDomain = value);
    }

    partial void OnFilterTextChanged(string value) => ApplyAttributeFilter();

    partial void OnOnlyMembershipDifferencesChanged(bool value) => ApplyMembershipFilter();

    partial void OnShowNestedChanged(bool value) => RebuildMembershipRows();

    partial void OnOnlyMemberDifferencesChanged(bool value) => ApplyMemberFilter();

    partial void OnShowNestedMembersChanged(bool value)
    {
        // Not while a comparison runs: it has not read the direct members yet, a second operation would take the line
        // from it - and with the line its token - and it starts this read itself when it is through.
        if (value && _nestedMembersA is null && HasResult && !IsRunning)
        {
            _nestedMembersLoad = LoadNestedMembersAsync();
        }
        else
        {
            RebuildMemberRows();
        }
    }

    partial void OnHasResultChanged(bool value)
    {
        RaiseTabHeaders();
        UpdateSummary();
    }

    // ---- What the status line says ----------------------------------------------------------------------------

    /// <summary>
    /// The state of the window that lasts, the most important first: its session ended; the last comparison could not
    /// be read; the last comparison shows no pair of objects, and why.
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(SessionEndedStatus ?? _failure ?? _outcome);

    /// <summary>
    /// The summary of the line: the object of the other connection the tabs show a comparison with, or - while nothing
    /// is compared and no connection is chosen - what to choose.
    /// </summary>
    private void UpdateSummary() => Status.SetIdle(
        HasResult && _snapshotB is { } found ? Localizer.Format(L.Comparison.StatusFound, found.DistinguishedName)
        : SelectedTarget is null && HasTargets ? Localizer[L.Comparison.StatusChooseTarget]
        : null);

    /// <summary>
    /// The comparison ended without a pair of objects: <paramref name="text"/> says why (also as
    /// <see cref="StatusText"/>) - the state of the window until something else is compared.
    /// </summary>
    /// <param name="key">The resource key of the text (the log copy).</param>
    /// <param name="text">The localized text.</param>
    private void SayOutcome(string key, string text)
    {
        StatusText = text;
        _outcome = PageStatus.Attention(PageStatusText.Failure(text, null), new PageStatusLog(key), canClose: false);
        UpdateLasting();
    }

    /// <summary>
    /// The candidates of an ambiguous match were found through a connection, or a session, that is no longer the one
    /// to compare with: they go, and with them what the last comparison said about its match ("choose one").
    /// </summary>
    private void ForgetCandidates()
    {
        Candidates.Clear();
        if (_outcome is not null)
        {
            _outcome = null;
            UpdateLasting();
        }
    }

    // ---- The run ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads A, matches in B (unless <paramref name="targetDn"/> is given), reads B, diffs, then reads direct membership;
    /// nested membership follows in the background.
    /// </summary>
    private async Task RunAsync(string? targetDn, CancellationToken cancellationToken)
    {
        if (IsDisposed || SelectedTarget is not { } target)
        {
            return;
        }

        var run = _runs.Next();
        _nestedRuns.Cancel();
        _nestedMemberRuns.Cancel();

        // The comparison is the operation of the window: the line says "working" from here on, Stop - in the line or
        // "Porównaj" as its Cancel - ends it as "stopped", and however this method is left the window does not stay in
        // "working". A newer run, the end of a session and closing the window that owns this comparison (its lifetime)
        // cancel it without a word. The cancel of the command (CompareCancelCommand) is the operator's Stop as well.
        using var comparing = Status.Begin(
            Localizer.Format(L.ComparisonStatus.Comparing, Name, target.Label), new PageStatusLog(L.ComparisonStatus.Comparing));
        using var stop = cancellationToken.Register(comparing.Cancel);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run, comparing.Token, Services.Connection?.Lifetime ?? CancellationToken.None);
        var ct = linked.Token;

        // Why the last comparison showed nothing is over: this one decides.
        _failure = null;
        _outcome = null;
        UpdateLasting();
        IsRunning = true;
        ErrorText = null;
        StatusText = Localizer[L.Comparison.StatusReading];
        try
        {
            var session = RequireSession();
            var (sideA, sideB) = PinSides(session, target);
            if (sideB is null)
            {
                SayOutcome(L.Comparison.TargetNotConnected, Localizer[L.Comparison.TargetNotConnected]);
                return;
            }

            SideA.ClearEnded();
            SideB.ClearEnded();
            SideA.SetIdentity(sideA.Label, sideA.Color ?? SideA.Color);
            SideB.SetIdentity(sideB.Label, sideB.Color);
            _targetDomain = sideB.Info.DomainDnsName;
            OnPropertyChanged(nameof(WindowTitle));

            var readerA = sideA.Reader;
            var readerB = sideB.Reader;
            var snapshotA = await Task.Run(() => ComparisonReader.ReadAsync(readerA, _sourceDn, ct), ct).ConfigureAwait(true);
            if (!_runs.IsCurrent(run))
            {
                return;
            }

            if (snapshotA is null)
            {
                SayOutcome(L.Comparison.SourceNotFound, Localizer.Format(L.Comparison.SourceNotFound, sideA.Info.DomainDnsName));
                return;
            }

            Name = snapshotA.Name;
            if (Kind != snapshotA.Kind || MatchRules.Count == 0)
            {
                Kind = snapshotA.Kind;
                RebuildRules(Services.Settings.Current.Comparison.RuleFor(Kind));
            }

            if (!ObjectMatcher.CanCompare(snapshotA.Kind))
            {
                SayOutcome(L.Comparison.NotComparable, Localizer[L.Comparison.NotComparable]);
                return;
            }

            // A DN matched before the other window reconnected to another domain names nothing there: match again.
            if (targetDn is not null && !NamingContextFilter.Contains(sideB.Info.DefaultNamingContext, targetDn))
            {
                targetDn = null;
            }

            if (targetDn is null)
            {
                var rule = SelectedRule?.Value ?? ObjectMatchRule.Automatic;
                var explicitText = ExplicitText;
                var match = await Task.Run(() => ObjectMatcher.MatchAsync(readerB, snapshotA.Object, rule, explicitText, ct), ct).ConfigureAwait(true);
                if (!_runs.IsCurrent(run))
                {
                    return;
                }

                ShowMatch(match, sideB);
                if (match.Match is not { } found)
                {
                    ShowSourceOnly(snapshotA, sideA, ct);
                    return;
                }

                targetDn = found.DistinguishedName;
            }

            var dnB = targetDn;
            var snapshotB = await Task.Run(() => ComparisonReader.ReadAsync(readerB, dnB, ct), ct).ConfigureAwait(true);
            if (!_runs.IsCurrent(run))
            {
                return;
            }

            if (snapshotB is null)
            {
                SayOutcome(L.Comparison.TargetNotFound, Localizer.Format(L.Comparison.TargetNotFound, sideB.Info.DomainDnsName));
                return;
            }

            _matchedDn = snapshotB.DistinguishedName;
            var policyA = await sideA.Policy.WaitAsync(ct).ConfigureAwait(true);
            var policyB = await sideB.Policy.WaitAsync(ct).ConfigureAwait(true);
            var now = Services.Time.GetUtcNow();
            _snapshotA = snapshotA;
            _snapshotB = snapshotB;
            SideA.Summary = Summary(snapshotA, sideA, policyA, now);
            SideB.Summary = Summary(snapshotB, sideB, policyB, now);
            IsSameObject = snapshotA.ObjectGuid is { } guid && guid == snapshotB.ObjectGuid;
            StatusText = Localizer.Format(L.Comparison.StatusFound, snapshotB.DistinguishedName);
            UpdateSummary();
            RebuildAttributeRows();

            // The membership tabs of the previous pair go now: a run stopped or failing while they load leaves them empty
            // (headers without a count), never the previous object's groups next to these attributes.
            _nestedA = _nestedB = _nestedMembersA = _nestedMembersB = null;
            _directA = _directB = _membersA = _membersB = MembershipRead.Empty;
            _membershipComplete = _membersComplete = false;
            HasResult = true;
            RebuildMembershipRows();
            RebuildMemberRows();

            // Direct membership: both sides at the same time (each with its own reader and request limit).
            var directA = Task.Run(() => ComparisonReader.ReadDirectMembershipAsync(readerA, snapshotA, policyA.DomainSid, ct), ct);
            var directB = Task.Run(() => ComparisonReader.ReadDirectMembershipAsync(readerB, snapshotB, policyB.DomainSid, ct), ct);
            await Task.WhenAll(directA, directB).ConfigureAwait(true);
            if (!_runs.IsCurrent(run))
            {
                return;
            }

            _directA = directA.Result;
            _directB = directB.Result;
            _membershipComplete = true;
            RebuildMembershipRows();
            if (snapshotA.Kind == ObjectKind.Group && snapshotB.Kind == ObjectKind.Group)
            {
                var membersA = Task.Run(() => ComparisonReader.ReadMembersAsync(readerA, snapshotA, ct), ct);
                var membersB = Task.Run(() => ComparisonReader.ReadMembersAsync(readerB, snapshotB, ct), ct);
                await Task.WhenAll(membersA, membersB).ConfigureAwait(true);
                if (!_runs.IsCurrent(run))
                {
                    return;
                }

                _membersA = membersA.Result;
                _membersB = membersB.Result;
            }

            _membersComplete = true;
            RebuildMemberRows();
            IsRunning = false;

            // The comparison is there - its result is the content of the tabs, and the line says which object was
            // found. What follows in the background is an operation of its own (it takes the line from this one).
            _nestedLoad = LoadNestedAsync(snapshotA, snapshotB, readerA, readerB);
            if (ShowNestedMembers && IsGroup)
            {
                _nestedMembersLoad = LoadNestedMembersAsync();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (_runs.IsCurrent(run) || cancellationToken.IsCancellationRequested)
            {
                StatusText = Localizer[L.Comparison.StatusCancelled];
            }

            // Only a comparison the operator stopped says so: the tabs may not show everything, and "Porównaj"
            // compares again.
            if (comparing.IsCancelling)
            {
                var again = Localizer[L.Comparison.Compare];
                comparing.CompleteAttention(
                    Localizer.Format(L.ComparisonStatus.Stopped, Name, again),
                    new PageStatusLog(L.ComparisonStatus.Stopped),
                    new PageStatusAction(again, CompareCommand));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SessionEndedException or InvalidOperationException)
        {
            if (_runs.IsCurrent(run))
            {
                MarkEndedSide();
            }
        }
        catch (DirectoryException ex)
        {
            if (_runs.IsCurrent(run))
            {
                // The tabs are empty, or not complete, and this is why: the state of the window until the next
                // comparison, with Retry (the same pair again when it is known).
                var code = ResultCode.Of(ex);
                ErrorText = Errors.Translate(ex);
                StatusText = string.Empty;
                _failure = PageStatus.Error(
                    PageStatusText.Failure(Localizer.Format(L.ComparisonStatus.Failed, Name), ErrorText, Localizer[L.Feedback.RetryNext], code),
                    new PageStatusLog(L.ComparisonStatus.Failed).WithCode(code),
                    new PageStatusAction(Localizer[L.Common.Retry], RefreshCommand),
                    canClose: false);
                UpdateLasting();
            }
        }
        finally
        {
            if (_runs.IsCurrent(run) || cancellationToken.IsCancellationRequested)
            {
                IsRunning = false;
            }
        }
    }

    /// <summary>Side A only (no other connection chosen yet): the source object's summary and the rules of its kind.</summary>
    private async Task LoadSourceAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        var run = _runs.Next();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run, Services.Connection?.Lifetime ?? CancellationToken.None);
        var ct = linked.Token;
        using var side = ConnectionReadContext.Create(
            _ownId, Services.Connection?.Identity, session, Services.Sessions.Formatter, Services.Sessions.GetDomainPolicyAsync(CancellationToken.None));
        try
        {
            var reader = side.Reader;
            var snapshot = await Task.Run(() => ComparisonReader.ReadAsync(reader, _sourceDn, ct), ct).ConfigureAwait(true);
            if (snapshot is null || !_runs.IsCurrent(run))
            {
                return;
            }

            Name = snapshot.Name;
            Kind = snapshot.Kind;
            RebuildRules(Services.Settings.Current.Comparison.RuleFor(Kind));
            var policy = await side.Policy.WaitAsync(ct).ConfigureAwait(true);
            if (_runs.IsCurrent(run))
            {
                SideA.Summary = Summary(snapshot, side, policy, Services.Time.GetUtcNow());
            }
        }
        catch (OperationCanceledException)
        {
            // Closed, or a comparison started meanwhile.
        }
        catch (Exception ex) when (ex is DirectoryException or ObjectDisposedException or SessionEndedException)
        {
            // Shown by "Porównaj" (which reads the object again); side A stays empty until then.
        }
    }

    /// <summary>Nested membership of both sides (last, in the background; slow in large domains).</summary>
    private async Task LoadNestedAsync(ObjectSnapshot a, ObjectSnapshot b, IDirectoryReader readerA, IDirectoryReader readerB)
    {
        var token = _nestedRuns.Next();

        // An operation of the window as well: the line says so with Stop, and a new comparison replaces it without a word.
        using var reading = Status.Begin(Localizer[L.Comparison.NestedLoading], new PageStatusLog(L.Comparison.NestedLoading));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, reading.Token, Services.Connection?.Lifetime ?? CancellationToken.None);
        var ct = linked.Token;
        var directA = _directA;
        var directB = _directB;
        IsNestedLoading = true;
        try
        {
            var nestedA = Task.Run(() => ComparisonReader.ReadNestedMembershipAsync(readerA, a, directA, ct), ct);
            var nestedB = Task.Run(() => ComparisonReader.ReadNestedMembershipAsync(readerB, b, directB, ct), ct);
            await Task.WhenAll(nestedA, nestedB).ConfigureAwait(true);
            if (!_nestedRuns.IsCurrent(token))
            {
                return;
            }

            _nestedA = nestedA.Result;
            _nestedB = nestedB.Result;
            RebuildMembershipRows();
        }
        catch (OperationCanceledException)
        {
            // Superseded or closed: nothing to say. Stopped by the operator: what is compared, and Refresh.
            if (reading.IsCancelling)
            {
                var again = Localizer[L.Comparison.Refresh];
                reading.CompleteAttention(
                    Localizer.Format(L.ComparisonStatus.NestedStopped, again),
                    new PageStatusLog(L.ComparisonStatus.NestedStopped),
                    new PageStatusAction(again, RefreshCommand));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SessionEndedException)
        {
            if (_nestedRuns.IsCurrent(token))
            {
                MarkEndedSide();
            }
        }
        catch (DirectoryException ex)
        {
            if (_nestedRuns.IsCurrent(token))
            {
                // The tab keeps the direct membership: a result that can be closed, with Retry.
                var code = ResultCode.Of(ex);
                reading.CompleteError(
                    PageStatusText.Failure(
                        Localizer[L.ComparisonStatus.NestedFailed], Errors.Translate(ex), Localizer[L.ComparisonStatus.NestedFailedNext], code),
                    new PageStatusLog(L.ComparisonStatus.NestedFailed).WithCode(code),
                    new PageStatusAction(Localizer[L.Common.Retry], RefreshCommand));
            }
        }
        finally
        {
            if (_nestedRuns.IsCurrent(token))
            {
                IsNestedLoading = false;
            }
        }
    }

    /// <summary>"Pokaż członków zagnieżdżonych": the nested members of both groups, read on demand.</summary>
    private async Task LoadNestedMembersAsync()
    {
        if (_snapshotA is not { } a || _snapshotB is not { } b || _sideA is not { } sideA || _sideB is not { } sideB)
        {
            return;
        }

        var token = _nestedMemberRuns.Next();

        // One read at a time says what it is doing: the nested members wait for the nested membership of the same
        // comparison (both are started at its end), then this read is the operation of the window.
        await WhenOverAsync(_nestedLoad).ConfigureAwait(true);
        if (!_nestedMemberRuns.IsCurrent(token) || IsDisposed)
        {
            return;
        }

        using var reading = Status.Begin(Localizer[L.ComparisonStatus.ReadingNestedMembers], new PageStatusLog(L.ComparisonStatus.ReadingNestedMembers));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, reading.Token, Services.Connection?.Lifetime ?? CancellationToken.None);
        var ct = linked.Token;
        var membersA = _membersA;
        var membersB = _membersB;
        var readerA = sideA.Reader;
        var readerB = sideB.Reader;
        IsNestedLoading = true;
        try
        {
            var nestedA = Task.Run(() => ComparisonReader.ReadNestedMembersAsync(readerA, a, membersA, ct), ct);
            var nestedB = Task.Run(() => ComparisonReader.ReadNestedMembersAsync(readerB, b, membersB, ct), ct);
            await Task.WhenAll(nestedA, nestedB).ConfigureAwait(true);
            if (!_nestedMemberRuns.IsCurrent(token))
            {
                return;
            }

            _nestedMembersA = nestedA.Result;
            _nestedMembersB = nestedB.Result;
            RebuildMemberRows();
        }
        catch (OperationCanceledException)
        {
            // Superseded or closed: nothing to say. Stopped by the operator: what is compared, and Refresh.
            if (reading.IsCancelling)
            {
                var again = Localizer[L.Comparison.Refresh];
                reading.CompleteAttention(
                    Localizer.Format(L.ComparisonStatus.NestedMembersStopped, again),
                    new PageStatusLog(L.ComparisonStatus.NestedMembersStopped),
                    new PageStatusAction(again, RefreshCommand));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SessionEndedException)
        {
            if (_nestedMemberRuns.IsCurrent(token))
            {
                MarkEndedSide();
            }
        }
        catch (DirectoryException ex)
        {
            if (_nestedMemberRuns.IsCurrent(token))
            {
                // The tab keeps the direct members: a result that can be closed, with Retry.
                var code = ResultCode.Of(ex);
                reading.CompleteError(
                    PageStatusText.Failure(
                        Localizer[L.ComparisonStatus.NestedMembersFailed],
                        Errors.Translate(ex),
                        Localizer[L.ComparisonStatus.NestedMembersFailedNext],
                        code),
                    new PageStatusLog(L.ComparisonStatus.NestedMembersFailed).WithCode(code),
                    new PageStatusAction(Localizer[L.Common.Retry], RefreshCommand));
            }
        }
        finally
        {
            if (_nestedMemberRuns.IsCurrent(token))
            {
                IsNestedLoading = false;
            }
        }
    }

    /// <summary>Waits until a background read is over, however it ended (it says its own failure).</summary>
    private static async Task WhenOverAsync(Task read)
    {
        try
        {
            await read.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Said, or not, by that read.
        }
    }

    /// <summary>
    /// The read contexts of the run: A from this window's pinned session, B from the other window's current session. The
    /// contexts of a previous run are released.
    /// </summary>
    private (ConnectionReadContext A, ConnectionReadContext? B) PinSides(IDirectorySession session, ComparisonTargetViewModel target)
    {
        var previousA = _sideA;
        var previousB = _sideB;

        // Side A reads through the window's pinned session only; pinned through the window's context it ends with it.
        var own = Services.Connection?.Pin();
        if (own is not null && !own.IsPinnedTo(session))
        {
            own.Dispose();
            own = null;
        }

        own ??= ConnectionReadContext.Create(_ownId, Services.Connection?.Identity, session, Services.Sessions.Formatter, Services.Sessions.GetDomainPolicyAsync(CancellationToken.None));
        var handle = _registry?.Find(target.Id);
        var other = handle?.Pin();

        // The results on screen were read through the previous side B: they stay (with their label, formatter, readers
        // and export names) only while the new side B reads the same session; another connection, another session of
        // the same window (reconnected, other DC) or none drops them before anything is relabelled or read.
        var sameSideB = other is not null && other.ReadsSameSessionAs(previousB);
        previousA?.Dispose();
        previousB?.Dispose();
        _sideA = own;
        _sideB = other;
        ObserveTarget(other is null ? null : handle);
        if (!sameSideB)
        {
            ClearResult();
        }

        return (own, other);
    }

    /// <summary>
    /// What matching found: one object (the comparison goes on), or why there is none to compare with - several
    /// candidates to choose from, too many, nothing - which is the state of the window until the next comparison.
    /// </summary>
    private void ShowMatch(MatchResult match, ConnectionReadContext sideB)
    {
        Candidates.Clear();
        switch (match.Status)
        {
            case MatchStatus.Found:
                StatusText = Localizer.Format(L.Comparison.StatusFound, match.Candidates[0].DistinguishedName);
                break;
            case MatchStatus.Ambiguous:
                foreach (var candidate in match.Candidates)
                {
                    Candidates.Add(MatchCandidateViewModel.From(candidate));
                }

                SayOutcome(L.Comparison.StatusAmbiguous, Localizer.Format(L.Comparison.StatusAmbiguous, match.Candidates.Count));
                break;
            case MatchStatus.TooMany:
                SayOutcome(L.Comparison.StatusTooMany, Localizer[L.Comparison.StatusTooMany]);
                break;
            case MatchStatus.OutsideNamingContext:
                SayOutcome(L.Comparison.StatusOutside, Localizer.Format(L.Comparison.StatusOutside, sideB.Info.DomainDnsName));
                break;
            case MatchStatus.NoValue:
                var noValue = match.Rule == ObjectMatchRule.Explicit ? L.Comparison.StatusNoExplicitText : L.Comparison.StatusNoValue;
                SayOutcome(noValue, Localizer[noValue]);
                break;
            default:
                if (match.Rule == ObjectMatchRule.Explicit)
                {
                    SayOutcome(L.Comparison.StatusNotFoundExplicit, Localizer[L.Comparison.StatusNotFoundExplicit]);
                }
                else
                {
                    SayOutcome(
                        L.Comparison.StatusNotFound,
                        Localizer.Format(L.Comparison.StatusNotFound, string.Join(", ", match.RulesTried.Select(r => Localizer[ComparisonText.RuleKey(r)]))));
                }

                break;
        }
    }

    /// <summary>No match: side A shows its object, side B and the tabs are empty.</summary>
    private void ShowSourceOnly(ObjectSnapshot a, ConnectionReadContext sideA, CancellationToken cancellationToken)
    {
        _snapshotA = a;
        ClearResult();
        _ = SetSourceSummaryAsync(a, sideA, cancellationToken);
    }

    /// <summary>Drops the comparison on screen: side B's object, the rows of every tab and the reads behind them.</summary>
    private void ClearResult()
    {
        _snapshotB = null;
        _matchedDn = null;
        HasResult = false;
        IsSameObject = false;
        SideB.Summary = null;
        _nestedA = _nestedB = _nestedMembersA = _nestedMembersB = null;
        _directA = _directB = _membersA = _membersB = MembershipRead.Empty;
        _membershipComplete = _membersComplete = false;
        _attributeComparisons = [];
        _attributeRows = [];
        _membershipRows = [];
        _memberRows = [];
        SelectedAttributeRow = null;
        AttributeRows.ReplaceAll([]);
        MembershipRows.ReplaceAll([]);
        MemberRows.ReplaceAll([]);
        MembershipNotes = string.Empty;
        MemberNotes = string.Empty;
        RaiseTabHeaders();
    }

    private async Task SetSourceSummaryAsync(ObjectSnapshot a, ConnectionReadContext sideA, CancellationToken cancellationToken)
    {
        try
        {
            var policy = await sideA.Policy.WaitAsync(cancellationToken).ConfigureAwait(true);
            SideA.Summary = Summary(a, sideA, policy, Services.Time.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            // A newer run took over.
        }
    }

    private ObjectSummaryViewModel Summary(ObjectSnapshot snapshot, ConnectionReadContext side, DomainPolicy policy, DateTimeOffset now) =>
        ObjectSummaryViewModel.Create(snapshot.Object, Localizer, side.Formatter, side.Schema, policy, now, side.Info.DomainNetBiosName);

    /// <summary>A read failed because a session ended: side B's (window closed, reconnected), else this window's.</summary>
    private void MarkEndedSide()
    {
        if (_sideB is { IsEnded: true } sideB)
        {
            var closed = _registry?.Find(sideB.ConnectionId) is null;
            SideB.MarkEnded(Localizer.Format(closed ? L.Comparison.SideClosed : L.Comparison.SideChanged, SideB.Label));
            StatusText = string.Empty;
        }
        else if (_sideA is { IsEnded: true } || Session is null)
        {
            // This window's own session: said by the status line (the state of the window), not by a note of side A.
            // The same sentence as SessionEndedStatus, for a read that noticed the end before the window was told.
            SideA.MarkEnded(null);
            StatusText = string.Empty;
            _failure = PageStatus.Error(NoSessionMessage, new PageStatusLog(L.WindowSession.Ended), canClose: false);
        }
        else
        {
            ErrorText = Localizer[L.WindowSession.Ended];
            _failure = PageStatus.Error(ErrorText, new PageStatusLog(L.WindowSession.Ended), canClose: false);
        }

        UpdateLasting();
        IsRunning = false;
        IsNestedLoading = false;
    }

    // ---- Rows -------------------------------------------------------------------------------------------------

    private void RebuildRows()
    {
        RebuildAttributeRows();
        RebuildMembershipRows();
        RebuildMemberRows();
    }

    private void RebuildAttributeRows()
    {
        if (_snapshotA is not { } a || _snapshotB is not { } b || _sideA is not { } sideA || _sideB is not { } sideB)
        {
            return;
        }

        _attributeComparisons = ObjectDiffer.Compare(a, b, new ComparisonOptions { CompareAddressesWithoutDomain = CompareAddressesWithoutDomain });
        _attributeRows = [.. _attributeComparisons.Select(c => new AttributeComparisonRowViewModel(c, Localizer, a.Schema, b.Schema, sideA.Formatter, sideB.Formatter))];
        ApplyAttributeFilter();
    }

    private void ApplyAttributeFilter()
    {
        var selected = SelectedAttributeRow;
        var filter = FilterText.Trim();
        AttributeRows.ReplaceAll(_attributeRows.Where(r =>
            IsShownByVolatileToggle(r)
            && (!OnlyDifferences || r.IsDifference)
            && (filter.Length == 0 || r.LdapName.Contains(filter, StringComparison.OrdinalIgnoreCase) || TextFolding.Contains(r.DisplayName, filter))));
        SelectedAttributeRow = selected is not null && AttributeRows.Contains(selected) ? selected : null;
        OnPropertyChanged(nameof(AttributesTabHeader));
    }

    /// <summary>A row "Pokaż atrybuty zmienne i identyfikujące" lets through.</summary>
    private bool IsShownByVolatileToggle(AttributeComparisonRowViewModel row) => ShowVolatile || !row.IsVolatile;

    private void RebuildMembershipRows()
    {
        IEnumerable<MembershipEntry> Side(MembershipRead direct, MembershipRead? nested) =>
            ShowNested && nested is not null ? direct.Entries.Concat(nested.Entries) : direct.Entries;

        _membershipRows = HasResult
            ? [.. MembershipComparer.Compare(Side(_directA, _nestedA), Side(_directB, _nestedB)).Select(c => new MembershipComparisonRowViewModel(c, Localizer))]
            : [];
        var notes = new List<string> { Localizer[L.Comparison.MembershipFootnote] };
        if (_directA.IsTruncated || _directB.IsTruncated || _nestedA?.IsTruncated == true || _nestedB?.IsTruncated == true)
        {
            notes.Add(Localizer.Format(L.Comparison.ListTruncated, ComparisonReader.MaxMembership));
        }

        MembershipNotes = string.Join('\n', notes);
        ApplyMembershipFilter();
    }

    private void ApplyMembershipFilter()
    {
        MembershipRows.ReplaceAll(_membershipRows.Where(r => !OnlyMembershipDifferences || r.IsDifference));
        OnPropertyChanged(nameof(MembershipTabHeader));
    }

    private void RebuildMemberRows()
    {
        IEnumerable<MembershipEntry> Side(MembershipRead direct, MembershipRead? nested) =>
            ShowNestedMembers && nested is not null ? direct.Entries.Concat(nested.Entries) : direct.Entries;

        _memberRows = HasResult && IsGroup
            ? [.. MembershipComparer.Compare(Side(_membersA, _nestedMembersA), Side(_membersB, _nestedMembersB)).Select(c => new MembershipComparisonRowViewModel(c, Localizer))]
            : [];
        var notes = new List<string>();
        var skipped = _membersA.Skipped + _membersB.Skipped;
        if (skipped > 0)
        {
            notes.Add(Localizer.Format(L.Comparison.MembersSkipped, skipped));
        }

        if (_membersA.IsTruncated || _membersB.IsTruncated || _nestedMembersA?.IsTruncated == true || _nestedMembersB?.IsTruncated == true)
        {
            notes.Add(Localizer.Format(L.Comparison.ListTruncated, ComparisonReader.MaxMembership));
        }

        MemberNotes = string.Join('\n', notes);
        ApplyMemberFilter();
    }

    private void ApplyMemberFilter()
    {
        MemberRows.ReplaceAll(_memberRows.Where(r => !OnlyMemberDifferences || r.IsDifference));
        OnPropertyChanged(nameof(MembersTabHeader));
    }

    private void RaiseTabHeaders()
    {
        OnPropertyChanged(nameof(AttributesTabHeader));
        OnPropertyChanged(nameof(MembershipTabHeader));
        OnPropertyChanged(nameof(MembersTabHeader));
    }

    private string TabHeader(string key, int differences, bool withCount) =>
        withCount ? Localizer.Format(L.Comparison.TabWithCount, Localizer[key], differences) : Localizer[key];

    // ---- Targets ----------------------------------------------------------------------------------------------

    private void RebuildRules(ObjectMatchRule? select)
    {
        var choices = ObjectMatcher.Choices(Kind);
        MatchRules.Clear();
        foreach (var rule in choices)
        {
            MatchRules.Add(new Option<ObjectMatchRule>(rule, Localizer[ComparisonText.RuleKey(rule)]));
        }

        var wasInitializing = _initializing;
        _initializing = true;
        SelectedRule = MatchRules.FirstOrDefault(o => o.Value == select) ?? MatchRules[0];
        _initializing = wasInitializing;
    }

    /// <summary>The other connected windows (never this one); the selection is kept by id, the only other one preselected.</summary>
    private void RebuildTargets()
    {
        var selected = SelectedTarget?.Id;
        var wasInitializing = _initializing;

        // A combo bound to the list writes null while it is rebuilt: that is not the operator's choice.
        _initializing = true;
        Targets.Clear();
        foreach (var handle in _registry?.Connections ?? [])
        {
            if (handle.Id == _ownId || handle.Identity is not { } identity)
            {
                continue;
            }

            Targets.Add(new ComparisonTargetViewModel(handle.Id, identity.Label, identity.Color, identity.Server));
        }

        // The chosen connection stays chosen; the only other one is preselected when nothing was chosen; a closed one is
        // never replaced silently by another.
        SelectedTarget = Targets.FirstOrDefault(t => t.Id == selected) ?? (selected is null && Targets.Count == 1 ? Targets[0] : null);
        _initializing = wasInitializing;
        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(NoTargetsMessage));
        UpdateSummary();
        CompareCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private void OnConnectionsChanged(object? sender, EventArgs e) => _ = Services.Dispatcher.RunAsync(() =>
    {
        if (!IsDisposed)
        {
            RebuildTargets();
        }
    });

    /// <summary>Follows the target window: a session change or its closing marks side B ended (its results stay).</summary>
    private void ObserveTarget(IConnectionHandle? handle)
    {
        if (ReferenceEquals(handle, _observedTarget))
        {
            return;
        }

        if (_observedTarget is { } previous)
        {
            previous.SessionChanged -= OnTargetSessionChanged;
            previous.Closed -= OnTargetClosed;
        }

        _observedTarget = handle;
        if (handle is not null)
        {
            handle.SessionChanged += OnTargetSessionChanged;
            handle.Closed += OnTargetClosed;
        }
    }

    private void OnTargetSessionChanged(object? sender, EventArgs e) => _ = Services.Dispatcher.RunAsync(() =>
    {
        if (!IsDisposed && ReferenceEquals(sender, _observedTarget) && _sideB is not null)
        {
            _runs.Cancel();
            _nestedRuns.Cancel();
            _nestedMemberRuns.Cancel();
            Status.Operation?.Dispose();
            IsRunning = false;
            IsNestedLoading = false;

            // The candidates were found through the previous session; the results stay visible under their label.
            ForgetCandidates();
            SideB.MarkEnded(Localizer.Format(L.Comparison.SideChanged, SideB.Label));
        }
    });

    private void OnTargetClosed(object? sender, EventArgs e)
    {
        // The handle dropped its subscribers already; only this view model's reference is released.
        _ = Services.Dispatcher.RunAsync(() =>
        {
            if (IsDisposed || !ReferenceEquals(sender, _observedTarget))
            {
                return;
            }

            _observedTarget = null;
            _runs.Cancel();
            _nestedRuns.Cancel();
            _nestedMemberRuns.Cancel();
            Status.Operation?.Dispose();
            IsRunning = false;
            IsNestedLoading = false;
            SideB.MarkEnded(Localizer.Format(L.Comparison.SideClosed, SideB.Label));
        });
    }

    private void Remember(Action<Settings.AppSettings> change)
    {
        if (!_initializing && !IsDisposed)
        {
            _ = Services.Settings.UpdateAsync(change);
        }
    }

    private static string SessionLabel(IDirectorySession? session) => session is null
        ? string.Empty
        : session.Info.BoundIdentity.Length > 0 ? $"{session.Info.DomainDnsName} · {session.Info.BoundIdentity}" : session.Info.DomainDnsName;
}
