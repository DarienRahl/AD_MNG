using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Pki;

/// <summary>The <i>Zdrowie</i> tab (with <i>Sprawdź adresy HTTP</i>) and the <i>Certyfikaty LDAPS</i> tab.</summary>
public sealed partial class PkiOverviewDocumentViewModel
{
    // ---- Health ------------------------------------------------------------------------------------------------------------

    public ObservableCollection<PkiHealthRowViewModel> HealthRows { get; } = [];

    public bool HasHealthRows => HealthRows.Count > 0;

    /// <summary>"OK: 9 · wygasa: 1 · wygasła: 0 · niedostępna: 1 · nieznany: 4".</summary>
    [ObservableProperty]
    public partial string HealthSummaryText { get; private set; } = string.Empty;

    /// <summary>The thresholds the states use (Settings → Ogólne → Certyfikaty).</summary>
    [ObservableProperty]
    public partial string ThresholdsText { get; private set; } = string.Empty;

    /// <summary>The window has an HTTP check (<i>Sprawdź adresy HTTP</i> says <i>niedostępne</i> otherwise).</summary>
    public bool IsHttpAvailable => Pki.HealthProbe is not null;

    /// <summary>The HTTP check runs.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckHttpCommand))]
    public partial bool IsCheckingHttp { get; private set; }

    /// <summary>
    /// What the tab knows about the HTTP check: not available here, not run yet, no URL to check, or what it found
    /// ("Adresy HTTP nie zostały sprawdzone…", "Sprawdzone adresy HTTP: 8 (OK: 6)."). The first two are the note under the
    /// button (<see cref="HttpNote"/>); the others are part of the summary of the tab. While the check runs, the line says how
    /// far it is.
    /// </summary>
    [ObservableProperty]
    public partial string HttpStatusText { get; private set; } = string.Empty;

    /// <summary>The HTTP check is not available in this window or has not run yet: what that means for the rows (a note, empty when the check ran).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHttpNote))]
    public partial string HttpNote { get; private set; } = string.Empty;

    public bool HasHttpNote => HttpNote.Length > 0;

    /// <summary>The results of the last HTTP check (OCSP included, which has no health row).</summary>
    public ObservableCollection<PkiHttpRowViewModel> HttpRows { get; } = [];

    public bool HasHttpRows => HttpRows.Count > 0;

