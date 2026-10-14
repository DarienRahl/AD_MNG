using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Event × rules → matches (section 12.1): every matching rule in configuration order, and the evaluated severity
/// <c>max(event.Severity, every matched rule's Severity override)</c>. The default overloads use
/// <see cref="IWatchdogConfigProvider.Current"/> and the last known token values; <c>ConfigChangeNotifier</c> passes the
/// previous configuration explicitly (17.2), the dispatch job re-matches held items with <see cref="Rematch"/>.
/// </summary>
public sealed class RuleEngine
{
    private readonly IWatchdogConfigProvider _configProvider;
    private readonly IConfigTokenSource _tokens;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _cacheGate = new();
    private (WatchdogConfiguration Configuration, ConfigTokens Tokens, RuleMatchContext Context)? _cached;

    public RuleEngine(IWatchdogConfigProvider configProvider, IConfigTokenSource tokens, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _configProvider = configProvider;
        _tokens = tokens;
        _timeProvider = timeProvider;
    }

    /// <summary>Evaluates <paramref name="watchEvent"/> against the current configuration.</summary>
    public RuleEvaluationResult Evaluate(WatchEvent watchEvent) => Evaluate(watchEvent, _configProvider.Current.Configuration);

    /// <summary>Evaluates <paramref name="watchEvent"/> against <paramref name="configuration"/> (e.g. the previous
    /// configuration for <c>WatchdogConfigurationChanged</c>). A stored document is brought into its effective form
    /// first (built-in rules merged, nulls repaired).</summary>
    public RuleEvaluationResult Evaluate(WatchEvent watchEvent, WatchdogConfiguration configuration) =>
        Evaluate(watchEvent, CreateContext(configuration), _timeProvider.GetUtcNow());

    /// <summary>Evaluates <paramref name="watchEvent"/> against a prepared configuration.</summary>
    /// <param name="watchEvent">The event.</param>
    /// <param name="context">The prepared configuration (<see cref="CreateContext"/>).</param>
    /// <param name="now">Current time (exclusion expiry).</param>
    /// <param name="attributionGraceExpired">See <see cref="RuleMatcher.Match"/>.</param>
    public static RuleEvaluationResult Evaluate(WatchEvent watchEvent, RuleMatchContext context, DateTimeOffset now, bool attributionGraceExpired = false)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(context);
        var matches = new List<RuleMatch>();
        var severity = watchEvent.Severity;
        foreach (var rule in context.Configuration.Rules)
        {
            if (rule is null)
            {
                continue;
            }

            var outcome = RuleMatcher.Match(rule, watchEvent, context, now, attributionGraceExpired);
            if (outcome == RuleMatchOutcome.NoMatch)
            {
                continue;
            }

            matches.Add(new RuleMatch
            {
                Rule = rule,
                NotificationSeverity = rule.Severity ?? watchEvent.Severity,
                IsProvisional = outcome == RuleMatchOutcome.Provisional,
            });
            if (rule.Severity is { } overrideSeverity && overrideSeverity > severity)
            {
                severity = overrideSeverity;
            }
        }

        return new RuleEvaluationResult { Event = watchEvent, Severity = severity, Matches = matches };
    }

    /// <summary>Re-matches one rule of the current configuration against the event's current state (severity, tags,
    /// attribution) – the dispatch job's check before releasing a held notification (13.3). Null when the rule no
    /// longer exists.</summary>
    public RuleMatch? Rematch(WatchEvent watchEvent, string ruleId, bool attributionGraceExpired)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        var context = CreateContext(_configProvider.Current.Configuration);
        var rule = context.Configuration.Rules.FirstOrDefault(r => r is not null && string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            return null;
        }

        var outcome = RuleMatcher.Match(rule, watchEvent, context, _timeProvider.GetUtcNow(), attributionGraceExpired);
        return outcome == RuleMatchOutcome.NoMatch
            ? null
            : new RuleMatch { Rule = rule, NotificationSeverity = rule.Severity ?? watchEvent.Severity, IsProvisional = outcome == RuleMatchOutcome.Provisional };
    }

    /// <summary>The prepared form of <paramref name="configuration"/> with the current token values (the last one is
    /// cached; the provider's current configuration is used as is, any other document in its effective form).</summary>
    public RuleMatchContext CreateContext(WatchdogConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var tokens = _tokens.Current;
        lock (_cacheGate)
        {
            if (_cached is { } cached && ReferenceEquals(cached.Configuration, configuration) && cached.Tokens == tokens)
            {
                return cached.Context;
            }
        }

        var effective = ReferenceEquals(configuration, _configProvider.Current.Configuration)
            ? configuration
            : WatchdogConfigProvider.Effective(configuration);
        var context = RuleMatchContext.Create(effective, tokens);
        lock (_cacheGate)
        {
            _cached = (configuration, tokens, context);
        }

        return context;
    }
}
