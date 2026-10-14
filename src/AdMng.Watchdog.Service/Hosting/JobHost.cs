using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Runs one <see cref="IWatchdogJob"/> for the lifetime of the service (spec 19.1): subscribes once to the job's
/// <see cref="IWatchdogJob.WakeSignals"/>, then loops <c>RunOnceAsync</c> → wait <c>NextRunIn ?? DefaultInterval</c>
/// (ended early by a signal). Unexpected exceptions are logged through <see cref="SafeExceptionLog"/> and retried with
/// exponential backoff (<see cref="InitialBackoff"/> … <see cref="MaxBackoff"/>, signals do not shorten it); the
/// <see cref="FatalErrorHandler"/> decides when repeated failures end the process.
/// </summary>
public sealed partial class JobHost : BackgroundService
{
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly IWatchdogJob _job;
    private readonly IWatchdogSignals _signals;
    private readonly FatalErrorHandler _fatal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JobHost> _logger;
    private int _iterations;

    public JobHost(IWatchdogJob job, IWatchdogSignals signals, FatalErrorHandler fatal, TimeProvider timeProvider, ILogger<JobHost> logger)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(fatal);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _job = job;
        _signals = signals;
        _fatal = fatal;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string JobName => _job.Name;

    /// <summary>Completed iterations (successful or <c>Failed</c>), for tests and diagnostics.</summary>
    public int Iterations => Volatile.Read(ref _iterations);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _signals.Subscribe(_job.WakeSignals);
        var backoff = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _job.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                Interlocked.Increment(ref _iterations);
                backoff = TimeSpan.Zero;
                _fatal.ReportSuccess(_job.Name);
                if (result.Failed)
                {
                    Log.IterationFailed(_logger, _job.Name, result.FailureCode ?? "unknown");
                }

                var wait = result.NextRunIn is { } next && next >= TimeSpan.Zero ? next : _job.DefaultInterval;
                await subscription.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SafeExceptionLog.LogError(_logger, ex, "job " + _job.Name);
                backoff = backoff == TimeSpan.Zero ? InitialBackoff : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                _fatal.ReportFailure(_job.Name);
                try
                {
                    await Task.Delay(backoff, _timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 2300, Level = LogLevel.Warning, Message = "Job {Job} iteration failed: {FailureCode}.")]
        public static partial void IterationFailed(ILogger logger, string job, string failureCode);
    }
}
