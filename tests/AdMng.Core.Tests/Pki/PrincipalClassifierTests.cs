using AdMng.Core.ActiveDirectory;
using AdMng.Core.Pki;
using AdMng.Core.PrivilegedAccess;
using AdMng.Testing;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// Decision 4 of docs/dev/p-certificate-services.md: the principal classes of the ESC review – the fixed SIDs, the RIDs of
/// any forest domain (also a child domain's), the catalogue and the graph reach, the CA computers and the accounts the
/// account pass found to be Tier 0.
/// </summary>
public class PrincipalClassifierTests
{
    private const string Root = FakeDirectorySession.DefaultDomainSid;
    private const string Child = "S-1-5-21-1111111111-2222222222-3333333333";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("S-1-5-18")]       // SYSTEM
    [InlineData("S-1-5-9")]        // Enterprise Domain Controllers
    [InlineData("S-1-5-10")]       // PRINCIPAL SELF
    [InlineData("S-1-3-0")]        // CREATOR OWNER
    [InlineData("S-1-5-32-544")]   // Administrators
    [InlineData("S-1-5-32-548")]   // Account Operators (catalogue Tier 0)
    [InlineData("S-1-5-32-551")]   // Backup Operators (catalogue Tier 0)
    public void WellKnownTier0Sids_AreTier0(string sid) => Assert.Equal(PrincipalClass.Tier0, Classifier().Classify(sid));

    [Theory]
    [InlineData("S-1-1-0")]        // Everyone
    [InlineData("S-1-5-11")]       // Authenticated Users
    [InlineData("S-1-5-7")]        // Anonymous
    [InlineData("S-1-5-15")]       // This Organization
    [InlineData("S-1-5-4")]        // Interactive
    [InlineData("S-1-5-2")]        // Network
    [InlineData("S-1-5-32-545")]   // Users
    [InlineData("S-1-5-32-546")]   // Guests
    public void WellKnownBroadSids_AreBroad(string sid) => Assert.Equal(PrincipalClass.Broad, Classifier().Classify(sid));

    [Theory]
    [InlineData(500u)]
    [InlineData(512u)]
    [InlineData(516u)]
    [InlineData(517u)]
    [InlineData(518u)]
    [InlineData(519u)]
    [InlineData(521u)]
    [InlineData(498u)]
    [InlineData(526u)]
    [InlineData(527u)]
    public void Tier0Rids_AreTier0_InTheWindowsDomain_AndInAnyOtherForestDomain(uint rid)
    {
        var classifier = Classifier(domainSid: Root, rootDomainSid: Root);
        Assert.Equal(PrincipalClass.Tier0, classifier.Classify($"{Root}-{rid}"));
        Assert.Equal(PrincipalClass.Tier0, classifier.Classify($"{Child}-{rid}"));
    }

    [Theory]
    [InlineData(513u)]
    [InlineData(514u)]
    [InlineData(515u)]
    public void BroadRids_AreBroad_InAnyForestDomain(uint rid)
    {
        var classifier = Classifier(domainSid: Root, rootDomainSid: Root);
        Assert.Equal(PrincipalClass.Broad, classifier.Classify($"{Root}-{rid}"));
        Assert.Equal(PrincipalClass.Broad, classifier.Classify($"{Child}-{rid}"));
    }

    [Fact]
    public void ChildDomainUsers_IsBroad_AndTheRootsDomainAdmins_AreTier0_FromAChildWindow()
    {
        // A window on the child domain (DZIAL\…): the forest root's Domain Admins own the default templates.
        var classifier = Classifier(domainSid: Child, rootDomainSid: Root);

        Assert.Equal(PrincipalClass.Broad, classifier.Classify($"{Child}-{WellKnownRids.DomainUsers}"));
        Assert.Equal(PrincipalClass.Tier0, classifier.Classify($"{Root}-{WellKnownRids.DomainAdmins}"));
        Assert.Equal(PrincipalClass.Tier0, classifier.Classify($"{Root}-{WellKnownRids.EnterpriseAdmins}"));
        Assert.Equal(PrincipalClass.Other, classifier.Classify($"{Child}-1105"));
    }

    [Fact]
    public void TheCaComputers_AndTheTier0AccountsOfThePass_AreTier0()
    {
        var ca = $"{Root}-1601";
        var admin = $"{Root}-1602";
        var classifier = new PrincipalClassifier(Root, Root, new PrivilegedAccessOptions(), null, [ca]) { Tier0Accounts = [admin] };

        Assert.Equal(PrincipalClass.Tier0, classifier.Classify(ca));
        Assert.Equal(PrincipalClass.Tier0, classifier.Classify(admin));
        Assert.Equal([admin], classifier.Tier0Accounts);
        Assert.Equal(PrincipalClass.Other, Classifier().Classify(ca));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-1105")]
    [InlineData("S-1-5-32-555")] // Remote Desktop Users (catalogue Elevated)
    [InlineData("S-1-5-21-1-2")] // not a domain SID: its last part is no RID of a domain
    [InlineData("nie-SID")]
    public void AnythingElse_IsOther(string sid) => Assert.Equal(PrincipalClass.Other, Classifier().Classify(sid));

    [Fact]
    public async Task TheGraph_MakesGroupsReachingTier0_Tier0_AndNamesThem()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var nested = builder.AddGroup("GG_Administratorzy_Serwerów", company.GroupsOu);
        var deeper = builder.AddGroup("GG_Zespół_Wdrożeń", company.GroupsOu);
        builder.AddMembers(builder.DomainAdminsDn, nested);
        builder.AddMembers(nested, deeper);
        var dnsAdmins = builder.AddGroup("DnsAdmins", builder.UsersDn, GroupScope.DomainLocal);
        var graph = await GroupGraph.ReadAsync(builder.Session, null, 500, Ct);
        var withGraph = new PrincipalClassifier(Root, Root, new PrivilegedAccessOptions { DomainSid = Root, RootDomainSid = Root }, graph, []);
        var withoutGraph = Classifier();

        // Nested (twice) into Domain Admins: Tier 0 through the graph; DnsAdmins by its name (no well-known RID).
        Assert.Equal(PrincipalClass.Tier0, withGraph.Classify(builder.SidOf(nested)));
        Assert.Equal(PrincipalClass.Tier0, withGraph.Classify(builder.SidOf(deeper)));
        Assert.Equal(PrincipalClass.Tier0, withGraph.Classify(builder.SidOf(dnsAdmins)));
        Assert.Equal(PrincipalClass.Other, withGraph.Classify(builder.SidOf(company.GroupIt)));
        Assert.Equal(PrincipalClass.Other, withoutGraph.Classify(builder.SidOf(deeper)));
        Assert.Equal(PrincipalClass.Other, withoutGraph.Classify(builder.SidOf(dnsAdmins)));
    }

    [Fact]
    public void TheOperatorsLevels_DecideTheCatalogueGroups_NotTheFixedRids()
    {
        var options = new PrivilegedAccessOptions
        {
            DomainSid = Root,
            LevelOverrides = new Dictionary<string, PrivilegeLevel> { ["AccountOperators"] = PrivilegeLevel.High, ["DomainAdmins"] = PrivilegeLevel.High },
        };
        var classifier = new PrincipalClassifier(Root, Root, options, null, []);

        Assert.Equal(PrincipalClass.Other, classifier.Classify("S-1-5-32-548"));
        Assert.Equal(PrincipalClass.Tier0, classifier.Classify($"{Root}-{WellKnownRids.DomainAdmins}"));
        Assert.Equal(PrincipalClass.Tier0, classifier.Classify("S-1-5-32-544"));
    }

    private static PrincipalClassifier Classifier(string? domainSid = Root, string? rootDomainSid = null) =>
        new(domainSid, rootDomainSid, new PrivilegedAccessOptions { DomainSid = domainSid, RootDomainSid = rootDomainSid }, null, []);
}