    /// <summary>
    /// <i>Sprawdź adresy HTTP</i>: the AIA / CDP / OCSP URLs of the certificates, on the operator's button only (decision 1).
    /// The operation of the tab: the line says how far it is, with Stop; what it found is the rows and the summary of the tab,
    /// so it ends without a message - unless it was stopped or failed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckHttp))]
    private async Task CheckHttpAsync()
    {
        if (_snapshot is null)
        {
            return;
        }

        var total = PkiHealth.HttpUrls(_snapshot).Count;
        var title = Localizer[L.PkiOverviewStatus.HttpChecking];
        var key = L.PkiOverviewStatus.HttpChecking;
        string Stage(int done) => Localizer.Format(L.PkiDocumentStatus.Stage, title, PageStatusText.Progress(Localizer, done, total));
        using var scope = HealthStatus.Begin(
            total > 0 ? Stage(0) : Localizer.Format(L.PkiDocumentStatus.Working, title),
            new PageStatusLog(key, 0, total),
            PageProgress.Of(0, total));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_actions.Token, scope.Token);
        IsCheckingHttp = true;
        HttpStatusText = Localizer.Format(L.Pki.HttpChecking, 0, total);
        var progress = Pki.Dispatcher.CreateProgress<int>(done =>
        {
            HttpStatusText = Localizer.Format(L.Pki.HttpChecking, done, total);
            if (total > 0)
            {
                scope.Report(Stage(done), PageProgress.Of(done, total), new PageStatusLog(key, done, total));
            }
        });
        PageStatus? result = null;
        string? error = null;
        try
        {
            await Pki.Snapshot.CheckHttpAsync(progress, linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the operator: the results of before stay. The window closed, the session changed or F5 dropped the
            // snapshot: the check is of no interest any more and says nothing.
            if (scope.IsCancelling && !_actions.IsCancellationRequested)
            {
                var label = Localizer[L.Pki.CheckHttp];
                result = PageStatus.Attention(
                    Localizer.Format(L.PkiDocumentStatus.StoppedKept, title, label),
                    new PageStatusLog(L.PkiDocumentStatus.StoppedKept),
                    new PageStatusAction(label, CheckHttpCommand));
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            var code = ResultCode.Of(ex);
            error = Pki.Errors.Translate(ex);
            result = PageStatus.Error(
                PageStatusText.Failure(Localizer[L.PkiOverviewStatus.HttpFailed], error, Localizer[L.Feedback.RetryNext], code),
                new PageStatusLog(L.PkiOverviewStatus.HttpFailed).WithCode(code),
                new PageStatusAction(Localizer[L.Common.Retry], CheckHttpCommand));
        }
        finally
        {
            IsCheckingHttp = false;
        }

        if (IsDisposed)
        {
            return;
        }

        // The state first, then the operation: the line goes from "working" to the summary or the result.
        ShowHttpResults();
        EvaluateHealth(Pki.Settings.Current.Pki.Thresholds(), Pki.Time.GetUtcNow());
        if (error is not null)
        {
            HttpStatusText = error;
        }

        scope.Complete(result);
    }

    private bool CanCheckHttp() => IsHttpAvailable && _snapshot is not null && !IsCheckingHttp && HasSession && !IsDisposed;

    /// <summary>The health rows of the snapshot at <paramref name="now"/> with the window's HTTP results.</summary>
    private void EvaluateHealth(PkiHealthThresholds thresholds, DateTimeOffset now)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var http = Pki.Snapshot.HttpResults;
        var rows = PkiHealth.Evaluate(snapshot, thresholds, now, http);
        HealthRows.Clear();
        foreach (var row in rows)
        {
            var ca = row.CaGuid is { } id ? snapshot.EnterpriseCas.FirstOrDefault(c => c.ObjectGuid == id) : null;
            var item = Localizer[L.Pki.HealthItemKey(row.Item)];
            var numbered = row.Item is PkiHealthItem.AiaLocation or PkiHealthItem.CdpLocation or PkiHealthItem.DeltaCrlLocation
                or PkiHealthItem.KraCertificate or PkiHealthItem.NtAuthCertificate;
            var result = row.Url is { } url ? http?.FirstOrDefault(r => string.Equals(r.Url, url, StringComparison.Ordinal)) : null;
            HealthRows.Add(new PkiHealthRowViewModel(
                row,
                ca is null ? string.Empty : ca.DisplayName ?? ca.Name,
                numbered ? Localizer.Format(L.Pki.HealthItemNumbered, item, row.Index + 1) : item,
                PkiDisplay.Status(Localizer, row.Status),
                PkiDisplay.Date(Localizer, row.ValidUntil),
                result is null ? string.Empty : HttpOutcomeText(result)));
        }

        HealthSummaryText = Localizer.Format(L.Pki.HealthSummary,
            rows.Count(r => r.Status == PkiHealthStatus.Ok),
            rows.Count(r => r.Status == PkiHealthStatus.Expiring),
            rows.Count(r => r.Status == PkiHealthStatus.Expired),
            rows.Count(r => r.Status == PkiHealthStatus.Unavailable),
            rows.Count(r => r.Status == PkiHealthStatus.Unknown));
        ThresholdsText = Localizer.Format(L.Pki.HealthThresholds, thresholds.EndEntityWarningDays, thresholds.CaWarningDays);
        OnPropertyChanged(nameof(HasHealthRows));
        UpdateHealthLine();
    }

