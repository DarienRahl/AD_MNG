using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// The <see cref="IHeldNotificationMatcher"/> of the service: the full rule match (<see cref="RuleMatcher"/>, section
/// 12.1 – kinds, scope, attributes, tags, groups, <c>MinSeverity</c>, actor conditions with expanded SID tokens,
/// exclusions) of the held item's rule against the event's final own severity (<see cref="EventSeverities.Own"/>), tags and
/// actor (13.3), with the tokens and
/// privileged roots of the prepared configuration (<see cref="RuleEngine.CreateContext"/>). The dispatch job passes the
/// rule of the current configuration, so a rule that was removed or disabled since the item was queued no longer
/// matches.
/// </summary>
public sealed class RuleEngineHeldMatcher : IHeldNotificationMatcher
{
    private readonly RuleEngine _engine;

    public RuleEngineHeldMatcher(RuleEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <inheritdoc/>
    public PlannedMatch? Rematch(WatchEvent watchEvent, RuleSettings rule, WatchdogConfiguration configuration, DateTimeOffset now, bool attributionGraceExpired)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(configuration);
        var outcome = RuleMatcher.Match(rule, watchEvent, _engine.CreateContext(configuration), now, attributionGraceExpired);
        return outcome == RuleMatchOutcome.NoMatch
            ? null
            : new PlannedMatch(rule, rule.Severity ?? EventSeverities.Own(watchEvent), outcome == RuleMatchOutcome.Provisional);
    }
}
