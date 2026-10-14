using AdMng.Watchdog.Contracts.Events;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>Actor conditions (12.1 #8): compared by SID only, and only with a <c>Resolved</c> attribution; while
/// attribution is pending the answer is <see cref="ActorConditionState.Pending"/> until the grace period ends, after
/// which an unresolved actor excludes nothing.</summary>
internal static class ActorCondition
{
    public static ActorConditionState Evaluate(EventAttribution? attribution, SidPattern? actorSid, bool attributionGraceExpired)
    {
        if (actorSid is not { } pattern || attribution is null)
        {
            return ActorConditionState.NoMatch;
        }

        return attribution.Status switch
        {
            AttributionStatus.Pending => attributionGraceExpired ? ActorConditionState.NoMatch : ActorConditionState.Pending,
            AttributionStatus.Resolved when pattern.Matches(attribution.Actor?.Sid) => ActorConditionState.Matches,
            _ => ActorConditionState.NoMatch,
        };
    }
}
