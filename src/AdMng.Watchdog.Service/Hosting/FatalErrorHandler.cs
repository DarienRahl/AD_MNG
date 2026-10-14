namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// The SCM recovery path (spec 19.1): when the detection job keeps failing with unexpected exceptions for more than
/// <see cref="Threshold"/> – a state a restart may cure (leaked connections, a wedged LDAP pool) – the process exits with
/// code 1 so the service recovery actions restart it. Any successful iteration resets the clock.
/// </summary>
public sealed partial class FatalErrorHandler
{
    /// <summary>The job whose failures can end the process.</summary>
    public const string WatchedJob = "detection";

    /// <summary>How long the watched job may fail continuously.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromHours(1);

    private readonly IProcessTerminator _terminator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FatalErrorHandler> _logger;
    private readonly Lock _gate = new();
    private DateTimeOffset? _failingSince;
    private bool _exiting;

    public FatalErrorHandler(IProcessTerminator terminator, TimeProvider timeProvider, ILogger<FatalErrorHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(terminator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _terminator = terminator;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>A job iteration completed (also with a handled <c>Failed</c> result).</summary>
    public void ReportSuccess(string jobName)
    {
        if (string.Equals(jobName, WatchedJob, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _failingSince = null;
            }
        }
    }

    /// <summary>A job iteration threw an unexpected exception.</summary>
    public void ReportFailure(string jobName)
    {
        if (!string.Equals(jobName, WatchedJob, StringComparison.Ordinal))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            _failingSince ??= now;
            if (_exiting || now - _failingSince.Value <= Threshold)
            {
                return;
            }

            _exiting = true;
        }

        Log.Exiting(_logger, jobName, (int)Threshold.TotalMinutes);
        _terminator.Exit(StartupExitCodes.Fatal);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = EventLogIds.FatalStartupCheck, Level = LogLevel.Critical,
            Message = "The {Job} job has failed for more than {Minutes} minutes; exiting with code 1 so the service recovery restarts it.")]
        public static partial void Exiting(ILogger logger, string job, int minutes);
    }
}
