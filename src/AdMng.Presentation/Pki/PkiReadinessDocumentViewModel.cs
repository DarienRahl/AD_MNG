using System.ComponentModel;
using AdMng.Core.Directory;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Pki;

/// <summary>
/// <i>Gotowość na silne mapowanie i NTAuth</i> (P1, docs/dev/p-certificate-services.md 2.5, W6) – a document of the PKI area,
/// not the stage-S readiness check. Status rows (<i>OK</i> / <i>ostrzeżenie</i> / <i>problem</i> / <i>nie sprawdzono</i>) per area:
/// accounts with weak <c>altSecurityIdentities</c> (the enrollment expansion's mapped accounts, Tier 0 first), published
/// authentication templates whose certificates carry no SID extension, logon-capable enterprise CAs not in NTAuth, the DC
/// LDAPS certificates (the TLS probe runs only on <i>Sprawdź certyfikaty LDAPS</i> – decision 1; results cached by the window's
/// snapshot service are shown), the KDC certificates published on the DC objects and the KDC events row that says P5. The
/// snapshot rows appear first, the KDC and account rows when their reads finish. Read-only (rule 5), on the window's DC
/// (rule 4), in memory only; <i>Kopiuj</i> and the CSV export go to the clipboard or an operator-chosen file (rule 1).
/// </summary>
public sealed partial class PkiReadinessDocumentViewModel : PkiDocumentBase
{
    /// <summary>The rows an area shows before "i jeszcze N" (the copy and the export hold every row).</summary>
    public const int RowsShown = 100;

    private PkiSnapshot? _snapshot;
    private IReadOnlyList<PkiDomainController>? _domainControllers;
    private string? _domainControllersError;
    private PkiEnrollmentExpansion? _expansion;
    private string? _expansionError;
    private IReadOnlyList<DcTlsCheck>? _ldaps;
    private string? _ldapsError;
    private string? _domainDns;
    private DateTimeOffset? _readAt;
    private string? _server;
    private IDirectorySession? _session;
    private CancellationTokenSource? _probing;

    public PkiReadinessDocumentViewModel(PkiServices pki)
        : base(pki)
    {
        LastRunText = Localizer[L.PkiReadiness.NeverRun];
        LoadedText = LastRunText;
    }

    public override string Title => Localizer[L.PkiReadiness.Title];

    public override ObjectIcon Icon => ObjectIcon.Certificate;

    /// <summary>The progress while something is read, else the summary.</summary>
    public override string StatusText => Error ?? (IsLoading || IsCheckingLdaps ? ProgressText : LoadedText);

    /// <summary>"Odczyt kont (1 500)…" while the document reads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string ProgressText { get; private set; } = string.Empty;

    /// <summary>"Odczytano: 13.10.2026 12:00 na dc01.contoso.test" (or "Jeszcze nie odczytano.").</summary>
    [ObservableProperty]
    public partial string LastRunText { get; private set; } = string.Empty;

    /// <summary>The areas with their rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand), nameof(ExportCsvCommand))]
    public partial IReadOnlyList<PkiReadinessGroupViewModel> Groups { get; private set; } = [];

    /// <summary>"OK: 6 · Ostrzeżenia: 3 · Problemy: 2 · Nie sprawdzono: 2" of every row (empty before the first read).</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    /// <summary>The DC LDAPS probe runs (<i>Sprawdź certyfikaty LDAPS</i>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(CheckLdapsCommand))]
    public partial bool IsCheckingLdaps { get; private set; }

    /// <summary>The report reads or probes (the view shows the progress instead of the last read).</summary>
    public bool IsBusy => IsLoading || IsCheckingLdaps;

    /// <summary>Areas are shown.</summary>
    public bool HasResult => Groups.Count > 0;

    /// <summary>The window has a TLS probe (null: the LDAPS rows say <i>niedostępne</i>).</summary>
    public bool CanProbe => Pki.TlsProbe is not null;

