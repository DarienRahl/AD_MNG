using AdMng.Core.Computers;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Readiness;

/// <summary>
/// The Watchdog (address, reachable, Kerberos, role, health – through the application's Watchdog connection),
/// Korespondencja (classic Outlook and its profile – the Outlook diagnostics of Korespondencja) and this computer (Windows
/// release, WebView2 runtime).
/// </summary>
public sealed partial class ReadinessChecker
{
    /// <summary>The Watchdog health checks named in a row at most (the Watchdog tab lists them all).</summary>
    private const int MaxHealthIssues = 5;

    private async Task WatchdogAsync(RunContext context, List<ReadinessResult> rows, CancellationToken cancellationToken)
    {
        if (_services.Watchdog is not { } watchdog)
        {
            rows.AddRange(ReadinessCodes.Of(ReadinessArea.Watchdog).Select(code => NotChecked(code, Localizer[L.Readiness.NotAvailable])));
            return;
        }

        if (!watchdog.HasServiceUrl)
        {
            rows.Add(Row(ReadinessCodes.WatchdogConfigured, ReadinessStatus.Warning, Localizer[L.Readiness.WatchdogNotConfigured], Localizer[L.Readiness.RemedyWatchdogAddress]));
            rows.AddRange(ReadinessCodes.Of(ReadinessArea.Watchdog).Skip(1).Select(code => NotChecked(code, Localizer[L.Readiness.WatchdogNoAddress])));
            return;
        }

        var host = watchdog.HostText;
        rows.Add(Ok(ReadinessCodes.WatchdogConfigured, Localizer.Format(L.Readiness.WatchdogAddress, host), host));
        MeDto me;
        try
        {
            watchdog.EnsureConfigured();
            me = await watchdog.Client.GetMeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            cancellationToken.ThrowIfCancellationRequested();
            WatchdogFailure(watchdog, ex, host, rows);
            return;
        }

        rows.Add(Ok(ReadinessCodes.WatchdogReachable, Localizer.Format(L.Readiness.WatchdogAnswered, host), host));
        rows.Add(Ok(ReadinessCodes.WatchdogAuthentication, Localizer[L.Readiness.WatchdogKerberosOk], host));
        rows.Add(me.Roles.Contains(WatchdogRole.Admin)
            ? Ok(ReadinessCodes.WatchdogRole, Localizer.Format(L.Readiness.WatchdogRoleOk, Localizer[WatchdogDisplay.RoleKey(WatchdogRole.Admin)]), host)
            : me.Roles.Contains(WatchdogRole.Viewer)
                ? Ok(ReadinessCodes.WatchdogRole, Localizer.Format(L.Readiness.WatchdogRoleViewer, Localizer[WatchdogDisplay.RoleKey(WatchdogRole.Viewer)]), host)
                : Row(ReadinessCodes.WatchdogRole, ReadinessStatus.Warning, Localizer[L.Readiness.WatchdogRoleNone], Localizer[L.Readiness.RemedyWatchdogRole], host));
        await AddAsync(rows, ReadinessCodes.WatchdogHealth, () => WatchdogHealthAsync(watchdog, context, host, cancellationToken), host).ConfigureAwait(false);
    }

    /// <summary>The rows of a failed first call: which step failed (reachable, Kerberos, role) and what to do.</summary>
    private void WatchdogFailure(WatchdogConnectionService watchdog, Exception exception, string host, List<ReadinessResult> rows)
    {
        var text = watchdog.Describe(exception);
        var skipped = Localizer[L.Readiness.WatchdogSkipped];
        var kind = (exception as WatchdogClientException)?.Kind;
        if (kind is WatchdogClientErrorKind.Unauthorized or WatchdogClientErrorKind.Forbidden)
        {
            rows.Add(Ok(ReadinessCodes.WatchdogReachable, Localizer.Format(L.Readiness.WatchdogAnswered, host), host));
            if (kind == WatchdogClientErrorKind.Unauthorized)
            {
                rows.Add(Row(ReadinessCodes.WatchdogAuthentication, ReadinessStatus.Error, text, Localizer[L.Readiness.RemedyWatchdogKerberos], host));
                rows.Add(NotChecked(ReadinessCodes.WatchdogRole, skipped, host: host));
            }
            else
            {
                rows.Add(Ok(ReadinessCodes.WatchdogAuthentication, Localizer[L.Readiness.WatchdogKerberosOk], host));
                rows.Add(Row(ReadinessCodes.WatchdogRole, ReadinessStatus.Error, text, Localizer[L.Readiness.RemedyWatchdogRole], host));
            }

            rows.Add(NotChecked(ReadinessCodes.WatchdogHealth, skipped, host: host));
            return;
        }

        var (remedy, documentation) = kind switch
        {
            WatchdogClientErrorKind.CertificateInvalid or WatchdogClientErrorKind.CertificateUntrusted or WatchdogClientErrorKind.CertificateChanged =>
                (L.Readiness.RemedyWatchdogCertificate, L.Readiness.DocWatchdogTls),
            WatchdogClientErrorKind.ApiVersionMismatch => (L.Readiness.RemedyWatchdogVersion, L.Readiness.DocWatchdogOrder),
            _ => (L.Readiness.RemedyWatchdogUnreachable, L.Readiness.DocWatchdogDns),
        };
        rows.Add(Row(ReadinessCodes.WatchdogReachable, ReadinessStatus.Error, text, Localizer[remedy], host, documentation));
        rows.AddRange(ReadinessCodes.Of(ReadinessArea.Watchdog).Skip(2).Select(code => NotChecked(code, skipped, host: host)));
    }

