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

    /// <summary>Every rule of <paramref name="configuration"/> that matches the event as it is now, in configuration order –
    /// the dispatch job re-plans a released target from all of them (13.3), so a target whose rule no longer matches passes
    /// to the next rule that still does. Default: <see cref="Rematch"/> for each rule.</summary>
    IReadOnlyList<PlannedMatch> MatchAll(WatchEvent watchEvent, WatchdogConfiguration configuration, DateTimeOffset now, bool attributionGraceExpired)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(configuration);
        var matches = new List<PlannedMatch>();
        foreach (var rule in configuration.Rules ?? [])
        {
            if (rule is not null && Rematch(watchEvent, rule, configuration, now, attributionGraceExpired) is { } match)
            {
                matches.Add(match);
            }
        }

        return matches;
    }
}
