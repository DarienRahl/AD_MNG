using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// One rule against one event (section 12.1). A rule matches when all hold: enabled; kinds and object kinds; scope;
/// attributes; required/any tags; the group filter (membership and privilege kinds only); <c>MinSeverity</c> ≤ the event's
/// own severity (<see cref="EventSeverities.Own"/> – never one raised by another rule's override); no actor exclusion (SIDs of a resolved actor only, <c>ExcludeSelfChanges</c> with tag <c>selfChange</c>);
/// no applying exclusion. While attribution is pending a rule with actor conditions matches
/// <see cref="RuleMatchOutcome.Provisional"/>ly; after the grace period an unresolved actor excludes nothing. The
/// locked <c>health</c> rule always matches health events; <see cref="WatchEventKind.Test"/> never matches.
/// </summary>
/// <remarks>
/// Self-changes (11.4 #5): <c>ExcludeSelfChanges</c> never hides a Tier-0 kind written by the object itself (a relayed
/// computer writing RBCD, SPNs or SID history on itself is the attack) – only the routine self-maintenance of an ordinary
/// computer (KeyCredentialChanged key rotation) or a service account (ServicePrincipalNameAdded self-registration), see
/// <see cref="IsExcludableSelfChange"/>. With <c>Attribution.SuppressComputerSelfChanges</c> every rule behaves as if
/// <c>ExcludeSelfChanges</c> were set for computer-noise changes of a computer (9.3, <see cref="IsComputerNoiseChange"/>).
/// Actor SIDs with a <c>{domainSid}</c> / <c>{rootDomainSid}</c> token whose value is unknown exclude nothing
/// (<see cref="RuleMatchContext.ResolveActorSid"/>).
/// </remarks>
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
            || (rule.MinSeverity is { } minimum && EventSeverities.Own(watchEvent) < minimum))
        {
            return RuleMatchOutcome.NoMatch;
        }

        var provisional = false;
        var actors = rule.Actors ?? new ActorFilter();
        foreach (var excluded in actors.ExcludeActors ?? [])
        {
            switch (ActorCondition.Evaluate(watchEvent.Attribution, context.ResolveActorSid(excluded), attributionGraceExpired))
            {
                case ActorConditionState.Matches:
                    return RuleMatchOutcome.NoMatch;
                case ActorConditionState.Pending:
                    provisional = true;
                    break;
            }
        }

        var excludeSelfChanges = actors.ExcludeSelfChanges
            || (context.Configuration.Attribution?.SuppressComputerSelfChanges == true && IsComputerNoiseChange(watchEvent));
        if (excludeSelfChanges && IsExcludableSelfChange(watchEvent))
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

    /// <summary>
    /// True when a self-change of <paramref name="watchEvent"/> may be hidden by <c>ExcludeSelfChanges</c>: every kind
    /// outside <see cref="Tier0Catalog.Kinds"/>; of the Tier-0 kinds only KeyCredentialChanged of a computer (key
    /// rotation) and ServicePrincipalNameAdded of a service account (SPN self-registration), and never for a domain
    /// controller or a privileged object.
    /// </summary>
    public static bool IsExcludableSelfChange(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        if (!Tier0Catalog.IsTier0Kind(watchEvent.Kind))
        {
            return true;
        }

        if (HasTag(watchEvent, EventTags.DomainController) || HasTag(watchEvent, EventTags.Privileged))
        {
            return false;
        }

        return (watchEvent.Kind, watchEvent.Object?.Kind) is (WatchEventKind.KeyCredentialChanged, WatchObjectKind.Computer)
            or (WatchEventKind.ServicePrincipalNameAdded, WatchObjectKind.ServiceAccount);
    }

    /// <summary>
    /// True for a change of a computer that touches only computer-noise attributes (9.3: pwdLastSet, operating system,
    /// dNSHostName, encryption types, userCertificate, and servicePrincipalName values naming the computer itself) – the
    /// changes <c>Attribution.SuppressComputerSelfChanges</c> excludes when the computer made them itself. Never for the
    /// service's own account (tag <c>watchdogAccount</c>, stage B design 9.3): its self-writes (RC4 re-enabled, an extra
    /// SPN) are what <c>watchdog-self</c> reports.
    /// </summary>
    public static bool IsComputerNoiseChange(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        if (watchEvent.Object is not { Kind: WatchObjectKind.Computer } computer || (watchEvent.Changes ?? []).Count == 0
            || HasTag(watchEvent, EventTags.WatchdogAccount))
        {
            return false;
        }

        var netbios = computer.SamAccountName?.TrimEnd('$');
        var dnsHostName = netbios is { Length: > 0 } && DistinguishedName.TryParse(computer.Dn, out var dn) && dn.DnsDomain is { Length: > 0 } domain
            ? netbios + "." + domain
            : null;
        return watchEvent.Changes!.All(change => change is not null
            && NoiseAttributes.ComputerNoise.Contains(change.Attribute)
            && (!string.Equals(change.Attribute, "servicePrincipalName", StringComparison.OrdinalIgnoreCase)
                || change.Added.Concat(change.Removed).All(spn => NoiseAttributes.IsOwnHostSpn(spn, dnsHostName, computer.SamAccountName))));
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
