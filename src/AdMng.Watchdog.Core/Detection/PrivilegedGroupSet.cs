using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The privileged roots of the PrivilegeEvaluator (section 10.8 step 1) with tokens expanded: groups by SID (Critical /
/// High tiers), by sAMAccountName, by DN, plus the watchdog's own role and host groups from the bootstrap file (High).
/// A group in both tiers is Critical. When the forest root domain SID is unknown, the root-domain RIDs 518/519/527 match
/// within any domain SID.
/// </summary>
public sealed class PrivilegedGroupSet
{
    private static readonly uint[] RootDomainRids = [WellKnownRids.SchemaAdmins, WellKnownRids.EnterpriseAdmins, WellKnownRids.EnterpriseKeyAdmins];

    private readonly HashSet<string> _criticalSids;
    private readonly HashSet<string> _highSids;
    private readonly HashSet<string> _highNames;
    private readonly List<DistinguishedName> _criticalDns;
    private readonly List<DistinguishedName> _highDns;
    private readonly HashSet<string> _protectRemovalSids;
    private readonly List<DistinguishedName> _watchListDns;
    private readonly bool _rootSidUnknown;

    public PrivilegedGroupSet(PrivilegedGroupSettings settings, DirectoryFacts facts, IEnumerable<string>? roleGroupSids = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(facts);
        _rootSidUnknown = facts.RootDomainSid is null;
        _criticalSids = ExpandSids(settings.CriticalSids, facts);
        _highSids = ExpandSids(settings.HighSids, facts);
        _highSids.UnionWith(roleGroupSids ?? []);
        _highNames = new HashSet<string>(settings.HighSamAccountNames, StringComparer.OrdinalIgnoreCase);
        _criticalDns = ExpandDns(settings.AdditionalCriticalDns, facts);
        _highDns = ExpandDns(settings.AdditionalHighDns, facts);
        _protectRemovalSids = ExpandSids(settings.ProtectRemovalSids, facts);
        _watchListDns = ExpandDns(settings.WatchListDns, facts);
    }

    /// <summary>SIDs of the root groups (Critical and High), expanded.</summary>
    public IReadOnlyCollection<string> RootSids => [.. _criticalSids, .. _highSids];

    /// <summary>sAMAccountNames of High root groups (e.g. DnsAdmins).</summary>
    public IReadOnlyCollection<string> RootSamAccountNames => _highNames;

    /// <summary>DNs of root groups configured by DN.</summary>
    public IReadOnlyCollection<DistinguishedName> RootDns => [.. _criticalDns, .. _highDns];

    /// <summary>Tier of a group when it is a privileged root; null otherwise.</summary>
    public PrivilegeTier? TierOf(string? sid, string? samAccountName, string? dn)
    {
        var critical = (sid is not null && (_criticalSids.Contains(sid) || IsRootRidMatch(sid)))
            || (dn is not null && _criticalDns.Any(d => DistinguishedName.AreEqual(d.ToString(), dn)));
        if (critical)
        {
            return PrivilegeTier.Critical;
        }

        var high = (sid is not null && _highSids.Contains(sid))
            || (samAccountName is not null && _highNames.Contains(samAccountName))
            || (dn is not null && _highDns.Any(d => DistinguishedName.AreEqual(d.ToString(), dn)));
        return high ? PrivilegeTier.High : null;
    }

    /// <summary>Removal from such a group raises High (Protected Users).</summary>
    public bool IsProtectRemoval(string? sid) => sid is not null && _protectRemovalSids.Contains(sid);

    /// <summary>The group is on the watch list (GroupFilter.WatchList).</summary>
    public bool IsWatchList(string? dn) => dn is not null && _watchListDns.Any(d => DistinguishedName.AreEqual(d.ToString(), dn));

    private bool IsRootRidMatch(string sid) =>
        _rootSidUnknown && sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
        && AdConvert.GetRid(sid) is { } rid && RootDomainRids.Contains(rid);

    private static HashSet<string> ExpandSids(IEnumerable<string> values, DirectoryFacts facts) =>
        values.Select(v => DetectionTokens.Expand(v, facts.DomainDn, facts.DomainSid, facts.RootDomainSid))
            .Where(v => !DetectionTokens.HasToken(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static List<DistinguishedName> ExpandDns(IEnumerable<string> values, DirectoryFacts facts) =>
        values.Select(v => DetectionTokens.Expand(v, facts.DomainDn, facts.DomainSid, facts.RootDomainSid))
            .Select(v => DistinguishedName.TryParse(v, out var dn) && !dn.IsRoot ? dn : null)
            .OfType<DistinguishedName>()
            .ToList();
}
