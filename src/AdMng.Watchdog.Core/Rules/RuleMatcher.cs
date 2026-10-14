using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// One rule against one event (section 12.1). A rule matches when all hold: enabled; kinds and object kinds; scope;
/// attributes; required/any tags; the group filter (membership and privilege kinds only); <c>MinSeverity</c> ≤ event
/// severity; no actor exclusion (SIDs of a resolved actor only, <c>ExcludeSelfChanges</c> with tag <c>selfChange</c>);
/// no applying exclusion. While attribution is pending a rule with actor conditions matches
/// <see cref="RuleMatchOutcome.Provisional"/>ly; after the grace period an unresolved actor excludes nothing. The
/// locked <c>health</c> rule always matches health events; <see cref="WatchEventKind.Test"/> never matches.
/// </summary>
public static class RuleMatcher
{
    /// <summary>Matches <paramref name="rule"/> against <paramref name="watchEvent"/>.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="watchEvent">The event with its current severity, tags and attribution.</param>
    /// <param name="context">The prepared configuration the rule belongs to.</param>
    /// <param name="now">Current time (exclusion expiry).</param>
    /// <param name="attributionGraceExpired">True when re-matching after <c>Notifications.AttributionGrace</c>: a still
    /// pending actor then counts as unresolved (no provisional result).</param>
    public static RuleMatchOutcome Match(RuleSettings rule, WatchEvent watchEvent, RuleMatchContext context, DateTimeOffset now,
        bool attributionGraceExpired = false)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(context);

        if (watchEvent.Kind == WatchEventKind.Test)
        {
            return RuleMatchOutcome.NoMatch;
        }

        if (IsLockedHealthRule(rule) && IsHealth(watchEvent.Kind))
        {
            return RuleMatchOutcome.Match;
        }

        if (!rule.Enabled
            || !Contains(rule.Kinds, watchEvent.Kind)
            || !Contains(rule.ObjectKinds, watchEvent.Object?.Kind ?? WatchObjectKind.Unknown)
            || !ScopeMatcher.Matches(rule.Scope, watchEvent, context)
            || !AttributesMatch(rule, watchEvent)
            || !TagsMatch(rule, watchEvent)
            || !GroupsMatch(rule, watchEvent, context)
            || (rule.MinSeverity is { } minimum && watchEvent.Severity < minimum))
        {
            return RuleMatchOutcome.NoMatch;
        }

        var provisional = false;
        var actors = rule.Actors ?? new ActorFilter();
        foreach (var excluded in actors.ExcludeActors ?? [])
        {
            switch (ActorCondition.Evaluate(watchEvent.Attribution, context.ResolveSid(excluded), attributionGraceExpired))
            {
                case ActorConditionState.Matches:
                    return RuleMatchOutcome.NoMatch;
                case ActorConditionState.Pending:
                    provisional = true;
                    break;
            }
        }

        if (actors.ExcludeSelfChanges)
        {
            if (HasTag(watchEvent, EventTags.SelfChange))
            {
                return RuleMatchOutcome.NoMatch;
            }

            provisional |= watchEvent.Attribution?.Status == AttributionStatus.Pending && !attributionGraceExpired;
        }

        foreach (var exclusion in rule.Exclusions ?? [])
        {
            if (exclusion is null)
            {
                continue;
            }

            switch (ExclusionMatcher.Evaluate(exclusion, rule, watchEvent, context, now, attributionGraceExpired))
            {
                case ExclusionOutcome.Applies:
                    return RuleMatchOutcome.NoMatch;
                case ExclusionOutcome.AwaitingActor:
                    provisional = true;
                    break;
            }
        }

        return provisional ? RuleMatchOutcome.Provisional : RuleMatchOutcome.Match;
    }

    /// <summary>True for the shipped locked health rule (id <c>health</c>, Locked).</summary>
    public static bool IsLockedHealthRule(RuleSettings rule) =>
        rule.Locked && string.Equals(rule.Id, DefaultRules.Health, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the rule has conditions on the actor (12.1 #8, 11.7 <c>actorConditionsUnreliable</c>).</summary>
    public static bool HasActorConditions(RuleSettings rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Actors is { } actors && ((actors.ExcludeActors?.Count ?? 0) > 0 || actors.ExcludeSelfChanges)
            || (rule.Exclusions ?? []).Any(e => e is not null && !string.IsNullOrWhiteSpace(e.Actor));
    }

    private static bool IsHealth(WatchEventKind kind) =>
        Enum.IsDefined(kind) && EventKindCatalog.Get(kind).IsHealth;

    private static bool Contains<T>(IReadOnlyList<T>? values, T value) =>
        values is null || values.Count == 0 || values.Contains(value);

    private static bool AttributesMatch(RuleSettings rule, WatchEvent watchEvent)
    {
        var attributes = rule.Attributes ?? [];
        return attributes.Count == 0
            || (watchEvent.Changes ?? []).Any(c => attributes.Contains(c.Attribute, StringComparer.OrdinalIgnoreCase));
    }

    private static bool TagsMatch(RuleSettings rule, WatchEvent watchEvent)
    {
        var required = rule.RequiredTags ?? [];
        var any = rule.AnyTags ?? [];
        return required.All(t => HasTag(watchEvent, t)) && (any.Count == 0 || any.Any(t => HasTag(watchEvent, t)));
    }

    private static bool HasTag(WatchEvent watchEvent, string tag) =>
        (watchEvent.Tags ?? []).Contains(tag, StringComparer.OrdinalIgnoreCase);

    private static bool GroupsMatch(RuleSettings rule, WatchEvent watchEvent, RuleMatchContext context)
    {
        if (rule.Groups == GroupFilter.Any)
        {
            return true;
        }

        ObjectRef? group;
        switch (watchEvent.Kind)
        {
            case WatchEventKind.GroupMemberAdded or WatchEventKind.GroupMemberRemoved:
                group = watchEvent.Object;   // the group is the object
                break;
            case WatchEventKind.PrivilegeGranted or WatchEventKind.PrivilegeRevoked:
                group = watchEvent.Member;   // the privileged group is the member
                break;
            default:
                return true;                 // the filter applies to membership and privilege kinds only
        }

        return group is not null && rule.Groups switch
        {
            GroupFilter.Privileged => context.IsPrivilegedRoot(group, watchEvent),
            GroupFilter.WatchList => context.IsWatchListGroup(group, watchEvent),
            GroupFilter.Explicit => context.ContainsDn(rule.GroupDns ?? [], group.Dn, watchEvent),
            _ => true,
        };
    }
}
