using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting.Systemd;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>The job loops whose progress the systemd watchdog follows (<see cref="JobHostService"/>).</summary>
public interface IJobProgressSource
{
    /// <summary>The job name and progress of every hosted job loop.</summary>
    IReadOnlyList<(string Job, JobProgress Progress)> SnapshotProgress();
}

/// <summary>The watchdog settings systemd passes to the unit (<c>WatchdogSec=</c>).</summary>
/// <param name="PingInterval">How often <c>WATCHDOG=1</c> is sent; null = the unit has no watchdog.</param>
public sealed record SystemdWatchdogSettings(TimeSpan? PingInterval)
{
    /// <summary>The longest ping interval (design 12.1: every 30 s with <c>WatchdogSec=120s</c>).</summary>
    public static readonly TimeSpan MaxPingInterval = TimeSpan.FromSeconds(30);

    /// <summary>The shortest ping interval.</summary>
    public static readonly TimeSpan MinPingInterval = TimeSpan.FromSeconds(1);

    /// <summary>From the process environment (<c>WATCHDOG_USEC</c>, <c>WATCHDOG_PID</c>).</summary>
    public static SystemdWatchdogSettings FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariable, Environment.ProcessId);

    /// <summary>
    /// Like <c>sd_watchdog_enabled</c>: <c>WATCHDOG_USEC</c> holds the watchdog timeout; when <c>WATCHDOG_PID</c> is set it
    /// must name this process. The pings go out at a quarter of the timeout, at most every <see cref="MaxPingInterval"/>.
    /// </summary>
    public static SystemdWatchdogSettings FromEnvironment(Func<string, string?> environment, int processId)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!long.TryParse(environment("WATCHDOG_USEC"), NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds) || microseconds <= 0)
        {
            return new SystemdWatchdogSettings((TimeSpan?)null);
        }

        if (environment("WATCHDOG_PID") is { Length: > 0 } pid
            && (!int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var watchdogPid) || watchdogPid != processId))
        {
            return new SystemdWatchdogSettings((TimeSpan?)null);
        }

        var quarter = TimeSpan.FromTicks(microseconds * (TimeSpan.TicksPerMillisecond / 1000) / 4);
        return new SystemdWatchdogSettings(quarter > MaxPingInterval ? MaxPingInterval : quarter < MinPingInterval ? MinPingInterval : quarter);
    }
}

/// <summary>
/// The systemd watchdog of <c>admng-watchdog.service</c> (stage B design 12.1, <c>WatchdogSec=120s</c>): after the host has
/// started (<c>READY=1</c>, sent by <see cref="SystemdLifetime"/>), sends <c>WATCHDOG=1</c> every
/// <see cref="SystemdWatchdogSettings.PingInterval"/> – but only while every job loop progresses within its expected
/// interval (<see cref="JobProgress.IsStalled"/>). A hung detection loop therefore stops the pings, and systemd kills
/// and restarts the service (<c>Restart=on-failure</c>). A stalled job is logged once (event 1010, Critical) so the
/// journal says why the watchdog fired; a dead process or a starved thread pool stops the pings by itself.
/// </summary>
public sealed partial class SystemdWatchdogNotifier : BackgroundService
{
    /// <summary>The <c>sd_notify</c> state of a ping.</summary>
    public static readonly ServiceState Ping = new("WATCHDOG=1");

    private readonly ISystemdNotifier _notifier;
    private readonly IJobProgressSource _jobs;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly SystemdWatchdogSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SystemdWatchdogNotifier> _logger;
    private readonly HashSet<string> _reportedStalls = new(StringComparer.Ordinal);
    private bool _sendFailureReported;

    public SystemdWatchdogNotifier(
        ISystemdNotifier notifier,
        IJobProgressSource jobs,
        IHostApplicationLifetime lifetime,
        SystemdWatchdogSettings settings,
        TimeProvider timeProvider,
        ILogger<SystemdWatchdogNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _notifier = notifier;
        _jobs = jobs;
        _lifetime = lifetime;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Pings sent so far (tests, diagnostics).</summary>
    public int PingsSent { get; private set; }

    /// <summary>One check: pings when no job is stalled; returns whether it pinged.</summary>
    public bool Tick()
    {
        var now = _timeProvider.GetUtcNow();
        var stalled = new List<(string Job, JobProgress Progress)>();
        foreach (var (job, progress) in _jobs.SnapshotProgress())
        {
            if (progress.IsStalled(now))
            {
                stalled.Add((job, progress));
            }
        }

        if (stalled.Count > 0)
        {
            foreach (var (job, progress) in stalled)
            {
                if (_reportedStalls.Add(job))
                {
                    Log.JobStalled(_logger, job, progress.Phase.ToString(), (int)(now - progress.Since).TotalMinutes);
                }
            }

            return false;
        }

        _reportedStalls.Clear();
        try
        {
            _notifier.Notify(Ping);
            PingsSent++;
            _sendFailureReported = false;
            return true;
        }
        catch (SocketException ex)
        {
            if (!_sendFailureReported)
            {
                _sendFailureReported = true;
                Log.PingFailed(_logger, ex.SocketErrorCode.ToString());
            }

            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_notifier.IsEnabled || _settings.PingInterval is not { } interval)
        {
            return;
        }

        if (!await WaitForStartedAsync(stoppingToken).ConfigureAwait(false))
        {
            return;
        }

        using var timer = new PeriodicTimer(interval, _timeProvider);
        try
        {
            do
            {
                Tick();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>systemd starts the watchdog with <c>READY=1</c>; a ping before it would start it during the migrations.</summary>
    private async Task<bool> WaitForStartedAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var onStarted = _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var onStopping = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        try
        {
            await started.Task.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = EventLogIds.FatalStartupCheck, Level = LogLevel.Critical,
            Message = "The {Job} job has made no progress for {Minutes} minute(s) (phase {Phase}); the systemd watchdog pings stop and systemd restarts the service.")]
        public static partial void JobStalled(ILogger logger, string job, string phase, int minutes);

        [LoggerMessage(EventId = 2302, Level = LogLevel.Warning, Message = "The systemd watchdog notification could not be sent ({Error}).")]
        public static partial void PingFailed(ILogger logger, string error);
    }
}
