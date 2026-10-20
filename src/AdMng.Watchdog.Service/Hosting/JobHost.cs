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

    /// <summary>While a job keeps failing with the same code, warning 2300 is repeated at most this often.</summary>
    public static readonly TimeSpan FailureLogRepeat = TimeSpan.FromHours(1);

    private readonly IWatchdogJob _job;
    private readonly IWatchdogSignals _signals;
    private readonly FatalErrorHandler _fatal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JobHost> _logger;
    private int _iterations;
    private string? _failureCode;
    private int _failedIterations;
    private DateTimeOffset _failureLoggedAt;
    private volatile JobProgressBox _progressBox = new(default);

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

    /// <summary>The current phase of the loop (read by <see cref="SystemdWatchdogNotifier"/>).</summary>
    public JobProgress Progress => _progressBox.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _signals.Subscribe(_job.WakeSignals);
        var backoff = TimeSpan.Zero;
        var runBudget = JobProgress.RunBudget(_job.Name);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    SetProgress(JobPhase.Running, runBudget);
                    var result = await _job.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _iterations);
                    backoff = TimeSpan.Zero;
                    _fatal.ReportSuccess(_job.Name);
                    ReportResult(result);

                    var wait = result.NextRunIn is { } next && next >= TimeSpan.Zero ? next : _job.DefaultInterval;
                    SetProgress(JobPhase.Waiting, wait);
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
                    SetProgress(JobPhase.BackingOff, backoff);
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
        finally
        {
            SetProgress(JobPhase.Stopped, TimeSpan.Zero);
        }
    }

    // Published as one reference: the notifier reads it from another thread and must never see a torn struct.
    private void SetProgress(JobPhase phase, TimeSpan expected) =>
        _progressBox = new JobProgressBox(new JobProgress(phase, _timeProvider.GetUtcNow(), expected));

    private sealed record JobProgressBox(JobProgress Value);

    /// <summary>A failed iteration is logged when the failure starts or its code changes, then at most every
    /// <see cref="FailureLogRepeat"/> while it lasts (the condition itself is in the health registry); the recovery once.</summary>
    private void ReportResult(JobResult result)
    {
        if (!result.Failed)
        {
            if (_failureCode is not null)
            {
                Log.Recovered(_logger, _job.Name, _failedIterations);
                _failureCode = null;
                _failedIterations = 0;
            }

            return;
        }

        var code = result.FailureCode ?? "unknown";
        var now = _timeProvider.GetUtcNow();
        _failedIterations++;
        if (!string.Equals(code, _failureCode, StringComparison.Ordinal) || now - _failureLoggedAt >= FailureLogRepeat)
        {
            Log.IterationFailed(_logger, _job.Name, code, _failedIterations);
            _failureCode = code;
            _failureLoggedAt = now;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 2300, Level = LogLevel.Warning, Message = "Job {Job} iteration failed: {FailureCode} ({Count} failed iteration(s) in a row).")]
        public static partial void IterationFailed(ILogger logger, string job, string failureCode, int count);

        [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "Job {Job} recovered after {Count} failed iteration(s).")]
        public static partial void Recovered(ILogger logger, string job, int count);
    }
}