    /// <summary>The HTTP rows and the texts about the check from the window's last results.</summary>
    private void ShowHttpResults()
    {
        HttpRows.Clear();
        var results = Pki.Snapshot.HttpResults;
        foreach (var result in results ?? [])
        {
            HttpRows.Add(new PkiHttpRowViewModel(result, Localizer[L.Pki.UrlKindKey(result.Kind)], HttpOutcomeText(result)));
        }

        if (!IsCheckingHttp)
        {
            HttpStatusText = !IsHttpAvailable ? Localizer[L.Pki.HttpUnavailable]
                : results is null ? Localizer[L.Pki.HttpNotChecked]
                : results.Count == 0 ? Localizer[L.Pki.HttpNoUrls]
                : Localizer.Format(L.Pki.HttpChecked, results.Count, results.Count(r => r.Outcome == PkiHttpOutcome.Ok));
            HttpNote = !IsHttpAvailable || results is null ? HttpStatusText : string.Empty;
        }

        OnPropertyChanged(nameof(HasHttpRows));
        UpdateHealthLine();
    }

    /// <summary>
    /// The summary of the tab: what the health rows say and what the HTTP check found - the note that says the check was not
    /// run is no part of it, it stays under the button.
    /// </summary>
    private void UpdateHealthLine()
    {
        var found = HttpNote.Length > 0 || IsCheckingHttp ? string.Empty : HttpStatusText;
        HealthStatus.SetIdle(string.Join(" · ", new[] { HealthSummaryText, found }.Where(part => part.Length > 0)));
    }

    private string HttpOutcomeText(PkiHttpCheckResult result)
    {
        var text = Localizer[L.Pki.HttpKey(result.Outcome)];
        return result.StatusCode is { } status && result.Outcome != PkiHttpOutcome.NotFound
            ? Localizer.Format(L.Pki.HttpStatus, text, status.ToString(CultureInfo.InvariantCulture))
            : text;
    }

    // ---- LDAPS -------------------------------------------------------------------------------------------------------------

    public ObservableCollection<PkiLdapsRowViewModel> LdapsRows { get; } = [];

    public bool HasLdapsRows => LdapsRows.Count > 0;

    /// <summary>The window has a TLS probe (the tab says <i>niedostępne</i> otherwise; the KDC column is still filled).</summary>
    public bool IsLdapsAvailable => Pki.TlsProbe is not null;

    /// <summary>The DCs are probed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProbeLdapsCommand))]
    public partial bool IsProbingLdaps { get; private set; }

    /// <summary>"Nie sprawdzono…", "Sprawdzanie…", "Sprawdzone porty: 3; OK: 2; z problemem: 1.".</summary>
    [ObservableProperty]
    public partial string LdapsStatusText { get; private set; } = string.Empty;

    /// <summary>The KDC column's source (the certificates published on the DC objects; the KDC's own store is P4).</summary>
    public string KdcNote => Localizer[L.Pki.KdcNote];

    /// <summary><i>Sprawdź certyfikaty LDAPS</i>: the TLS handshake of every DC (636, 3269 for a GC), at most 4 at a time.</summary>
    [RelayCommand(CanExecute = nameof(CanProbeLdaps))]
    private Task ProbeLdapsAsync()
    {
        _ldapsActivated = true;
        return RunLdapsAsync(refresh: true);
    }

    private bool CanProbeLdaps() => _snapshot is not null && !IsProbingLdaps && HasSession && !IsDisposed;

    private Task ProbeOnActivationAsync() => RunLdapsAsync(refresh: false);