    /// <summary>The service's own health summary: the watched DC and its checks (Warning/Error named), and the watched domain.</summary>
    private async Task<ReadinessResult> WatchdogHealthAsync(WatchdogConnectionService watchdog, RunContext context, string host, CancellationToken cancellationToken)
    {
        StatusDto status;
        try
        {
            status = await watchdog.Client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (WatchdogClientException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Row(ReadinessCodes.WatchdogHealth, ReadinessStatus.Error, watchdog.Describe(ex), Localizer[L.Readiness.RemedyWatchdogUnreachable], host);
        }

        watchdog.ReportStatus(status);
        var checks = status.Checks.Where(c => c.State is HealthState.Warning or HealthState.Error).ToList();
        var states = checks.Select(c => c.State).Append(status.Directory.State).ToList();
        var worst = states.Contains(HealthState.Error) ? ReadinessStatus.Error
            : states.Contains(HealthState.Warning) ? ReadinessStatus.Warning
            : ReadinessStatus.Ok;
        var issues = checks
            .OrderByDescending(c => c.State == HealthState.Error)
            .Take(MaxHealthIssues)
            .Select(c => $"{Localizer.HealthText(c.Code)} – {Localizer.HealthStateName(c.State)}")
            .ToList();
        if (status.Directory.State is HealthState.Warning or HealthState.Error)
        {
            issues.Insert(0, $"{Localizer[L.Readiness.AreaDirectory]} – {Localizer.HealthStateName(status.Directory.State)}");
        }

        var text = issues.Count == 0
            ? Localizer.Format(L.Readiness.WatchdogHealthOk, status.ServiceVersion)
            : Localizer.Format(L.Readiness.WatchdogHealthIssues, status.ServiceVersion, string.Join("; ", issues));
        var domain = context.Session.Info.DomainDnsName;
        if (!string.IsNullOrWhiteSpace(status.Domain)
            && !string.Equals(status.Domain.Trim().TrimEnd('.'), domain.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
        {
            text += " " + Localizer.Format(L.Readiness.WatchdogOtherDomain, status.Domain, domain);
            worst = worst == ReadinessStatus.Error ? worst : ReadinessStatus.Warning;
        }

        return worst == ReadinessStatus.Ok
            ? Ok(ReadinessCodes.WatchdogHealth, text, host)
            : Row(ReadinessCodes.WatchdogHealth, worst, text, Localizer[L.Readiness.RemedyWatchdogHealth], host);
    }

    // ---- Korespondencja ---------------------------------------------------------------------------------------------------------

    private async Task CorrespondenceAsync(RunContext context, List<ReadinessResult> rows, CancellationToken cancellationToken)
    {
        if (_services.Outlook is not { Availability: not OutlookAvailability.NotSupported } outlook)
        {
            rows.AddRange(ReadinessCodes.Of(ReadinessArea.Correspondence).Select(code => NotChecked(code, Localizer[L.Readiness.NotAvailable])));
            return;
        }

        if (outlook.Availability == OutlookAvailability.NotInstalled)
        {
            rows.Add(Row(ReadinessCodes.CorrespondenceOutlook, ReadinessStatus.Warning, Localizer[L.Readiness.OutlookNotInstalled], Localizer[L.Readiness.RemedyOutlook]));
            rows.Add(NotChecked(ReadinessCodes.CorrespondenceProfile, Localizer[L.Readiness.OutlookSkipped]));
            return;
        }

        rows.Add(Ok(ReadinessCodes.CorrespondenceOutlook, Localizer[L.Readiness.OutlookInstalled]));
        if (context.Request.Depth == ReadinessDepth.Quick)
        {
            rows.Add(NotChecked(ReadinessCodes.CorrespondenceProfile, Localizer[L.Readiness.NotCheckedQuick]));
            return;
        }

        await AddAsync(rows, ReadinessCodes.CorrespondenceProfile, () => OutlookProfileAsync(outlook, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Connects to Outlook (starting it when it is not running) and reads its diagnostics without a test item; the session is released.</summary>
    private async Task<ReadinessResult> OutlookProfileAsync(IOutlookClient outlook, CancellationToken cancellationToken)
    {
        var connected = await outlook.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (connected.Session is not { } session)
        {
            var failure = connected.Failure ?? new OutlookFailure(OutlookFailureKind.Unknown, OutlookFailureCodes.Unknown, null, null);
            return Row(ReadinessCodes.CorrespondenceProfile, ReadinessStatus.Warning, CorrespondenceText.FailureText(Localizer, failure), Localizer[L.Readiness.RemedyOutlookProfile]);
        }

        try
        {
            var diagnostics = await session.DiagnoseAsync(createTestItem: false, cancellationToken).ConfigureAwait(false);
            return Ok(ReadinessCodes.CorrespondenceProfile,
                Localizer.Format(L.Readiness.OutlookProfileOk, diagnostics.Version, diagnostics.ProfileName, Number(diagnostics.Accounts.Count)));
        }
        catch (OutlookException ex)
        {
            return Row(ReadinessCodes.CorrespondenceProfile, ReadinessStatus.Warning, CorrespondenceText.FailureText(Localizer, ex.Failure), Localizer[L.Readiness.RemedyOutlookProfile]);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- This computer --------------------------------------------------------------------------------------------------------------

    private Task ThisComputerAsync(List<ReadinessResult> rows)
    {
        rows.Add(WindowsRelease(_services.Computer));
        rows.Add(WebView2(_services.Computer));
        return Task.CompletedTask;
    }

    /// <summary>The Windows release of this computer against the release catalogue of the computer review (K1).</summary>
    private ReadinessResult WindowsRelease(ILocalComputerInfo computer)
    {
        if (!computer.IsWindows)
        {
            return Row(ReadinessCodes.ComputerWindows, ReadinessStatus.Error, Localizer.Format(L.Readiness.WindowsNotWindows, computer.OperatingSystem), Localizer[L.Readiness.RemedyWindows]);
        }

        var build = computer.Build is { } number ? Number(number) + (computer.UpdateBuildRevision is { } ubr ? "." + Number(ubr) : string.Empty) : "?";
        var today = DateOnly.FromDateTime(_services.Time.GetLocalNow().DateTime);
        var match = _services.Releases.Match(computer.OperatingSystem, computer.Build is { } b ? $"10.0 ({Number(b)})" : null, today);
        var name = match.Release is { } release && !computer.OperatingSystem.Contains(release.Release, StringComparison.OrdinalIgnoreCase)
            ? $"{computer.OperatingSystem} ({release.Release})"
            : computer.OperatingSystem;
        var end = match.EndOfSupport?.ToString("d", Localizer.Culture) ?? string.Empty;
        return match.State switch
        {
            SupportState.Supported => Ok(ReadinessCodes.ComputerWindows, Localizer.Format(L.Readiness.WindowsSupported, name, build, end)),
            SupportState.EndingSoon => Row(ReadinessCodes.ComputerWindows, ReadinessStatus.Warning, Localizer.Format(L.Readiness.WindowsEndingSoon, name, build, end),
                Localizer[L.Readiness.RemedyWindows]),
            SupportState.Ended => Row(ReadinessCodes.ComputerWindows, ReadinessStatus.Error, Localizer.Format(L.Readiness.WindowsEnded, name, build, end), Localizer[L.Readiness.RemedyWindows]),
            _ => Ok(ReadinessCodes.ComputerWindows, Localizer.Format(L.Readiness.WindowsUnknownRelease, name, build)),
        };
    }

    private ReadinessResult WebView2(ILocalComputerInfo computer)
    {
        if (!computer.CanProbeWebView2)
        {
            return NotChecked(ReadinessCodes.ComputerWebView2, Localizer[L.Readiness.NotAvailable]);
        }

        if (computer.IsWebView2Disabled)
        {
            return NotChecked(ReadinessCodes.ComputerWebView2, Localizer[L.Readiness.WebView2Disabled]);
        }

        return computer.WebView2Version is { Length: > 0 } version
            ? Ok(ReadinessCodes.ComputerWebView2, Localizer.Format(L.Readiness.WebView2Ok, version))
            : Row(ReadinessCodes.ComputerWebView2, ReadinessStatus.Warning, Localizer[L.Readiness.WebView2Missing], Localizer[L.Readiness.RemedyWebView2]);
    }
}
