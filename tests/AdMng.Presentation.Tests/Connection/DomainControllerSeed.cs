using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Connection;

/// <summary>
/// Seeds domain controllers the way AD records them in the configuration partition: <c>CN=&lt;site&gt;,CN=Sites</c> →
/// <c>CN=Servers</c> → <c>server</c> (dNSHostName) → <c>CN=NTDS Settings</c> (nTDSDSA: options, objectCategory,
/// msDS-HasDomainNCs); the PDC emulator is the <c>fSMORoleOwner</c> of the domain head.
/// </summary>
internal static class DomainControllerSeed
{
    public const string DefaultSite = "Default-First-Site-Name";

    /// <returns>DN of the NTDS Settings object.</returns>
    public static string Add(
        FakeDirectorySession session,
        string hostName,
        string site = DefaultSite,
        bool globalCatalog = true,
        bool readOnly = false,
        bool pdc = false,
        string? domainNc = null)
    {
        var info = session.Info;
        var sites = Ensure(session, Ensure(session, info.ConfigurationNamingContext, "configuration"), "Sites", "sitesContainer");
        var servers = Ensure(session, Ensure(session, sites, site, "site"), "Servers", "serversContainer");
        var label = hostName.Split('.')[0].ToUpperInvariant();
        var server = session.Seed(DistinguishedName.Parse(servers).Child("CN", label).ToString(), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "server" },
            [Attr.DnsHostName] = hostName,
        });
        var ntds = session.Seed(DistinguishedName.Parse(server).Child("CN", "NTDS Settings").ToString(), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "applicationSettings", "nTDSDSA" },
            [Attr.ObjectCategory] = $"CN={(readOnly ? "NTDS-DSA-RO" : "NTDS-DSA")},{info.SchemaNamingContext}",
            ["options"] = globalCatalog ? "1" : "0",
            ["msDS-HasDomainNCs"] = domainNc ?? info.DefaultNamingContext,
        });
        if (pdc)
        {
            session.SeedAttributes(info.DefaultNamingContext, new Dictionary<string, object?> { ["fSMORoleOwner"] = ntds });
        }

        return ntds;
    }

    private static string Ensure(FakeDirectorySession session, string dn, string objectClass)
    {
        if (!session.Exists(dn))
        {
            session.Seed(dn, new Dictionary<string, object?> { [Attr.ObjectClass] = new[] { "top", objectClass } });
        }

        return dn;
    }

    private static string Ensure(FakeDirectorySession session, string parentDn, string name, string objectClass) =>
        Ensure(session, DistinguishedName.Parse(parentDn).Child("CN", name).ToString(), objectClass);
}