    /// <summary>
    /// The probe the operator started (the button, or the first activation of the tab): the operation of the tab - the line
    /// says that the domain controllers are being probed, with Stop; what it found is the rows and the summary of the tab, so
    /// it ends without a message - unless it was stopped or failed.
    /// </summary>
    private async Task RunLdapsAsync(bool refresh)
    {
        var title = Localizer[L.PkiDocumentStatus.LdapsChecking];
        using var scope = LdapsStatus.Begin(Localizer.Format(L.PkiDocumentStatus.Working, title), new PageStatusLog(L.PkiDocumentStatus.LdapsChecking));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_actions.Token, scope.Token);
        LdapsStatus.SetLasting(null);
        PageStatus? result = null;
        try
        {
            await LoadLdapsAsync(refresh, linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the operator: the results of before stay. The window closed, the session changed or F5 dropped the
            // snapshot: the probe is of no interest any more and says nothing.
            if (scope.IsCancelling && !_actions.IsCancellationRequested)
            {
                var label = Localizer[L.Pki.ProbeLdaps];
                result = PageStatus.Attention(
                    Localizer.Format(L.PkiDocumentStatus.StoppedKept, title, label),
                    new PageStatusLog(L.PkiDocumentStatus.StoppedKept),
                    new PageStatusAction(label, ProbeLdapsCommand));
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            SayLdapsFailure(ex);
        }

        scope.Complete(result);
    }

    /// <summary>
    /// The probe could not be made: the tab says why its checks are missing - an error that lasts until the next probe, with
    /// Retry (the probe sends nothing but a TLS handshake, so it is safe to repeat).
    /// </summary>
    private void SayLdapsFailure(Exception exception)
    {
        var reason = Pki.Errors.Translate(exception);
        var code = ResultCode.Of(exception);
        LdapsStatusText = reason;
        LdapsStatus.SetLasting(PageStatus.Error(
            PageStatusText.Failure(Localizer[L.PkiDocumentStatus.LdapsFailed], reason, Localizer[L.Feedback.RetryNext], code),
            new PageStatusLog(L.PkiDocumentStatus.LdapsFailed).WithCode(code),
            new PageStatusAction(Localizer[L.Common.Retry], ProbeLdapsCommand)));
    }

    /// <summary>The DCs (LDAP, the KDC column) and – with a probe – their LDAPS checks; <paramref name="refresh"/> probes again.</summary>
    private async Task LoadLdapsAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        IsProbingLdaps = true;
        LdapsStatusText = Localizer[L.Pki.LdapsChecking];
        try
        {
            var controllers = await Pki.Snapshot.GetDomainControllersAsync(refresh, cancellationToken).ConfigureAwait(true);
            IReadOnlyList<DcTlsCheck> checks = IsLdapsAvailable
                ? await Pki.Snapshot.GetDcCertificatesAsync(refresh, cancellationToken).ConfigureAwait(true)
                : [];
            cancellationToken.ThrowIfCancellationRequested();
            ShowLdaps(snapshot, controllers, checks);
            LdapsStatus.SetLasting(null);
        }
        finally
        {
            IsProbingLdaps = false;
            EvaluateLdaps();
        }
    }

    private void ShowLdaps(PkiSnapshot snapshot, IReadOnlyList<PkiDomainController> controllers, IReadOnlyList<DcTlsCheck> checks)
    {
        var now = Pki.Time.GetUtcNow();
        string Kdc(string host)
        {
            var dc = controllers.FirstOrDefault(d => string.Equals(d.HostName, host, StringComparison.OrdinalIgnoreCase));
            if (dc is null)
            {
                return string.Empty;
            }

            var info = KdcCertificateEvaluation.Evaluate(dc.Certificates, host, snapshot.TrustMaterial, now);
            var text = Localizer[PkiTextKeys.KdcKey(info.State)];
            return info.Best is { } best ? text + " – " + PkiDisplay.Day(Localizer, best.NotAfter) : text;
        }

        LdapsRows.Clear();
        if (checks.Count > 0)
        {
            foreach (var check in checks)
            {
                var leaf = check.Certificate?.Leaf;
                LdapsRows.Add(new PkiLdapsRowViewModel(check.Host, check.Port, check)
                {
                    PortText = PortText(check.Port),
                    OutcomeText = Localizer[PkiTextKeys.TlsKey(check.Outcome)],
                    ValidToText = PkiDisplay.Day(Localizer, leaf?.NotAfter),
                    SubjectText = leaf is null ? string.Empty : PkiDisplay.CommonName(leaf.Subject),
                    ChainStatusText = check.Certificate is { ChainStatus.Count: > 0 } info ? Localizer.Format(L.Pki.ChainStatus, string.Join(", ", info.ChainStatus)) : string.Empty,
                    KdcText = check.Port == PkiSnapshotService.LdapsPort ? Kdc(check.Host) : string.Empty,
                });
            }
        }
        else
        {
            // No probe in this host (or no DC answered the catalogue): the DCs with their KDC indicator only.
            foreach (var dc in controllers.Where(d => d.HostName is not null))
            {
                LdapsRows.Add(new PkiLdapsRowViewModel(dc.HostName!, PkiSnapshotService.LdapsPort, null)
                {
                    PortText = PortText(PkiSnapshotService.LdapsPort),
                    OutcomeText = Localizer[L.Pki.LdapsUnavailable],
                    KdcText = Kdc(dc.HostName!),
                });
            }
        }

        EvaluateLdaps();
        OnPropertyChanged(nameof(HasLdapsRows));
    }

