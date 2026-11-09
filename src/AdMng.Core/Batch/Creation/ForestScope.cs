using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Core.Batch.Creation;

/// <summary>The domains of the forest (2.4): their count decides the uniqueness scope, their DNS names the suffix checks of 2.2.</summary>
/// <param name="DomainCount">The number of domain crossRefs (<c>systemFlags</c> bit 2).</param>
/// <param name="DomainDnsNames">Their <c>dnsRoot</c> values.</param>
public sealed record ForestInfo(int DomainCount, IReadOnlyList<string> DomainDnsNames)
{
    /// <summary>The <c>nCName</c> of each domain, in the order of <see cref="DomainDnsNames"/> (empty when a crossRef has none).</summary>
    public IReadOnlyList<string> DomainNamingContexts { get; init; } = [];

    /// <summary>The naming context of the domain <paramref name="dnsName"/>, or null when the forest has no such domain.</summary>
    public string? NamingContextOf(string dnsName)
    {
        ArgumentNullException.ThrowIfNull(dnsName);
        for (var i = 0; i < DomainDnsNames.Count && i < DomainNamingContexts.Count; i++)
        {
            if (string.Equals(DomainDnsNames[i], dnsName, StringComparison.OrdinalIgnoreCase) && DomainNamingContexts[i].Length > 0)
            {
                return DomainNamingContexts[i];
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="dnsName"/> is the DNS name of a domain of the forest (case-insensitive).</summary>
    public bool IsDomain(string dnsName) =>
        DomainDnsNames.Contains(dnsName ?? throw new ArgumentNullException(nameof(dnsName)), StringComparer.OrdinalIgnoreCase);

    public override string ToString() => $"Forest: {DomainCount} domains";
}

/// <summary>Reads the forest's domains (docs/dev/phase3-bulk-create.md 2.4, owner B2).</summary>
public static class ForestScope
{
    /// <summary>The <c>systemFlags</c> bit of a crossRef that names a domain (FLAG_CR_NTDS_DOMAIN).</summary>
    public const int DomainCrossRefFlag = 2;

    /// <summary>
    /// One one-level search of <c>CN=Partitions,&lt;configuration&gt;</c> for crossRefs with <c>systemFlags</c> bit 2,
    /// returning <c>dnsRoot</c> and <c>nCName</c>.
    /// </summary>
    /// <exception cref="DirectoryException">The partitions container cannot be read.</exception>
    public static async Task<ForestInfo> ReadAsync(IDirectoryReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var search = new DirectorySearch
        {
            BaseDn = PartitionsDn(reader.Info),
            Scope = SearchScope.OneLevel,
            Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, "crossRef"), LdapFilter.BitAnd(Attr.SystemFlags, DomainCrossRefFlag)),
            Attributes = ["dnsRoot", "nCName"],
        };
        var names = new List<string>();
        var contexts = new List<string>();
        var count = 0;
        await foreach (var crossRef in reader.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            count++;
            if (crossRef.GetString("dnsRoot") is { Length: > 0 } dnsRoot && !names.Contains(dnsRoot, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(dnsRoot);
                contexts.Add(crossRef.GetString("nCName") ?? string.Empty);
            }
        }

        return new ForestInfo(count, names) { DomainNamingContexts = contexts };
    }

    /// <summary><c>CN=Partitions,&lt;configuration naming context&gt;</c>.</summary>
    public static string PartitionsDn(SessionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return DistinguishedName.Parse(info.ConfigurationNamingContext).Child("CN", "Partitions").ToString();
    }
}
