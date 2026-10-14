using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// Re-matches a held notification's rule against the event's final state before it is released (section 13.3: final
/// severity, tags and actor). Implemented by <see cref="RuleEngineHeldMatcher"/> (the rules area's <c>RuleMatcher</c>).
/// </summary>
public interface IHeldNotificationMatcher
{
    /// <summary>The match of <paramref name="rule"/> for the event as it is now, or null when it no longer matches.</summary>
    /// <param name="watchEvent">The event with its final (or still pending) attribution, tags and severity.</param>
    /// <param name="rule">The rule of the held notification (current configuration).</param>
    /// <param name="configuration">The current configuration.</param>
    /// <param name="now">Current time (exclusion expiry).</param>
    /// <param name="attributionGraceExpired">The attribution grace period is over (an unresolved actor excludes nothing).</param>
    PlannedMatch? Rematch(WatchEvent watchEvent, RuleSettings rule, WatchdogConfiguration configuration, DateTimeOffset now, bool attributionGraceExpired);
}
