using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
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
/// <remarks>Without a registered planner the events are still evaluated (matched rules, digest) but nothing is queued
/// for immediate delivery – health check <see cref="PlannerCheckCode"/> is Error and a critical log entry is written,
/// so a wiring mistake is visible instead of silently dropping alerts.</remarks>
public sealed partial class RuleEvaluationJob : IWatchdogJob
{
    /// <summary>Events per run (13.1).</summary>
    public const int BatchSize = 500;

    /// <summary>Health check reporting whether a notification planner is registered.</summary>
    public const string PlannerCheckCode = "rules.planner";

    private static readonly WatchdogSignal[] Signals =
        [WatchdogSignal.EventsStored, WatchdogSignal.AttributionUpdated, WatchdogSignal.ConfigChanged];

    private readonly IEventStore _events;
    private readonly RuleEngine _engine;
    private readonly IReadOnlyList<IRuleMatchPlanner> _planners;
    private readonly IHealthRegistry _health;
    private readonly IWatchdogConfigProvider _configProvider;
    private readonly IConfigTokenSource _tokens;
    private readonly IWatchdogSignals _signals;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RuleEvaluationJob> _logger;
    private readonly ConfiguredDnMonitor? _configuredDns;

    /// <summary>Creates the job; with a snapshot (<see cref="IDetectionStore"/>) every run also reports configured DNs that
    /// no longer exist (<see cref="ConfiguredDnMonitor"/>, at most every <see cref="ConfiguredDnMonitor.Interval"/>).</summary>
    public RuleEvaluationJob(IEventStore events, RuleEngine engine, IEnumerable<IRuleMatchPlanner> planners, IWatchdogConfigProvider configProvider,
        IConfigTokenSource tokens, IWatchdogSignals signals, IHealthRegistry health, TimeProvider timeProvider, ILogger<RuleEvaluationJob> logger,
        IDetectionStore? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(planners);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _events = events;
        _engine = engine;
        _planners = [.. planners];
        _health = health;
        _configProvider = configProvider;
        _tokens = tokens;
        _signals = signals;
        _timeProvider = timeProvider;
        _logger = logger;
        _configuredDns = snapshot is null ? null : new ConfiguredDnMonitor(snapshot, health, timeProvider);
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
            var tokens = await _tokens.GetAsync(cancellationToken).ConfigureAwait(false);
            var configuration = _configProvider.Current.Configuration;
            var context = _engine.CreateContext(configuration);
            await CheckConfiguredDnsAsync(configuration, tokens, cancellationToken).ConfigureAwait(false);

            // Read → match → plan → commit in one section with the attribution engine's re-evaluation (RuleEngine).
            using var section = await _engine.EnterEvaluationAsync(cancellationToken).ConfigureAwait(false);
            var events = await _events.GetUnevaluatedAsync(BatchSize, cancellationToken).ConfigureAwait(false);
            if (events.Count == 0)
            {
                return new JobResult(DefaultInterval);
            }

            var now = _timeProvider.GetUtcNow();
            ReportPlanner(now);
            var evaluations = new List<EventEvaluation>(events.Count);
            var notifications = new List<PendingNotification>();
            foreach (var watchEvent in events)
            {
                var result = RuleEngine.Evaluate(watchEvent, context, now);
                evaluations.Add(result.ToEvaluation());
                foreach (var planner in _planners)
                {
                    notifications.AddRange(await planner.PlanAsync(result, context.Configuration, now, cancellationToken).ConfigureAwait(false));
                }
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

    private async Task CheckConfiguredDnsAsync(WatchdogConfiguration configuration, ConfigTokens tokens, CancellationToken cancellationToken)
    {
        if (_configuredDns is null)
        {
            return;
        }

        try
        {
            await _configuredDns.CheckAsync(configuration, tokens, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "configured DN check");   // never blocks rule evaluation
        }
    }

    private void ReportPlanner(DateTimeOffset now)
    {
        if (_planners.Count == 0)
        {
            LogNoPlanner();
        }

        _health.Report(new HealthCheckDto
        {
            Code = PlannerCheckCode,
            State = _planners.Count == 0 ? HealthState.Error : HealthState.Ok,
            Detail = _planners.Count == 0 ? "No notification planner is registered – events are evaluated but nothing is sent immediately." : null,
            CheckedAt = now,
        });
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "No notification planner is registered: rule matches are recorded but no immediate notification is queued.")]
    private partial void LogNoPlanner();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Evaluated {Events} events, queued {Notifications} notifications.")]
    private partial void LogEvaluated(int events, int notifications);
}
