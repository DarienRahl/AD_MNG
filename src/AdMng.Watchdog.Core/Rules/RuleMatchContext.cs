using System.Collections.Concurrent;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// One configuration prepared for matching (section 12.1): token values, the privileged roots of
/// <c>PrivilegedGroups</c> (by SID pattern, sAMAccountName and DN), the watch list, and caches of expanded/parsed DNs
/// and SIDs. Thread-safe; create one per configuration and token set (<see cref="RuleEngine.CreateContext"/>).
/// </summary>
/// <remarks>When <see cref="ConfigTokens.DomainDn"/> is unknown, <c>{domain}</c> expands to the domain components of the
/// event's object (one instance watches one domain).</remarks>
public sealed class RuleMatchContext
{
    private readonly ConcurrentDictionary<(string Value, string? Domain), DistinguishedName?> _dns = new();
    private readonly ConcurrentDictionary<string, SidPattern?> _sids = new(StringComparer.OrdinalIgnoreCase);
    private readonly SidPattern[] _privilegedSids;
    private readonly HashSet<string> _privilegedNames;
    private readonly string[] _privilegedDns;

    private RuleMatchContext(WatchdogConfiguration configuration, ConfigTokens tokens, IEnumerable<string> additionalPrivilegedRootSids)
    {
        Configuration = configuration;
        Tokens = tokens;
        var groups = configuration.PrivilegedGroups ?? new PrivilegedGroupSettings();
        _privilegedSids = [.. (groups.CriticalSids ?? []).Concat(groups.HighSids ?? []).Concat(additionalPrivilegedRootSids)
            .Select(ResolveSid)
            .OfType<SidPattern>()];
        _privilegedNames = new HashSet<string>((groups.HighSamAccountNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);
        _privilegedDns = [.. (groups.AdditionalCriticalDns ?? []).Concat(groups.AdditionalHighDns ?? [])];
    }

    /// <summary>The configuration (effective form: normalized, built-in rules merged).</summary>
    public WatchdogConfiguration Configuration { get; }

    /// <summary>The token values used for expansion.</summary>
    public ConfigTokens Tokens { get; }

    /// <summary>Prepares <paramref name="configuration"/> (taken as is – pass the effective form).</summary>
    /// <param name="configuration">Effective configuration.</param>
    /// <param name="tokens">Token values (unknown values degrade as described on <see cref="ConfigTokens"/>).</param>
    /// <param name="additionalPrivilegedRootSids">Further privileged root group SIDs, e.g. the watchdog's own role and
    /// host groups from the bootstrap file (10.8).</param>
    public static RuleMatchContext Create(WatchdogConfiguration configuration, ConfigTokens tokens, IEnumerable<string>? additionalPrivilegedRootSids = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tokens);
        return new RuleMatchContext(configuration, tokens, additionalPrivilegedRootSids ?? []);
    }

    /// <summary>A configured DN expanded (for the domain of <paramref name="watchEvent"/> when the domain is unknown) and
    /// parsed; null when empty, unexpandable or not a DN – such an entry never matches.</summary>
    public DistinguishedName? ResolveDn(string? configured, WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var domain = Tokens.DomainDn ?? DomainOf(watchEvent);
        return _dns.GetOrAdd((configured, domain), static (key, tokens) =>
        {
            var expanded = (tokens with { DomainDn = key.Domain ?? tokens.DomainDn }).ExpandDn(key.Value);
            return expanded is not null && DistinguishedName.TryParse(expanded, out var dn) && !dn.IsRoot ? dn : null;
        }, Tokens);
    }

    /// <summary>A configured SID as a pattern (<see cref="ConfigTokens.ExpandSid"/>); null when not a SID.</summary>
    public SidPattern? ResolveSid(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? null : _sids.GetOrAdd(configured, static (value, tokens) => tokens.ExpandSid(value), Tokens);

    /// <summary>
    /// A configured actor SID (<c>ActorFilter.ExcludeActors</c>, <c>RuleExclusion.Actor</c>): only an exact SID. While
    /// the value of a <c>{domainSid}</c> / <c>{rootDomainSid}</c> token is unknown the entry is null and excludes nothing –
    /// the relative-id fallback of <see cref="ConfigTokens.ExpandSid"/> (any domain) is meant for privileged roots, and
    /// for an exclusion it would fail open to the same RID of every trusted domain.
    /// </summary>
    public SidPattern? ResolveActorSid(string? configured) =>
        ResolveSid(configured) is { Sid: not null } exact ? exact : null;

    /// <summary>True when <paramref name="group"/> is a privileged root: its SID is one of the critical/high SIDs, its
    /// sAMAccountName one of <c>HighSamAccountNames</c>, or its DN one of the additional critical/high DNs.</summary>
    public bool IsPrivilegedRoot(ObjectRef group, WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(group);
        return (group.Sid is { } sid && _privilegedSids.Any(p => p.Matches(sid)))
            || (group.SamAccountName is { } name && _privilegedNames.Contains(name.Trim()))
            || ContainsDn(_privilegedDns, group.Dn, watchEvent);
    }

    /// <summary>True when the DN of <paramref name="group"/> is in <c>PrivilegedGroups.WatchListDns</c>.</summary>
    public bool IsWatchListGroup(ObjectRef group, WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(group);
        return ContainsDn(Configuration.PrivilegedGroups?.WatchListDns ?? [], group.Dn, watchEvent);
    }

    /// <summary>True when <paramref name="dn"/> equals one of <paramref name="configuredDns"/> (normalized comparison).</summary>
    public bool ContainsDn(IEnumerable<string> configuredDns, string? dn, WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(configuredDns);
        if (!DistinguishedName.TryParse(dn, out var parsed) || parsed.IsRoot)
        {
            return false;
        }

        return configuredDns.Any(c => ResolveDn(c, watchEvent) is { } candidate && candidate.Equals(parsed));
    }

    private static string? DomainOf(WatchEvent watchEvent)
    {
        if (!DistinguishedName.TryParse(watchEvent.Object?.Dn, out var dn))
        {
            return null;
        }

        var domain = dn.DomainComponent.ToString();
        return domain.Length == 0 ? null : domain;
    }
}
