using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// The default <see cref="IHeldNotificationMatcher"/>: the rule matched when the event was evaluated, so only the
/// conditions that late attribution can change are checked again (12.1): enabled, <c>MinSeverity</c> against the
/// re-resolved severity, required/any tags (selfChange, passwordReset, syncAccount are added by attribution),
/// <c>ExcludeSelfChanges</c>, <c>ExcludeActors</c> and actor exclusions (SIDs only). While the actor is still pending
/// and the grace period runs, a rule with actor conditions matches provisionally; after it an unresolved actor
/// excludes nothing.
/// </summary>
public sealed class AttributionDependentRecheck : IHeldNotificationMatcher
{
    /// <inheritdoc/>
    public PlannedMatch? Rematch(WatchEvent watchEvent, RuleSettings rule, WatchdogConfiguration configuration, DateTimeOffset now, bool attributionGraceExpired)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(rule);
        if (!rule.Enabled || (rule.MinSeverity is { } min && watchEvent.Severity < min))
        {
            return null;
        }

        var tags = new HashSet<string>(watchEvent.Tags, StringComparer.OrdinalIgnoreCase);
        if ((rule.RequiredTags ?? []).Any(t => !tags.Contains(t)) || (rule.AnyTags is { Count: > 0 } any && !any.Any(tags.Contains)))
        {
            return null;
        }

        var actors = rule.Actors ?? new ActorFilter();
        if (actors.ExcludeSelfChanges && tags.Contains(EventTags.SelfChange))
        {
            return null;
        }

        var attribution = watchEvent.Attribution;
        var actorSid = attribution.Status == AttributionStatus.Resolved ? attribution.Actor?.Sid : null;
        if (actorSid is not null
            && ((actors.ExcludeActors ?? []).Any(s => SameSid(s, actorSid)) || (rule.Exclusions ?? []).Any(x => ActorExclusionApplies(x, watchEvent, actorSid, now))))
        {
            return null;
        }

        var hasActorConditions = actors.ExcludeSelfChanges || (actors.ExcludeActors?.Count ?? 0) > 0
            || (rule.Exclusions ?? []).Any(x => !string.IsNullOrWhiteSpace(x.Actor));
        var provisional = attribution.Status == AttributionStatus.Pending && hasActorConditions && !attributionGraceExpired;
        return new PlannedMatch(rule, rule.Severity ?? watchEvent.Severity, provisional);
    }

    private static bool ActorExclusionApplies(RuleExclusion exclusion, WatchEvent e, string actorSid, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(exclusion.Actor) || !SameSid(exclusion.Actor, actorSid) || exclusion.ExpiresAt is { } expires && expires < now)
        {
            return false;
        }

        if (exclusion.ObjectGuid is { } guid && e.Object.ObjectGuid != guid && e.Member?.ObjectGuid != guid)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(exclusion.SubtreeDn)
            && !(DistinguishedName.TryParse(exclusion.SubtreeDn, out var subtree) && DistinguishedName.TryParse(e.Object.Dn, out var dn)
                && (dn.Equals(subtree) || dn.IsDescendantOf(subtree))))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(exclusion.Attribute)
            || e.Changes.All(c => string.Equals(c.Attribute, exclusion.Attribute, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameSid(string configured, string actual) =>
        !configured.Contains('{', StringComparison.Ordinal) && string.Equals(configured.Trim(), actual, StringComparison.OrdinalIgnoreCase);
}
