using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Rule scope (12.1 #3): <c>IncludeSubtrees</c> empty or the object's DN / <c>PreviousDn</c> equal to or under one of
/// them; neither DN under any <c>ExcludeSubtrees</c> entry. Tokens are expanded through the
/// <see cref="RuleMatchContext"/>; an entry that cannot be expanded or parsed matches nothing (an include entry then
/// selects nothing, an exclude entry excludes nothing).
/// </summary>
public static class ScopeMatcher
{
    /// <summary>True when the event's object is in scope.</summary>
    public static bool Matches(RuleScope? scope, WatchEvent watchEvent, RuleMatchContext context)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(context);
        if (scope is null)
        {
            return true;
        }

        var dns = EventDns(watchEvent);
        var include = scope.IncludeSubtrees ?? [];
        if (include.Count > 0 && !include.Any(entry => context.ResolveDn(entry, watchEvent) is { } subtree && dns.Any(dn => IsAtOrBelow(dn, subtree))))
        {
            return false;
        }

        var exclude = scope.ExcludeSubtrees ?? [];
        return !exclude.Any(entry => context.ResolveDn(entry, watchEvent) is { } subtree && dns.Any(dn => IsAtOrBelow(dn, subtree)));
    }

    /// <summary>True when <paramref name="dn"/> equals <paramref name="subtree"/> or lies below it.</summary>
    public static bool IsAtOrBelow(DistinguishedName dn, DistinguishedName subtree)
    {
        ArgumentNullException.ThrowIfNull(dn);
        ArgumentNullException.ThrowIfNull(subtree);
        return dn.Equals(subtree) || dn.IsDescendantOf(subtree);
    }

    /// <summary>The parsed object DN and previous DN of an event (unparsable ones are left out).</summary>
    public static IReadOnlyList<DistinguishedName> EventDns(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        var result = new List<DistinguishedName>(2);
        foreach (var value in new[] { watchEvent.Object?.Dn, watchEvent.PreviousDn })
        {
            if (DistinguishedName.TryParse(value, out var dn) && !dn.IsRoot)
            {
                result.Add(dn);
            }
        }

        return result;
    }
}
