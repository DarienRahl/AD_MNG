using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Pki;
using AdMng.Core.PrivilegedAccess;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.PrivilegedAccess;

/// <summary>
/// The U4b PKI pass (P1, docs/dev/p-certificate-services.md 2.3, decision 6) on <see cref="FakeDirectoryBuilder.AddPublicKeyAclSample"/>:
/// a default Windows PKI yields no grant (also from a child-domain window), owners only where 2.3 says, the
/// <c>WriteTemplateProperty</c> and <c>WriteAltSecurityIdentities</c> rights, and the typed
/// <see cref="DirectoryAclGrantSource.CollectPublicKeyAsync"/> for ESC5 / ESC14. The exact-set U4b tests stay in
/// <c>DirectoryAclGrantSourceTests</c>, unchanged.
/// </summary>
public class DirectoryAclPublicKeyTests
{
    private const string ChildDomain = "S-1-5-21-1111111111-2222222222-3333333333";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task U4b_ReportsTheDelegationsOnThePki_ButNothingOfTheDefaultPki()
    {
        var scenario = Scenario.Create();
        var acl = scenario.Acl;

        var rendered = Render(scenario, await scenario.CollectAsync());

        Assert.Equal(Sorted(
        [
            $"{acl.PublicKeyServices} | GG_Sprzedaz | Tier0 | WriteAllProperties",
            $"{acl.NtAuth} | GG_IT | Tier0 | WriteAllProperties",
            $"{acl.SalesPolicyOid} | GG_Sprzedaz | Tier0 | Owner",
            $"{acl.IssuingCa} | GG_Ksiegowosc | Tier0 | GenericAll",
            $"{acl.UserTemplate} | S-1-5-11 | Tier0 | WriteTemplateProperty:msPKI-Enrollment-Flag",
            $"{acl.IssuingCaComputer} | GG_IT | Tier0 | GenericAll",
            $"{acl.Administrator} | GG_IT | Tier0 | WriteAltSecurityIdentities",
        ]), rendered);
    }

    [Fact]
    public async Task FromAChildDomainWindow_TheRootsDomainAdmins_AndCertPublishers_AreStillProvisioning()
    {
        var scenario = Scenario.Create();
        var acl = scenario.Acl;
        var options = new PrivilegedAccessOptions { DomainSid = ChildDomain, RootDomainSid = scenario.Builder.DomainSid };

        var grants = await scenario.CollectAsync(options);

        // Only the configuration partition is shared by the forest: the domain part of the fake is not a child domain.
        var configuration = DistinguishedName.Parse(scenario.Builder.ConfigurationDn);
        Assert.Equal(Sorted(
        [
            $"{acl.PublicKeyServices} | GG_Sprzedaz | Tier0 | WriteAllProperties",
            $"{acl.NtAuth} | GG_IT | Tier0 | WriteAllProperties",
            $"{acl.SalesPolicyOid} | GG_Sprzedaz | Tier0 | Owner",
            $"{acl.IssuingCa} | GG_Ksiegowosc | Tier0 | GenericAll",
            $"{acl.UserTemplate} | S-1-5-11 | Tier0 | WriteTemplateProperty:msPKI-Enrollment-Flag",
        ]), Render(scenario, [.. grants.Where(g => DistinguishedName.Parse(g.ScopeDn!).IsDescendantOf(configuration))]));
    }

    [Fact]
    public async Task ACaHostOfAChildDomain_IsNotResolved_ToAComputerOfTheWindowsDomain()
    {
        // srv-ca03.oddzial.contoso.test lives in a child domain; SRV-CA03 of contoso.test is another computer of the same name.
        var scenario = Scenario.Create();
        var builder = scenario.Builder;
        var lookalike = builder.AddComputer("SRV-CA03", scenario.Company.ComputersOu);
        var lookalikeSid = builder.SidOf(lookalike);
        builder.SetSecurityDescriptor(lookalike, scenario.DomainAdmins, FakeAce.FullControl(scenario.DomainAdmins), FakeAce.FullControl(builder.SidOf(scenario.Company.GroupSales)));
        var service = builder.Session.Seed(
            DistinguishedName.Parse(builder.EnrollmentServicesDn).Child("CN", "Kontoso Oddział CA").ToString(),
            new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", "pKIEnrollmentService" },
                [Attr.DnsHostName] = "srv-ca03.oddzial.contoso.test",
                [Attr.NtSecurityDescriptor] = FakeSecurityDescriptor.Build(scenario.DomainAdmins, scenario.DomainAdmins,
                    FakeAce.FullControl(scenario.DomainAdmins), FakeAce.FullControl(lookalikeSid)),
            });