    /// <summary>The LDAPS verdicts at the current end-entity threshold (decision 2) and the tab's summary.</summary>
    private void EvaluateLdaps()
    {
        var warningDays = Pki.Settings.Current.Pki.CertificateExpiryWarningDays;
        foreach (var row in LdapsRows)
        {
            var (text, problem, warning) = Verdict(row.Check, warningDays);
            row.ChecksText = text;
            row.IsProblem = problem;
            row.IsWarning = warning;
        }

        if (IsProbingLdaps)
        {
            return;
        }

        LdapsStatusText = !IsLdapsAvailable ? Localizer[L.Pki.LdapsUnavailable]
            : !_ldapsActivated ? Localizer[L.Pki.LdapsNotChecked]
            : LdapsRows.Count == 0 ? Localizer[L.Pki.LdapsNoDcs]
            : Localizer.Format(L.Pki.LdapsSummary, LdapsRows.Count, LdapsRows.Count(r => !r.IsProblem && !r.IsWarning), LdapsRows.Count(r => r.IsProblem || r.IsWarning));
        LdapsStatus.SetIdle(LdapsStatusText);
    }

    private (string Text, bool Problem, bool Warning) Verdict(DcTlsCheck? check, int warningDays)
    {
        if (check is null)
        {
            return (string.Empty, false, false);
        }

        if (check.Outcome != TlsProbeOutcome.Ok)
        {
            return (Localizer[PkiTextKeys.TlsKey(check.Outcome)], true, false);
        }

        if (check.Certificate is not { Leaf: not null } info)
        {
            return (Localizer[L.Pki.CheckUnparsable], true, false);
        }

        var problems = new List<string>();
        var warnings = new List<string>();
        if (!info.NameMatchesDc)
        {
            problems.Add(Localizer[L.Pki.CheckNameMismatch]);
        }

        if (!info.NameMatchesDomain)
        {
            warnings.Add(Localizer.Format(L.Pki.CheckNoDomainName, Pki.Sessions.Current?.Info.DomainDnsName ?? string.Empty));
        }

        if (!info.HasServerAuth)
        {
            problems.Add(Localizer[L.Pki.CheckNoServerAuth]);
        }

        if (!info.IsTimeValid)
        {
            problems.Add(Localizer[L.Pki.CheckExpired]);
        }
        else if (info.DaysLeft <= warningDays)
        {
            warnings.Add(Localizer.Format(L.Pki.CheckExpiring, info.DaysLeft));
        }

        if (!info.ChainsToAdRoot)
        {
            problems.Add(Localizer[L.Pki.CheckNoAdRoot]);
        }

        if (!info.IssuerInNtAuth)
        {
            warnings.Add(Localizer[L.Pki.CheckIssuerNotInNtAuth]);
        }

        var all = problems.Concat(warnings).ToList();
        return (all.Count == 0 ? Localizer[L.Pki.CheckOk] : string.Join("; ", all), problems.Count > 0, problems.Count == 0 && warnings.Count > 0);
    }

    private string PortText(int port) => port == PkiSnapshotService.GlobalCatalogLdapsPort
        ? Localizer.Format(L.Pki.PortText, port.ToString(CultureInfo.InvariantCulture))
        : port.ToString(CultureInfo.InvariantCulture);
}
