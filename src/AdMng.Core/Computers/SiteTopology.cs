using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Core.Computers;

/// <summary>
/// The sites and subnets of the forest from the configuration partition of the session's DC (K1 design note 4.4): one
/// level of <c>CN=Sites</c> (<c>objectClass=site</c>) and of <c>CN=Subnets,CN=Sites</c> (<c>objectClass=subnet</c>). Read on
/// the first site resolution of a run; in memory only.
/// </summary>
public sealed record SiteTopology(IReadOnlyList<string> Sites, SubnetMap Subnets)
{
    private const string SiteObject = "siteObject";

    public static SiteTopology Empty { get; } = new([], SubnetMap.Empty);

    /// <summary>Reads the sites and subnets (empty when the configuration has no sites container).</summary>
    /// <exception cref="DirectoryException">The configuration partition could not be read.</exception>
    public static async Task<SiteTopology> ReadAsync(IDirectoryReader session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var sitesDn = DistinguishedName.Parse(session.Info.ConfigurationNamingContext).Child("CN", "Sites");
        var sites = new List<string>();
        var subnets = new List<(string, string?)>();
        try
        {
            await foreach (var site in session.SearchAsync(new DirectorySearch
            {
                BaseDn = sitesDn.ToString(),
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.Equal(ActiveDirectory.Attr.ObjectClass, "site"),
                Attributes = [ActiveDirectory.Attr.Name],
            }, cancellationToken).ConfigureAwait(false))
            {
                sites.Add(site.Name);
            }

            await foreach (var subnet in session.SearchAsync(new DirectorySearch
            {
                BaseDn = sitesDn.Child("CN", "Subnets").ToString(),
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.Equal(ActiveDirectory.Attr.ObjectClass, "subnet"),
                Attributes = [ActiveDirectory.Attr.Cn, SiteObject],
                PageSize = 1000,
            }, cancellationToken).ConfigureAwait(false))
            {
                subnets.Add((subnet.GetString(ActiveDirectory.Attr.Cn) ?? subnet.Name, subnet.GetString(SiteObject)));
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // No sites (or no subnets) container.
        }

        sites.Sort(StringComparer.OrdinalIgnoreCase);
        return new SiteTopology(sites, SubnetMap.Build(subnets));
    }
}