        var rendered = Render(scenario, await scenario.CollectAsync());

        Assert.Contains($"{service} | {lookalikeSid} | Tier0 | GenericAll", rendered);
        Assert.DoesNotContain(rendered, r => r.StartsWith(lookalike + " |", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Owners_AreReported_OnlyForContainersTemplatesEnrollmentServicesNtAuthAndLinkedOids()
    {
        var scenario = Scenario.Create();
        var builder = scenario.Builder;
        var pki = scenario.Acl.Pki;
        var sales = builder.SidOf(scenario.Company.GroupSales);
        var unlinked = builder.Session.Seed(
            DistinguishedName.Parse(pki.OidContainer).Child("CN", "601.2A3B4C5D6E7F8091A2B3C4D5E6F70812").ToString(),
            new Dictionary<string, object?>
            {
                [Attr.ObjectClass] = new[] { "top", "msPKI-Enterprise-Oid" },
                [Attr.DisplayName] = "Polityka bez grupy",
                ["msPKI-Cert-Template-OID"] = PkiSamples.ForestOid + ".601",
            });
        var cdpHost = DistinguishedName.Parse(pki.CdpContainer).Child("CN", PkiSamples.IssuingCaComputer).ToString();
        string[] reported = [pki.Template(PublicKeyTemplates.Machine), pki.RootCa, pki.KraContainer, cdpHost];
        string[] quiet = [pki.RootAia, pki.RootStore, pki.IssuingCdp, pki.KraAgent, unlinked];
        foreach (var dn in reported.Concat(quiet))
        {
            builder.SetSecurityDescriptor(dn, sales, OwnerOnly(scenario));
        }

        var rendered = Render(scenario, await scenario.CollectAsync());

        foreach (var dn in reported)
        {
            Assert.Contains($"{dn} | GG_Sprzedaz | Tier0 | Owner", rendered);
        }

        Assert.DoesNotContain(rendered, r => quiet.Any(dn => r.StartsWith(dn + " |", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TemplateWrites_NameTheSensitiveAttribute_UnknownGuidsCount_EnrollRightsDoNot()
    {
        var scenario = Scenario.Create();
        var builder = scenario.Builder;
        var machine = scenario.Acl.Pki.Template(PublicKeyTemplates.Machine);
        var it = builder.SidOf(scenario.Company.GroupIt);
        var sales = builder.SidOf(scenario.Company.GroupSales);
        var accounting = builder.SidOf(scenario.Company.GroupAccounting);
        var unknown = new Guid("4f2e6d1c-8b7a-4c3d-9e0f-a1b2c3d4e5f6");
        var displayName = builder.Session.Schema.FindAttribute(Attr.DisplayName)!.SchemaIdGuid;
        builder.SetSecurityDescriptor(machine, scenario.DomainAdmins,
        [
            .. OwnerOnly(scenario),
            new FakeAce(FakeAce.AccessAllowedObject, 0, 0x20, it, displayName),                                  // another attribute: not U4b
            new FakeAce(FakeAce.AccessAllowedObject, 0, 0x20, it, unknown),                                      // nothing names it: sensitive
            new FakeAce(FakeAce.AccessAllowed, 0, 0x100, sales),                                                 // every extended right = enroll
            new FakeAce(FakeAce.AccessAllowedObject, 0, 0x20, accounting, FakeDirectoryBuilder.AclGuids.PkiCertificateTemplate), // the class: all properties
            new FakeAce(FakeAce.AccessAllowedObject, 0, 0x20, sales, new Guid("ea1dddc4-60ff-416e-8cc0-17cee534bce7")), // the real name-flag GUID
        ]);

        var rendered = Render(scenario, await scenario.CollectAsync());

        Assert.Equal(Sorted(
        [
            $"{machine} | GG_IT | Tier0 | WriteTemplateProperty:{unknown:D}",
            $"{machine} | GG_Ksiegowosc | Tier0 | WriteAllProperties",
            $"{machine} | GG_Sprzedaz | Tier0 | WriteTemplateProperty:msPKI-Certificate-Name-Flag",
        ]), [.. rendered.Where(r => r.StartsWith(machine + " |", StringComparison.Ordinal))]);
    }

    [Fact]
    public async Task WriteAltSecurityIdentities_IsReportedOnAccounts_AlsoThroughPublicInformation()
    {
        var scenario = Scenario.Create();
        var builder = scenario.Builder;
        var company = scenario.Company;
        var publicInformation = new Guid("e48d0154-bcf8-11d1-8702-00c04fb96050");
        builder.SetSecurityDescriptor(company.LukaszWisniewski, scenario.DomainAdmins,
        [
            FakeAce.FullControl(scenario.DomainAdmins),
            new FakeAce(FakeAce.AccessAllowedObject, 0, 0x20, builder.SidOf(company.GroupSales), publicInformation),
        ]);

        var rendered = Render(scenario, await scenario.CollectAsync(scenario.Options() with { DeepAclScan = true }));

        Assert.Contains($"{scenario.Acl.Administrator} | GG_IT | Tier0 | WriteAltSecurityIdentities", rendered);
        Assert.Contains($"{company.LukaszWisniewski} | GG_Sprzedaz | Elevated | WriteAltSecurityIdentities", rendered);
    }

    [Fact]
    public async Task CollectPublicKey_ReturnsTypedGrants_OnEveryKindOfTarget()
    {
        var scenario = Scenario.Create();
        var acl = scenario.Acl;
        var builder = scenario.Builder;

        var grants = await scenario.CollectPublicKeyAsync([acl.Pki.RootCaComputer, acl.IssuingCaComputer]);

        Assert.Equal(Sorted(
        [
            Typed(acl.PublicKeyServices, PkiAclTargetKind.Container, builder.SidOf(scenario.Company.GroupSales), AclRight.WriteAllProperties, null),
            Typed(acl.NtAuth, PkiAclTargetKind.NtAuth, builder.SidOf(scenario.Company.GroupIt), AclRight.WriteAllProperties, null),
            Typed(acl.SalesPolicyOid, PkiAclTargetKind.Oid, builder.SidOf(scenario.Company.GroupSales), AclRight.Owner, null),
            Typed(acl.IssuingCa, PkiAclTargetKind.EnrollmentService, builder.SidOf(scenario.Company.GroupAccounting), AclRight.GenericAll, null),
            Typed(acl.UserTemplate, PkiAclTargetKind.Template, "S-1-5-11", AclRight.WriteTemplateProperty, PkiAttributes.EnrollmentFlag),
            Typed(acl.IssuingCaComputer, PkiAclTargetKind.CaComputer, builder.SidOf(scenario.Company.GroupIt), AclRight.GenericAll, null),
            Typed(acl.Administrator, PkiAclTargetKind.Tier0Account, builder.SidOf(scenario.Company.GroupIt), AclRight.WriteAltSecurityIdentities, null),
        ]), Sorted(grants.Select(g => Typed(g.TargetDn, g.TargetKind, g.TrusteeSid, g.Right, g.Detail))));
        Assert.All(grants, g => Assert.Equal($"{nameof(PkiAclGrant)}({g.TargetKind} {g.Right})", g.ToString()));
    }

    [Fact]
    public async Task CollectPublicKey_ReadsThePkiSubtree_TheCaComputers_AndTheTier0Accounts_NotTheDomainScan()
    {
        var scenario = Scenario.Create();
        var builder = scenario.Builder;
        var otherDomainComputer = "CN=SRV-CA09,OU=Serwery,DC=oddzial,DC=contoso,DC=test";
        builder.Session.ClearOperations();

        await scenario.CollectPublicKeyAsync([scenario.Acl.Pki.RootCaComputer, scenario.Acl.IssuingCaComputer, otherDomainComputer]);

        var operations = builder.Session.Operations;
        var searches = operations.Where(o => o.Kind == FakeOperationKind.Search).Select(o => o.Search!).ToList();
        var pkiBase = scenario.Acl.PublicKeyServices;
        Assert.Single(searches, s => DistinguishedName.AreEqual(s.BaseDn, pkiBase) && s.Scope == SearchScope.Subtree && s.ReadSecurityDescriptor);

        // In the domain: the group graph (read by the test's context) and one search of the Tier 0 accounts – no OUs, GPOs,
        // resource-based delegation or deep scan.
        var domainSearches = searches.Where(s => DistinguishedName.AreEqual(s.BaseDn, builder.DomainDn) && s.Filter != GroupGraph.Filter).ToList();
        var tier0 = Assert.Single(domainSearches);
        Assert.StartsWith("(&" + PrivilegedAccessScanner.AccountFilter, tier0.Filter, StringComparison.Ordinal);
        Assert.Contains(LdapFilter.Equal(Attr.MemberOf, builder.DomainAdminsDn), tier0.Filter, StringComparison.Ordinal);
        Assert.DoesNotContain(searches, s => s.Filter.Contains(Attr.AllowedToActOnBehalfOfOtherIdentity, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(searches, s => s.Filter.Contains("groupPolicyContainer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(searches, s => s.Filter.Contains("organizationalUnit", StringComparison.OrdinalIgnoreCase));

        // The CA computers by DN (only those of the window's domain) and AdminSDHolder.
        string[] gets = [.. operations.Where(o => o.Kind == FakeOperationKind.Get).Select(o => o.DistinguishedName)];
        Assert.Contains(scenario.Acl.Pki.RootCaComputer, gets);
        Assert.Contains(scenario.Acl.IssuingCaComputer, gets);
        Assert.DoesNotContain(otherDomainComputer, gets);
        Assert.Contains(gets, g => g.StartsWith("CN=AdminSDHolder,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CollectPublicKey_TheTier0Accounts_IncludeMembersOfTier0Groups_AndAdminSdHolder()
    {
        var scenario = Scenario.Create();
        var builder = scenario.Builder;
        var company = scenario.Company;
        var sales = builder.SidOf(company.GroupSales);
        var altSecurity = builder.Session.Schema.FindAttribute(AclObjectTypes.AltSecurityIdentities)!.SchemaIdGuid;
        var write = new FakeAce(FakeAce.AccessAllowedObject, 0, 0x20, sales, altSecurity);
        builder.AddMembers(builder.DomainAdminsDn, company.JanKowalski);
        builder.SetSecurityDescriptor(company.JanKowalski, scenario.DomainAdmins, FakeAce.FullControl(scenario.DomainAdmins), write);
        builder.SetSecurityDescriptor(company.LukaszWisniewski, scenario.DomainAdmins, FakeAce.FullControl(scenario.DomainAdmins), write);
        builder.SetSecurityDescriptor(builder.AdminSdHolderDn, scenario.DomainAdmins, FakeAce.FullControl(scenario.DomainAdmins), write);

        var grants = await scenario.CollectPublicKeyAsync([scenario.Acl.IssuingCaComputer]);

        var tier0 = grants.Where(g => g.TrusteeSid == sales && g.TargetKind == PkiAclTargetKind.Tier0Account).Select(g => g.TargetDn).ToList();
        Assert.Contains(company.JanKowalski, tier0);
        Assert.Contains(builder.AdminSdHolderDn, tier0);
        Assert.DoesNotContain(grants, g => g.TargetDn == company.LukaszWisniewski);
        Assert.All(grants.Where(g => g.TrusteeSid == sales && g.TargetKind == PkiAclTargetKind.Tier0Account),
            g => Assert.Equal(AclRight.WriteAltSecurityIdentities, g.Right));
    }

    [Fact]
    public async Task CollectPublicKey_TheTier0Accounts_IncludeAccountsWhosePrimaryGroupIsTier0()
    {
        // primaryGroupID = Domain Admins: the account is a Domain Admin, yet memberOf does not show it.
        var scenario = Scenario.Create();
        var builder = scenario.Builder;
        var sales = builder.SidOf(scenario.Company.GroupSales);
        var altSecurity = builder.Session.Schema.FindAttribute(AclObjectTypes.AltSecurityIdentities)!.SchemaIdGuid;
        var hidden = builder.AddUser("Konrad", "Ukryty", scenario.Company.ItOu, new Dictionary<string, object?> { [Attr.PrimaryGroupId] = WellKnownRids.DomainAdmins });
        builder.SetSecurityDescriptor(hidden, scenario.DomainAdmins, FakeAce.FullControl(scenario.DomainAdmins), new FakeAce(FakeAce.AccessAllowedObject, 0, 0x20, sales, altSecurity));

        var grants = await scenario.CollectPublicKeyAsync([scenario.Acl.IssuingCaComputer]);

        var grant = Assert.Single(grants, g => g.TargetDn == hidden);
        Assert.Equal((PkiAclTargetKind.Tier0Account, sales, AclRight.WriteAltSecurityIdentities), (grant.TargetKind, grant.TrusteeSid, grant.Right));
    }

    [Fact]
    public async Task TheOidContainer_AnMsPkiEnterpriseOidObject_HasItsOwnerReported_AsAContainer()
    {
        // In AD, CN=OID itself is of class msPKI-Enterprise-Oid (the fake seeds a container): its owner controls every OID.
        var scenario = Scenario.Create();
        var builder = scenario.Builder;
        var sales = builder.SidOf(scenario.Company.GroupSales);
        var oid = scenario.Acl.Pki.OidContainer;
        builder.Session.SetValues(oid, Attr.ObjectClass, "top", "msPKI-Enterprise-Oid");
        builder.SetSecurityDescriptor(oid, sales, OwnerOnly(scenario));

        Assert.Contains($"{oid} | GG_Sprzedaz | Tier0 | Owner", Render(scenario, await scenario.CollectAsync()));
        var grant = Assert.Single(await scenario.CollectPublicKeyAsync([scenario.Acl.IssuingCaComputer]), g => g.TargetDn == oid);
        Assert.Equal((PkiAclTargetKind.Container, sales, AclRight.Owner), (grant.TargetKind, grant.TrusteeSid, grant.Right));
    }

    [Fact]
    public async Task WithoutAdCs_ThePkiPass_FindsNothing_AndFailsNothing()
    {
        // The Samba lab: empty PKI containers without descriptors (AddWatchdogInfrastructure), or no PKI at all.
        var builder = new FakeDirectoryBuilder();
        builder.AddSampleCompany();
        builder.AddAclDefinitions();
        var options = new PrivilegedAccessOptions { DomainSid = builder.DomainSid, RootDomainSid = builder.DomainSid };
        var graph = await GroupGraph.ReadAsync(builder.Session, null, 500, Ct);
        var context = new PrivilegeScanContext { Session = builder.Session, Groups = graph, Options = options, Now = Now };

        Assert.Empty(await new DirectoryAclGrantSource().CollectPublicKeyAsync(context, [], Ct));
        Assert.DoesNotContain(await new DirectoryAclGrantSource().CollectAsync(context, Ct),
            g => DistinguishedName.Parse(g.ScopeDn!).IsDescendantOf(DistinguishedName.Parse(builder.ConfigurationDn)));

        var bare = new FakeDirectoryBuilder();
        bare.AddSampleCompany();
        var bareGraph = await GroupGraph.ReadAsync(bare.Session, null, 500, Ct);
        var bareContext = new PrivilegeScanContext { Session = bare.Session, Groups = bareGraph, Options = options, Now = Now };
        Assert.Empty(await new DirectoryAclGrantSource().CollectPublicKeyAsync(bareContext, [], Ct));
    }

    private static FakeAce[] OwnerOnly(Scenario scenario) =>
        [FakeAce.FullControl(scenario.DomainAdmins), FakeAce.FullControl(scenario.Acl.Pki.EnterpriseAdminsSid), FakeAce.FullControl("S-1-5-18")];

    private static string Typed(string dn, PkiAclTargetKind kind, string sid, AclRight right, string? detail) =>
        $"{dn} | {kind} | {sid} | {right} | {detail}";

    private static List<string> Sorted(IEnumerable<string> values) => [.. values.Order(StringComparer.Ordinal)];

    /// <summary>"scope DN | trustee name | level | values", sorted.</summary>
    private static List<string> Render(Scenario scenario, IReadOnlyList<PrivilegeGrant> grants) =>
        [.. grants.Select(g => string.Create(CultureInfo.InvariantCulture,
            $"{g.ScopeDn} | {scenario.NameOf(g.PrincipalSid!)} | {g.Level} | {string.Join(", ", g.Values)}"))
            .Order(StringComparer.Ordinal)];

    private sealed class Scenario
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

        private Scenario()
        {
            Builder = new FakeDirectoryBuilder();
            Company = Builder.AddSampleCompany();
            Acl = Builder.AddPublicKeyAclSample(Company);
            foreach (var dn in new[] { Company.GroupIt, Company.GroupSales, Company.GroupAccounting })
            {
                _names[Builder.SidOf(dn)] = DistinguishedName.Parse(dn).Name;
            }
        }

        public FakeDirectoryBuilder Builder { get; }

        public SampleCompany Company { get; }

        public PublicKeyAclSample Acl { get; }

        public string DomainAdmins => $"{Builder.DomainSid}-{WellKnownRids.DomainAdmins}";

        public static Scenario Create() => new();

        public PrivilegedAccessOptions Options() => new() { DomainSid = Builder.DomainSid, RootDomainSid = Builder.DomainSid };

        public string NameOf(string sid) => _names.GetValueOrDefault(sid, sid);

        public async Task<IReadOnlyList<PrivilegeGrant>> CollectAsync(PrivilegedAccessOptions? options = null) =>
            await new DirectoryAclGrantSource().CollectAsync(await ContextAsync(options), Ct);

        public async Task<IReadOnlyList<PkiAclGrant>> CollectPublicKeyAsync(IReadOnlyList<string> caComputerDns) =>
            await new DirectoryAclGrantSource().CollectPublicKeyAsync(await ContextAsync(null), caComputerDns, Ct);

        private async Task<PrivilegeScanContext> ContextAsync(PrivilegedAccessOptions? options)
        {
            options ??= Options();
            var graph = await GroupGraph.ReadAsync(Builder.Session, null, options.PageSize, Ct);
            return new PrivilegeScanContext { Session = Builder.Session, Groups = graph, Options = options, Now = Now };
        }
    }
}
