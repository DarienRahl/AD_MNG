using AdMng.Core.GroupPolicy;

namespace AdMng.Core.Tests.GroupPolicy;

/// <summary>gPLink parsing and which GPOs apply to an object in which order (link order, block inheritance, enforced).</summary>
public sealed class GroupPolicyInheritanceTests
{
    private const string Domain = "DC=contoso,DC=test";
    private const string Firma = "OU=Firma," + Domain;
    private const string Komputery = "OU=Komputery,OU=Firma," + Domain;
    private const string Krakow = "OU=Kraków,OU=Komputery,OU=Firma," + Domain;
    private const string Site = "CN=Warszawa-Śródmieście,CN=Sites,CN=Configuration," + Domain;

    private static readonly Guid DomainPolicy = Guid.Parse("31B2F340-016D-11D2-945F-00C04FB984F9");
    private static readonly Guid DomainEnforced = Guid.Parse("A0000000-0000-0000-0000-000000000001");
    private static readonly Guid FirmaPolicy = Guid.Parse("A0000000-0000-0000-0000-000000000002");
    private static readonly Guid FirmaEnforced = Guid.Parse("A0000000-0000-0000-0000-000000000003");
    private static readonly Guid StacjeLow = Guid.Parse("A0000000-0000-0000-0000-000000000004");
    private static readonly Guid StacjeHigh = Guid.Parse("A0000000-0000-0000-0000-000000000005");
    private static readonly Guid Disabled = Guid.Parse("A0000000-0000-0000-0000-000000000006");
    private static readonly Guid SitePolicy = Guid.Parse("A0000000-0000-0000-0000-000000000007");
    private static readonly Guid KrakowPolicy = Guid.Parse("A0000000-0000-0000-0000-000000000008");

    [Fact]
    public void GpLink_KeepsTheValueOrder_LinkOrderOneIsTheLast_OptionsAndGuids()
    {
        var links = GroupPolicyLinks.Parse(
            $"[LDAP://cn={{{StacjeLow}}},cn=policies,cn=system,DC=contoso,DC=test;0]" +
            $"[LDAP://CN={{{Disabled}}},CN=Policies,CN=System,DC=contoso,DC=test;1]" +
            "[LDAP://nie jest DN;0]" +
            $"[LDAP://CN={{{StacjeHigh}}},CN=Policies,CN=System,DC=contoso,DC=test;3]" +
            "[bez zamknięcia");

        Assert.Equal([StacjeLow, Disabled, StacjeHigh], links.Select(l => l.GpoId!.Value));
        Assert.Equal([3, 2, 1], links.Select(l => l.Order));
        Assert.Equal([false, true, true], links.Select(l => l.Disabled));
        Assert.Equal([false, false, true], links.Select(l => l.Enforced));
        Assert.Empty(GroupPolicyLinks.Parse(null));
        Assert.Empty(GroupPolicyLinks.Parse(" "));
        Assert.Null(GroupPolicyLinks.Parse("[LDAP://CN=Zasady,CN=Policies,DC=contoso,DC=test;0]")[0].GpoId);
        Assert.False(GroupPolicyLinks.TryParseGpoId("{00000000-0000-0000-0000-000000000000}", out _));
        Assert.False(GroupPolicyLinks.TryParseGpoId("31B2F340-016D-11D2-945F-00C04FB984F9", out _));
    }

    [Fact]
    public void Order_SiteDomainOus_ByLinkOrder_EnforcedLast_HighestContainerWins()
    {
        var inheritance = Inheritance(blockAtKomputery: false);

        // Application order (the last wins): site, domain, Firma, Komputery (high link order first), then the enforced
        // links from the deepest container up - the domain's enforced link is applied last.
        Assert.Equal(
            [SitePolicy, DomainPolicy, FirmaPolicy, StacjeLow, StacjeHigh, KrakowPolicy, FirmaEnforced, DomainEnforced],
            inheritance.Resolve(Krakow, Site).Select(a => a.GpoId));

        // Without a known site no site link applies; the disabled link never does.
        var withoutSite = inheritance.Resolve(Komputery, null);
        Assert.Equal([DomainPolicy, FirmaPolicy, StacjeLow, StacjeHigh, FirmaEnforced, DomainEnforced], withoutSite.Select(a => a.GpoId));
        Assert.DoesNotContain(withoutSite, a => a.GpoId == Disabled);
        Assert.Equal(Komputery, withoutSite.Single(a => a.GpoId == StacjeHigh).Container.Dn);
        Assert.Equal(1, withoutSite.Single(a => a.GpoId == StacjeHigh).Link.Order);

        // An object in CN=Computers gets the domain (and site) links only.
        Assert.Equal([DomainPolicy, DomainEnforced], inheritance.Resolve("CN=Computers," + Domain, null).Select(a => a.GpoId));
    }

    [Fact]
    public void BlockInheritance_DropsTheNonEnforcedLinksAbove_EnforcedOnesStay()
    {
        var inheritance = Inheritance(blockAtKomputery: true);

        Assert.Equal(
            [StacjeLow, StacjeHigh, KrakowPolicy, FirmaEnforced, DomainEnforced],
            inheritance.Resolve(Krakow, Site).Select(a => a.GpoId));

        // Above the blocking OU nothing changes.
        Assert.Equal([SitePolicy, DomainPolicy, FirmaPolicy, FirmaEnforced, DomainEnforced], inheritance.Resolve(Firma, Site).Select(a => a.GpoId));
    }

    [Fact]
    public void AGpoLinkedTwice_CountsOnce_AtItsWinningPosition()
    {
        var inheritance = new GroupPolicyInheritance(Domain,
        [
            Som(Domain, GroupPolicySomKind.Domain, (DomainPolicy, 0), (FirmaPolicy, 0)),
            Som(Firma, GroupPolicySomKind.OrganizationalUnit, (DomainPolicy, 0)),
        ]);

        var applied = inheritance.Resolve(Firma, null);
        Assert.Equal([FirmaPolicy, DomainPolicy], applied.Select(a => a.GpoId));
        Assert.Equal(Firma, applied[1].Container.Dn);
    }

    private static GroupPolicyInheritance Inheritance(bool blockAtKomputery) => new(Domain,
    [
        Som(Site, GroupPolicySomKind.Site, (SitePolicy, 0)),
        Som(Domain, GroupPolicySomKind.Domain, (DomainPolicy, 0), (DomainEnforced, GroupPolicyLink.EnforcedOption)),
        Som(Firma, GroupPolicySomKind.OrganizationalUnit, (FirmaEnforced, GroupPolicyLink.EnforcedOption), (FirmaPolicy, 0)),
        Som(Komputery, GroupPolicySomKind.OrganizationalUnit, blockAtKomputery,
            (StacjeLow, 0), (Disabled, GroupPolicyLink.DisabledOption | GroupPolicyLink.EnforcedOption), (StacjeHigh, 0)),
        Som(Krakow, GroupPolicySomKind.OrganizationalUnit, (KrakowPolicy, 0)),
    ]);

    private static GroupPolicySom Som(string dn, GroupPolicySomKind kind, params (Guid Gpo, int Options)[] links) => Som(dn, kind, false, links);

    private static GroupPolicySom Som(string dn, GroupPolicySomKind kind, bool block, params (Guid Gpo, int Options)[] links) =>
        new(dn, kind, GroupPolicyLinks.Parse(string.Concat(links.Select(l =>
            $"[LDAP://CN={{{l.Gpo.ToString().ToUpperInvariant()}}},CN=Policies,CN=System,{Domain};{l.Options}]"))), block);
}
