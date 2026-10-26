namespace AdMng.Core.PrivilegedAccess;

/// <summary>Which accounts are an account's peers.</summary>
public enum PeerDimension
{
    /// <summary>Same parent OU (or container).</summary>
    OrganizationalUnit,

    /// <summary>Same department.</summary>
    Department,

    /// <summary>Same title.</summary>
    Title,
}

/// <summary>
/// The peer comparison of README U4: "a group held by fewer than <see cref="MaxSharePercent"/> % of at least
/// <see cref="MinPeers"/> peers" is unusual for the account.
/// </summary>
public sealed record PeerOptions
{
    public const int MaxMinPeers = 10_000;

    public bool ByOrganizationalUnit { get; init; } = true;

    public bool ByDepartment { get; init; } = true;

    public bool ByTitle { get; init; }

    /// <summary>Fewest peers (the account itself not counted) a comparison needs.</summary>
    public int MinPeers { get; init; } = 5;

    /// <summary>A group held by fewer than this share of the peers (percent, 0–100) is unusual.</summary>
    public int MaxSharePercent { get; init; } = 20;

    public bool IsEnabled => ByOrganizationalUnit || ByDepartment || ByTitle;

    /// <summary>Values of a hand-edited settings file brought into range.</summary>
    public PeerOptions Normalized() => this with
    {
        MinPeers = Math.Clamp(MinPeers, 1, MaxMinPeers),
        MaxSharePercent = Math.Clamp(MaxSharePercent, 1, 100),
    };

    /// <summary>The dimensions that are switched on.</summary>
    public IEnumerable<PeerDimension> Dimensions()
    {
        if (ByOrganizationalUnit)
        {
            yield return PeerDimension.OrganizationalUnit;
        }

        if (ByDepartment)
        {
            yield return PeerDimension.Department;
        }

        if (ByTitle)
        {
            yield return PeerDimension.Title;
        }
    }
}

/// <summary>An account in the peer comparison.</summary>
/// <param name="Key">Identity of the account (normalized DN).</param>
/// <param name="ParentDn">The DN of its parent container.</param>
/// <param name="Department">department.</param>
/// <param name="Title">title.</param>
/// <param name="GroupDns">The groups it holds directly (memberOf and the primary group).</param>
public sealed record PeerSubject(string Key, string? ParentDn, string? Department, string? Title, IReadOnlyList<string> GroupDns);

/// <summary>
/// Finds the groups an account holds that none or few of its peers hold - also ordinary groups, not only the privileged
/// ones (README U4, "Odstępstwa od grupy porównawczej"). Pure and in memory: the peers are the accounts of the run's
/// scope. Direct groups are compared (nested ones would repeat every unusual group along its parents).
/// </summary>
public static class PeerComparison
{
    /// <summary>Peer outlier findings per account key.</summary>
    /// <param name="subjects">All accounts of the scope that take part (users).</param>
    /// <param name="options">Dimensions and thresholds.</param>
    /// <param name="grantedLevel">What a group grants (by DN): Elevated or more makes the finding Elevated, else Minor.</param>
    /// <param name="groupName">Name of a group DN for the finding.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<PrivilegeFinding>> Compare(
        IReadOnlyList<PeerSubject> subjects,
        PeerOptions options,
        Func<string, PrivilegeLevel> grantedLevel,
        Func<string, string> groupName)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(grantedLevel);
        ArgumentNullException.ThrowIfNull(groupName);
        var normalized = options.Normalized();
        var result = new Dictionary<string, List<PrivilegeFinding>>(StringComparer.Ordinal);
        foreach (var dimension in normalized.Dimensions())
        {
            var sets = subjects
                .Select(s => (Subject: s, Value: ValueOf(s, dimension)))
                .Where(x => x.Value is not null)
                .GroupBy(x => dimension == PeerDimension.OrganizationalUnit ? GroupGraph.Key(x.Value!) : x.Value!.ToUpperInvariant(), StringComparer.Ordinal);
            foreach (var set in sets)
            {
                var members = set.ToList();
                var peerCount = members.Count - 1;
                if (peerCount < normalized.MinPeers)
                {
                    continue;
                }

                var holders = new Dictionary<string, (string Dn, int Count)>(StringComparer.Ordinal);
                foreach (var (subject, _) in members)
                {
                    foreach (var dn in subject.GroupDns.DistinctBy(GroupGraph.Key, StringComparer.Ordinal))
                    {
                        var key = GroupGraph.Key(dn);
                        holders[key] = holders.TryGetValue(key, out var entry) ? (entry.Dn, entry.Count + 1) : (dn, 1);
                    }
                }

                foreach (var (subject, value) in members)
                {
                    var unusual = subject.GroupDns
                        .DistinctBy(GroupGraph.Key, StringComparer.Ordinal)
                        .Select(dn => (Dn: dn, Others: holders[GroupGraph.Key(dn)].Count - 1))
                        .Where(g => g.Others * 100 < normalized.MaxSharePercent * peerCount)
                        .Select(g => new PeerOutlierGroup(g.Dn, groupName(g.Dn), g.Others, grantedLevel(g.Dn)))
                        .OrderBy(g => g.Holders)
                        .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                    if (unusual.Count == 0)
                    {
                        continue;
                    }

                    var level = unusual.Any(g => g.GrantedLevel >= PrivilegeLevel.Elevated) ? PrivilegeLevel.Elevated : PrivilegeLevel.Minor;
                    var finding = new PrivilegeFinding
                    {
                        Source = PrivilegeSource.Peers,
                        Kind = PrivilegeFindingKind.PeerOutlier,
                        Level = level,
                        Scope = PrivilegeScopeKind.Peers,
                        ScopeDn = dimension == PeerDimension.OrganizationalUnit ? value : null,
                        Values = [.. unusual.Select(g => g.Name)],
                        Peer = new PeerOutlierDetails(dimension, value!, peerCount, unusual),
                    };
                    if (!result.TryGetValue(subject.Key, out var list))
                    {
                        result[subject.Key] = list = [];
                    }

                    // The same unusual groups among other peers (the department of an OU, say) add nothing new.
                    var reported = list.SelectMany(f => f.Peer!.Groups).Select(g => GroupGraph.Key(g.Dn)).ToHashSet(StringComparer.Ordinal);
                    if (!unusual.All(g => reported.Contains(GroupGraph.Key(g.Dn))))
                    {
                        list.Add(finding);
                    }
                }
            }
        }

        return result.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (IReadOnlyList<PrivilegeFinding>)kv.Value, StringComparer.Ordinal);
    }

    private static string? ValueOf(PeerSubject subject, PeerDimension dimension) => dimension switch
    {
        PeerDimension.OrganizationalUnit => subject.ParentDn,
        PeerDimension.Department => Trimmed(subject.Department),
        _ => Trimmed(subject.Title),
    };

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
