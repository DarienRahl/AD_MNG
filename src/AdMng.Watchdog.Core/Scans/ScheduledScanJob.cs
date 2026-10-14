using AdMng.Core.Directory;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// IWatchdogJob running the scheduled scanners (sections 10.12–10.15) once the baseline is complete: the permission
/// self-check (first iteration, then every <c>PermissionSelfCheckInterval</c>), account and password expiry on their
/// intervals, the lockout monitor every <c>LockoutRecheckInterval</c>, and every minute the burst and (optional)
/// password-spray detectors. A non-transient directory error of one scanner is logged and does not stop the others;
/// a transient one fails the iteration (the host retries with backoff).
/// </summary>
public sealed class ScheduledScanJob : IWatchdogJob
{
    public const string JobName = "scans";

    private const string SelfCheckScan = "selfCheck";
    private const string AccountExpiryScan = "accountExpiry";
    private const string PasswordExpiryScan = "passwordExpiry";
    private const string LockoutScan = "lockout";

    private readonly ScanJobServices _services;
    private readonly ILogger<ScheduledScanJob> _logger;
    private readonly ScanEventWriter _writer;
    private readonly AccountExpiryScanner _accountExpiry;
    private readonly PasswordExpiryScanner _passwordExpiry;
    private readonly LockoutMonitor _lockouts;
    private readonly BurstDetector _bursts;
    private readonly PasswordSprayDetector _spray;
    private readonly Dictionary<string, DateTimeOffset> _lastRun = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal ScheduledScanJob(ScanJobServices services, ILogger<ScheduledScanJob> logger)
    {
        _services = services;
        _logger = logger;
        _writer = new ScanEventWriter(services.Store, services.Events, services.Time, services.Severity);
        var evaluator = new ExpiryEvaluator(services.Notices, _writer);
        _accountExpiry = new AccountExpiryScanner(evaluator, services.Notices, services.Calendar);
        _passwordExpiry = new PasswordExpiryScanner(evaluator, services.Notices, services.Calendar);
        _lockouts = new LockoutMonitor(_writer, services.LockoutPolicies);
        _bursts = new BurstDetector(services.Events, _writer, services.Suggestions);
        _spray = new PasswordSprayDetector(services.SecurityEvents, services.Events, _writer, services.DomainControllers, services.Time, logger);
    }

    public string Name => JobName;

    /// <summary>Bursts are evaluated every minute; the other scanners run when their interval has passed.</summary>
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);

    public IReadOnlyList<WatchdogSignal> WakeSignals { get; } = [WatchdogSignal.ConfigChanged];

    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunCoreAsync(cancellationToken).ConfigureAwait(false);
            return new JobResult(DefaultInterval);
        }
        catch (DirectoryException ex)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "scheduled scans");
            return new JobResult(DefaultInterval, Failed: true, FailureCode: "directory");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        var session = await _services.Sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var dc = await _services.DcStateReader.ReadAsync(session, cancellationToken).ConfigureAwait(false);
        var state = await _services.Store.GetDcStateAsync(dc.Facts.DomainDnsName, cancellationToken).ConfigureAwait(false);
        if (state?.BaselineCompletedAt is null)
        {
            return;
        }

        var configuration = _services.Configuration.Current.Configuration;
        var scans = configuration.Scans;
        var settings = new DetectionSettings(configuration, dc.Facts, session.Schema, _services.HostOptions);
        var now = _services.Time.GetUtcNow();
        var privileges = await _services.Store.GetPrivilegeStateAsync(cancellationToken).ConfigureAwait(false);
        var context = new ScanContext(session, settings, dc, now, privileges);
        var appended = _writer.Appended;

        await RunDueAsync(SelfCheckScan, scans.PermissionSelfCheckInterval, now, () => _services.SelfCheck.RunAsync(session, dc.Facts, cancellationToken)).ConfigureAwait(false);
        await RunDueAsync(AccountExpiryScan, scans.AccountExpiry.Interval, now, () => _accountExpiry.RunAsync(context, cancellationToken)).ConfigureAwait(false);
        await RunDueAsync(PasswordExpiryScan, scans.PasswordExpiry.Interval, now, () => _passwordExpiry.RunAsync(context, cancellationToken)).ConfigureAwait(false);
        await RunDueAsync(LockoutScan, scans.LockoutRecheckInterval, now, () => _lockouts.RunAsync(context, cancellationToken)).ConfigureAwait(false);
        await RunIsolatedAsync("burst detection", () => _bursts.RunAsync(context, cancellationToken)).ConfigureAwait(false);
        await RunIsolatedAsync("password spray detection", () => _spray.RunAsync(context, cancellationToken)).ConfigureAwait(false);

        if (_writer.Appended > appended)
        {
            _services.Signals.Raise(WatchdogSignal.EventsStored);
        }
    }

    /// <summary>Runs a scanner when it never ran or its interval has passed (the time is recorded after the run).</summary>
    private async Task RunDueAsync(string scan, TimeSpan interval, DateTimeOffset now, Func<Task> run)
    {
        if (_lastRun.TryGetValue(scan, out var last) && now - last < interval)
        {
            return;
        }

        await RunIsolatedAsync(scan, run).ConfigureAwait(false);
        _lastRun[scan] = now;
    }

    private async Task RunIsolatedAsync(string operation, Func<Task> run)
    {
        try
        {
            await run().ConfigureAwait(false);
        }
        catch (DirectoryException ex) when (!ex.IsTransient)
        {
            SafeExceptionLog.LogWarning(_logger, ex, operation);
        }
    }
}