    /// <summary>The probe that runs, or ran last (tests await it).</summary>
    internal Task? Probe { get; private set; }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsLoading))
        {
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    // ---- Loading --------------------------------------------------------------------------------------------------------------

    protected override async Task LoadCoreAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var refresh = IsRefresh;
        if (!ReferenceEquals(_session, session) || refresh)
        {
            // Another DC, a reconnect or F5: a probe still running belongs to what is read again.
            _probing?.Cancel();
        }

        _session = session;
        _domainDns = session.Info.DomainDnsName;
        ProgressText = Localizer[L.PkiReadiness.StageSnapshot];
        var snapshot = await Pki.Snapshot.GetAsync(refresh, null, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        _snapshot = snapshot;
        _readAt = Pki.Time.GetLocalNow();
        _server = session.Info.Server;
        _domainControllers = null;
        _domainControllersError = null;
        _expansion = null;
        _expansionError = null;
        _ldaps = Pki.Snapshot.DcCertificates;
        _ldapsError = null;
        Rebuild();

        ProgressText = Localizer[L.PkiReadiness.StageDomainControllers];
        try
        {
            _domainControllers = await Pki.Snapshot.GetDomainControllersAsync(refresh, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or TimeoutException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            _domainControllersError = Pki.Errors.Translate(ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Rebuild();

        ProgressText = Localizer.Format(L.PkiReadiness.StageAccounts, 0);
        var progress = Pki.Dispatcher.CreateProgress<PkiExpansionProgress>(p =>
        {
            if (!cancellationToken.IsCancellationRequested && IsLoading)
            {
                ProgressText = Localizer.Format(L.PkiReadiness.StageAccounts, p.Accounts.ToString("N0", Localizer.Culture));
            }
        });
        try
        {
            _expansion = await Pki.Snapshot.GetExpansionAsync(refresh, progress, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or TimeoutException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            _expansionError = Pki.Errors.Translate(ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ProgressText = string.Empty;
        Rebuild();
    }

    protected override void OnNotConnected()
    {
        _probing?.Cancel();
        _session = null;
        _snapshot = null;
        _domainControllers = null;
        _expansion = null;
        _ldaps = null;
        Groups = [];
        SummaryText = string.Empty;
    }

    /// <summary>The thresholds changed: the expiry warnings of the LDAPS and KDC rows follow.</summary>
    protected override void OnSettingsChanged() => Rebuild();

    /// <summary>The texts are rebuilt in the new language (nothing is read again).</summary>
    protected override void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Title));
        Rebuild();
    }

    protected override void RefreshCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        CheckLdapsCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// <i>Sprawdź certyfikaty LDAPS</i>: the TLS handshake with port 636 (and 3269 of a global catalog) of every DC of the window's
    /// domain (decision 1: only on this button; at most 4 at a time, 5 s each – the snapshot service's probe). A second press
    /// probes again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckLdaps))]
    private Task CheckLdapsAsync()
    {
        Probe = ProbeAsync();
        return Probe;
    }

    private bool CanCheckLdaps() => CanProbe && HasSession && !IsCheckingLdaps && _snapshot is not null;

    private async Task ProbeAsync()
    {
        _probing?.Cancel();
        using var probing = CancellationTokenSource.CreateLinkedTokenSource(Pki.Lifetime);
        _probing = probing;
        IsCheckingLdaps = true;
        ProgressText = Localizer[L.PkiReadiness.StageLdaps];
        try
        {
            _ldaps = await Pki.Snapshot.GetDcCertificatesAsync(_ldaps is not null, probing.Token).ConfigureAwait(true);
            _ldapsError = null;
        }
        catch (OperationCanceledException)
        {
            // Another probe, a new session or the window closed.
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _ldapsError = Pki.Errors.Translate(ex);
        }
        finally
        {
            if (ReferenceEquals(_probing, probing))
            {
                _probing = null;
                IsCheckingLdaps = false;
                ProgressText = string.Empty;
                if (!probing.IsCancellationRequested)
                {
                    Rebuild();
                }
            }
        }
    }

    /// <summary>Every area from what has been read so far (thresholds and language as they are now).</summary>
    private void Rebuild()
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var builder = new PkiReadinessRows(Pki, Localizer, snapshot, Pki.Settings.Current.Pki.Thresholds(), Pki.Time.GetUtcNow());
        Groups =
        [
            builder.Mappings(_expansion, _expansionError, IsLoading && _expansion is null && _expansionError is null),
            builder.AuthenticationTemplates(),
            builder.NtAuth(),
            builder.DomainControllerLdaps(_ldaps, _ldapsError, _domainDns ?? string.Empty),
            builder.KdcCertificates(_domainControllers, _domainControllersError, IsLoading && _domainControllers is null && _domainControllersError is null),
            builder.KdcEvents(),
        ];
        var statuses = Groups.SelectMany(g => g.AllRows).Select(r => r.Status).ToList();
        SummaryText = Summary(Localizer, statuses);
        LastRunText = _readAt is { } at ? Localizer.Format(L.PkiReadiness.LastRun, at.ToString("g", Localizer.Culture), _server ?? string.Empty)
            : Localizer[L.PkiReadiness.NeverRun];
        LoadedText = SummaryText.Length > 0 ? SummaryText : LastRunText;
        RefreshCommands();
    }

    // ---- Copy and export ------------------------------------------------------------------------------------------------------

    /// <summary><i>Kopiuj</i>: every row as text, area by area.</summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private void Copy()
    {
        if (Pki.Ui.Clipboard.SetText(ReportText()))
        {
            Pki.Ui.Notifications.Show(NotificationSeverity.Info, Localizer[L.PkiReadiness.Copied]);
        }
    }

    /// <summary>The report as plain text (<i>Kopiuj</i>).</summary>
    public string ReportText()
    {
        var lines = new List<string> { Title, LastRunText, SummaryText };
        foreach (var group in Groups)
        {
            lines.Add(string.Empty);
            lines.Add($"{group.Title} – {group.StatusText}");
            lines.AddRange(group.AllRows.Select(r => r.ToText()));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary><i>Eksportuj do CSV…</i>: every row to a file the operator picks (rule 1).</summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task ExportCsvAsync()
    {
        var title = Localizer[L.PkiReadiness.ExportCsv];
        var name = Lists.ListExportSnapshot.SuggestFileName(Title + " – " + (_domainDns ?? string.Empty), Pki.Time.GetLocalNow());
        var path = await Pki.Ui.Files.SaveFileAsync(title, name, [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var rows = Groups.SelectMany(g => g.AllRows.Select(r => (IReadOnlyList<string>)[g.Title, r.StatusText, r.Title, r.Detail, r.RemedyText])).ToList();
        var headers = new[] { L.PkiReadiness.ColumnArea, L.PkiReadiness.ColumnStatus, L.PkiReadiness.ColumnItem, L.PkiReadiness.ColumnDetail, L.PkiReadiness.ColumnRemedy }
            .Select(k => Localizer[k]).ToList();
        var delimiter = CsvFiles.ListSeparator(Pki.Ui.Regional.Culture);
        try
        {
            await CsvFiles.WriteAsync(path, writer => Pki.Csv.Write(writer, headers, rows, delimiter, protectFormulas: true)).ConfigureAwait(true);
            Pki.Ui.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Files.Exported, rows.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Pki.Ui.Dialogs.ShowErrorAsync(title, Pki.Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    // ---- Texts ----------------------------------------------------------------------------------------------------------------

    /// <summary>The resource key of a status (<c>PkiReady.Status.Problem</c>).</summary>
    public static string StatusKey(PkiReadinessStatus status) => "PkiReady.Status." + status;

    /// <summary>Every dynamic key of the report (both languages must have them).</summary>
    public static IEnumerable<string> AllKeys() => Enum.GetValues<PkiReadinessStatus>().Select(StatusKey);

    /// <summary>How bad a status is (problem, warning, not checked, OK).</summary>
    public static int Rank(PkiReadinessStatus status) => status switch
    {
        PkiReadinessStatus.Problem => 3,
        PkiReadinessStatus.Warning => 2,
        PkiReadinessStatus.NotChecked => 1,
        _ => 0,
    };

    /// <summary>"OK: 4 · Ostrzeżenia: 1 · Problemy: 2" (only the statuses that occur).</summary>
    public static string Summary(ILocalizer localizer, IEnumerable<PkiReadinessStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(statuses);
        var counts = statuses.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        var parts = new List<string>();
        void Add(PkiReadinessStatus status, string key)
        {
            if (counts.TryGetValue(status, out var count))
            {
                parts.Add(localizer.Format(key, count));
            }
        }

        Add(PkiReadinessStatus.Ok, L.PkiReadiness.SummaryOk);
        Add(PkiReadinessStatus.Warning, L.PkiReadiness.SummaryWarning);
        Add(PkiReadinessStatus.Problem, L.PkiReadiness.SummaryProblem);
        Add(PkiReadinessStatus.NotChecked, L.PkiReadiness.SummaryNotChecked);
        return string.Join(" · ", parts);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _probing?.Cancel();
            _snapshot = null;
            _expansion = null;
            _domainControllers = null;
            _ldaps = null;
        }

        base.Dispose(disposing);
    }
}
