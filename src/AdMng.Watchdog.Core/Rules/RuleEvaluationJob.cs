using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Model;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// <see cref="IWatchdogJob"/> "rules" (section 13.1): reads up to <see cref="BatchSize"/> unevaluated events, evaluates
/// them against the rules (<see cref="RuleEngine"/>), lets the planner (<see cref="IRuleMatchPlanner"/>) create the
/// pending notifications and commits evaluations and notifications in one store call; raises
/// <see cref="WatchdogSignal.NotificationsQueued"/> when something was queued. Wakes on EventsStored,
/// AttributionUpdated and ConfigChanged; runs again at once while a full batch was read.
/// </summary>
public sealed partial class RuleEvaluationJob : IWatchdogJob
{
    /// <summary>Events per run (13.1).</summary>
    public const int BatchSize = 500;

    private static readonly WatchdogSignal[] Signals =
        [WatchdogSignal.EventsStored, WatchdogSignal.AttributionUpdated, WatchdogSignal.ConfigChanged];

    private readonly IEventStore _events;
    private readonly RuleEngine _engine;
    private readonly IRuleMatchPlanner _planner;
    private readonly IWatchdogConfigProvider _configProvider;
    private readonly IConfigTokenSource _tokens;
    private readonly IWatchdogSignals _signals;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RuleEvaluationJob> _logger;

    public RuleEvaluationJob(IEventStore events, RuleEngine engine, IRuleMatchPlanner planner, IWatchdogConfigProvider configProvider,
        IConfigTokenSource tokens, IWatchdogSignals signals, TimeProvider timeProvider, ILogger<RuleEvaluationJob> logger)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _events = events;
        _engine = engine;
        _planner = planner;
        _configProvider = configProvider;
        _tokens = tokens;
        _signals = signals;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "rules";

    /// <inheritdoc/>
    public TimeSpan DefaultInterval { get; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public IReadOnlyList<WatchdogSignal> WakeSignals => Signals;

    /// <inheritdoc/>
    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _tokens.GetAsync(cancellationToken).ConfigureAwait(false);
            var configuration = _configProvider.Current.Configuration;
            var context = _engine.CreateContext(configuration);
            var events = await _events.GetUnevaluatedAsync(BatchSize, cancellationToken).ConfigureAwait(false);
            if (events.Count == 0)
            {
                return new JobResult(DefaultInterval);
            }

            var now = _timeProvider.GetUtcNow();
            var evaluations = new List<EventEvaluation>(events.Count);
            var notifications = new List<PendingNotification>();
            foreach (var watchEvent in events)
            {
                var result = RuleEngine.Evaluate(watchEvent, context, now);
                evaluations.Add(result.ToEvaluation());
                notifications.AddRange(await _planner.PlanAsync(result, context.Configuration, now, cancellationToken).ConfigureAwait(false));
            }

            await _events.CommitEvaluationAsync(evaluations, notifications, cancellationToken).ConfigureAwait(false);
            LogEvaluated(events.Count, notifications.Count);
            if (notifications.Count > 0)
            {
                _signals.Raise(WatchdogSignal.NotificationsQueued);
            }

            return new JobResult(events.Count >= BatchSize ? TimeSpan.Zero : DefaultInterval);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeExceptionLog.LogError(_logger, ex, "rule evaluation");
            return new JobResult(DefaultInterval, Failed: true, FailureCode: SafeExceptionLog.ErrorCode(ex));
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Evaluated {Events} events, queued {Notifications} notifications.")]
    private partial void LogEvaluated(int events, int notifications);
}
