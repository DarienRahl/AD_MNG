using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Rule exclusions (12.1 #9). An exclusion applies when every field it sets matches: <c>ObjectGuid</c> = object or
/// member GUID; <c>SubtreeDn</c> contains the object (DN or previous DN); <c>Actor</c> (SID) = SID of the resolved actor;
/// <c>Attribute</c>: the event's relevant changes without this attribute are empty – the exclusion removes only that
/// attribute from consideration (relevant = the changes the rule's <c>Attributes</c> select, every change when the rule
/// has none). Expired exclusions (<c>ExpiresAt</c> &lt; now) and exclusions that set no condition never apply. An
/// unresolved actor (Unknown, Ambiguous, NotApplicable or the grace period over) excludes nothing; workstation names
/// and IP addresses are never matching inputs.
/// </summary>
public static class ExclusionMatcher
{
    /// <summary>Whether <paramref name="exclusion"/> excludes <paramref name="watchEvent"/> from <paramref name="rule"/>.</summary>
    /// <param name="exclusion">The exclusion.</param>
    /// <param name="rule">The rule it belongs to (its attribute filter defines the relevant changes).</param>
    /// <param name="watchEvent">The event.</param>
    /// <param name="context">The prepared configuration.</param>
    /// <param name="now">Current time (expiry).</param>
    /// <param name="attributionGraceExpired">True once the attribution grace period of the event is over: a still
    /// pending actor then counts as unresolved.</param>
    public static ExclusionOutcome Evaluate(RuleExclusion exclusion, RuleSettings rule, WatchEvent watchEvent, RuleMatchContext context,
        DateTimeOffset now, bool attributionGraceExpired = false)
    {
        ArgumentNullException.ThrowIfNull(exclusion);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(context);

        var hasCondition = exclusion.ObjectGuid is not null || !string.IsNullOrWhiteSpace(exclusion.SubtreeDn)
            || !string.IsNullOrWhiteSpace(exclusion.Actor) || !string.IsNullOrWhiteSpace(exclusion.Attribute);
        if (!hasCondition || exclusion.ExpiresAt < now)
        {
            return ExclusionOutcome.DoesNotApply;
        }

        if (exclusion.ObjectGuid is { } guid && watchEvent.Object?.ObjectGuid != guid && watchEvent.Member?.ObjectGuid != guid)
        {
            return ExclusionOutcome.DoesNotApply;
        }

        if (!string.IsNullOrWhiteSpace(exclusion.SubtreeDn)
            && (context.ResolveDn(exclusion.SubtreeDn, watchEvent) is not { } subtree
                || !ScopeMatcher.EventDns(watchEvent).Any(dn => ScopeMatcher.IsAtOrBelow(dn, subtree))))
        {
            return ExclusionOutcome.DoesNotApply;
        }

        if (!string.IsNullOrWhiteSpace(exclusion.Attribute) && !OnlyAttribute(exclusion.Attribute.Trim(), rule, watchEvent))
        {
            return ExclusionOutcome.DoesNotApply;
        }

        if (string.IsNullOrWhiteSpace(exclusion.Actor))
        {
            return ExclusionOutcome.Applies;
        }

        return ActorCondition.Evaluate(watchEvent.Attribution, context.ResolveSid(exclusion.Actor), attributionGraceExpired) switch
        {
            ActorConditionState.Matches => ExclusionOutcome.Applies,
            ActorConditionState.Pending => ExclusionOutcome.AwaitingActor,
            _ => ExclusionOutcome.DoesNotApply,
        };
    }

    private static bool OnlyAttribute(string attribute, RuleSettings rule, WatchEvent watchEvent)
    {
        var filter = rule.Attributes ?? [];
        var relevant = (watchEvent.Changes ?? [])
            .Select(c => c.Attribute)
            .Where(a => filter.Count == 0 || filter.Contains(a, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return relevant.Contains(attribute, StringComparer.OrdinalIgnoreCase)
            && relevant.All(a => string.Equals(a, attribute, StringComparison.OrdinalIgnoreCase));
    }
}
