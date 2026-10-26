using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.PrivilegedAccess;
using AdMng.Core.Schema;
using AdMng.Testing;

namespace AdMng.Core.Tests.PrivilegedAccess;

/// <summary>
/// The ACL delegations source (README U4b) on real binary security descriptors of the fake DC: the Polish sample company
/// with <see cref="FakeDirectoryBuilder.AddAclDelegationSample"/> - defaults of the schema and of the default trustees left
/// out, inherited ACEs reported once at their source, object types resolved from the schema, levels as in the design note.
/// </summary>
public class DirectoryAclGrantSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Collect_ReportsEveryDelegationBeyondTheDefaults_OnceAtItsSource()
    {
        var scenario = Scenario.Create();
        var grants = await CollectAsync(scenario);

        Assert.Equal(Sorted(
        [
            $"{scenario.Builder.DomainDn} | Usługa SQL | Tier0 | ReplicationGetChangesAll, ReplicationGetChanges",
            $"{scenario.Company.CompanyOu} | GG_Helpdesk | High | ResetPassword:user",
            $"{scenario.Company.CompanyOu} | GG_Wsparcie_IT | High | WriteMember:group",
            $"{scenario.Company.CompanyOu} | Jolanta Ćwik | Elevated | CreateChild:computer, DeleteChild:computer",
            $"{scenario.Company.ItOu} | Ewa Błaszczyk | High | GenericAll",
            $"{scenario.Privileged.ServersOu} | Ignacy Wróblewski | High | Owner",
            $"{scenario.Privileged.ServersOu} | Usługa WWW | High | WriteKeyCredentialLink:computer",
            $"{scenario.Builder.DomainControllersDn} | Bożena Gałązka | Tier0 | GenericAll:computer",
            $"{scenario.Builder.DomainAdminsDn} | Stanisław Łęcki | Tier0 | WriteMember",
            $"{scenario.Acl.AdminSdHolder} | Wojciech Ząbek | Tier0 | WriteDacl",
            $"{scenario.Acl.Policies} | Halina Źrebiec | High | GenericAll",
            $"{scenario.Acl.DefaultDomainPolicy} | GG_ERP_Administratorzy | Tier0 | WriteAllProperties",
            $"{scenario.Acl.DrivesPolicy} | Dorota Łukasiewicz | High | Owner",
            $"{scenario.Acl.DrivesPolicy} | GG_Serwis_RDP | High | WriteGpoProperty:displayName",
            $"{scenario.Privileged.AppServer} | Usługa WWW | High | ActOnBehalfOfOtherIdentity",
        ]), Render(scenario, grants));

        Assert.All(grants, g =>
        {
            Assert.Equal(PrivilegeSource.DirectoryAcl, g.Source);
            Assert.Equal(PrivilegeFindingKind.AclRight, g.Kind);
            Assert.Equal(PrivilegeScopeKind.Container, g.Scope);
        });
        Assert.Equal("Default Domain Policy", grants.Single(g => g.ScopeDn == scenario.Acl.DefaultDomainPolicy).ScopeName);
        Assert.Null(grants.Single(g => g.ScopeDn == scenario.Company.ItOu).ScopeName);

        // Inherited copies below OU=Firma, the class defaults (Account/Print Operators on OUs, Authenticated Users), the
        // default trustees (SYSTEM, Administrators, Domain/Enterprise Admins, DCs, Key Admins, CREATOR OWNER, Group Policy
        // Creator Owners on CN=Policies) and the deny ACE are not reported.
        foreach (var quiet in new[]
        {
            scenario.Company.EmployeesOu, scenario.Company.AccountingOu, scenario.Company.SalesOu, scenario.Company.GroupsOu,
            scenario.Builder.UsersDn, scenario.Builder.ComputersDn, scenario.Builder.BuiltinDn, scenario.Builder.KrbtgtDn,
            scenario.Builder.DefaultDomainControllersPolicyDn,
        })
        {
            Assert.DoesNotContain(grants, g => g.ScopeDn == quiet);
        }

        Assert.DoesNotContain(grants, g => g.PrincipalSid == scenario.Builder.SidOf(scenario.Company.PawelLewandowski));
    }

    [Fact]
    public async Task DeepScan_AddsTheExplicitAcesOfSingleAccounts_ButNotTheCreatorsDefaults()
    {
        var scenario = Scenario.Create();
        var grants = await CollectAsync(scenario, deep: true);
        var rendered = Render(scenario, grants);

        // Jan Kowalski is a Domain Admin: resetting his password is Tier 0; Łukasz is an ordinary user (Elevated).
        Assert.Contains($"{scenario.Company.JanKowalski} | Krystyna Ślusarczyk | Tier0 | ResetPassword", rendered);
        Assert.Contains($"{scenario.Company.LukaszWisniewski} | Halina Źrebiec | Elevated | GenericAll", rendered);
        Assert.Equal(17, grants.Count);

        // SRV-APP01 was joined by Paweł: owner and CREATOR OWNER defaults of the computer class, not a delegation.
        Assert.DoesNotContain(grants, g => g.PrincipalSid == scenario.Builder.SidOf(scenario.Company.PawelLewandowski));
        Assert.Single(grants, g => g.ScopeDn == scenario.Privileged.AppServer);
    }

    [Fact]
    public async Task Scanner_ExpandsTheTrusteeGroups_ToTheirMembers_WithThePath()
    {
        var scenario = Scenario.Create();
        var scanner = new PrivilegedAccessScanner(scenario.Builder.Session, scenario.Options(), [new DirectoryAclGrantSource()]);
        var evaluator = await scanner.PrepareAsync(Now, Ct);
        var results = new Dictionary<string, AccountPrivileges>(StringComparer.OrdinalIgnoreCase);
        await foreach (var account in scanner.ScanAsync(evaluator, scenario.Builder.DomainDn, SearchScope.Subtree, null, null, Ct))
        {
            results[account.Account.Name] = account;
        }

        // Henryk: reset password through GG_Helpdesk, member write through GG_Helpdesk → GG_Wsparcie_IT.
        var henryk = results["Henryk Żuławski"].Findings.Where(f => f.Source == PrivilegeSource.DirectoryAcl).ToList();
        var reset = Assert.Single(henryk, f => f.Values.Contains("ResetPassword:user"));
        Assert.Equal(["Henryk Żuławski", "GG_Helpdesk"], reset.Path.Select(s => s.Name));
        Assert.Equal(scenario.Company.CompanyOu, reset.ScopeDn);
        Assert.Equal(PrivilegeLevel.High, reset.Level);
        Assert.Equal(scenario.Privileged.Helpdesk, reset.DirectGroupDn);
        var members = Assert.Single(henryk, f => f.Values.Contains("WriteMember:group"));
        Assert.Equal(["Henryk Żuławski", "GG_Helpdesk", "GG_Wsparcie_IT"], members.Path.Select(s => s.Name));

        // Ewa: the domain's default policy through GG_ERP_Administratorzy (Tier 0), full control of OU=IT herself.
        var ewa = results["Ewa Błaszczyk"].Findings.Where(f => f.Source == PrivilegeSource.DirectoryAcl).ToList();
        var gpo = Assert.Single(ewa, f => f.ScopeDn == scenario.Acl.DefaultDomainPolicy);
        Assert.Equal(PrivilegeLevel.Tier0, gpo.Level);
        Assert.Equal("Default Domain Policy", gpo.ScopeName);
        Assert.Equal(["Ewa Błaszczyk", "GG_ERP_Administratorzy"], gpo.Path.Select(s => s.Name));
        var own = Assert.Single(ewa, f => f.ScopeDn == scenario.Company.ItOu);
        Assert.Equal(["Ewa Błaszczyk"], own.Path.Select(s => s.Name));
        Assert.Equal(PrivilegeFindingKind.AclRight, own.Kind);

        // DCSync makes the SQL service account Tier 0; Halina gets the right of CN=System at CN=Policies.
        Assert.Equal(PrivilegeLevel.Tier0, results["Usługa SQL"].Level);
        Assert.Contains(results["Usługa SQL"].Findings, f => f.Source == PrivilegeSource.DirectoryAcl && f.Values.Contains("ReplicationGetChangesAll"));
        Assert.Equal(PrivilegeLevel.High, results["Halina Źrebiec"].Level);
        Assert.Equal(scenario.Acl.Policies, Assert.Single(results["Halina Źrebiec"].Findings).ScopeDn);

        // Bożena controls the domain controllers' computer objects: Tier 0.
        Assert.Equal(PrivilegeLevel.Tier0, results["Bożena Gałązka"].Level);
    }

    [Fact]
    public async Task AuthenticatedUsers_NotNestedAnywhere_GiveTheRightToEveryAccount()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        builder.AddAclDefinitions();
        var da = $"{builder.DomainSid}-{WellKnownRids.DomainAdmins}";
        builder.SetSecurityDescriptor(company.ComputersOu, da,
            FakeAce.FullControl(da),
            new FakeAce(FakeAce.AccessAllowedObject, 0x02, 0x1, "S-1-5-11", FakeDirectoryBuilder.AclGuids.Computer));
        var options = new PrivilegedAccessOptions { DomainSid = builder.DomainSid, RootDomainSid = builder.DomainSid };
        var scanner = new PrivilegedAccessScanner(builder.Session, options, [new DirectoryAclGrantSource()]);
        var evaluator = await scanner.PrepareAsync(Now, Ct);

        var accounts = new List<AccountPrivileges>();
        await foreach (var account in scanner.ScanAsync(evaluator, company.CompanyOu, SearchScope.Subtree, null, null, Ct))
        {
            accounts.Add(account);
        }

        Assert.NotEmpty(accounts);
        Assert.All(accounts, a =>
        {
            var finding = Assert.Single(a.Findings);
            Assert.Equal(PrivilegeLevel.Elevated, finding.Level);
            Assert.Equal(["CreateChild:computer"], finding.Values);
            Assert.Equal(PrivilegePathStepKind.ImplicitGroup, finding.Path[1].Kind);
            Assert.Equal("S-1-5-11", finding.Path[1].Name);
            Assert.Null(finding.DirectGroupDn);
        });
    }

    [Fact]
    public async Task Laps_IsResolvedFromTheSchema_AndHighOnlyWhereServersAre()
    {
        var admPwd = new Guid("2d4f1a77-0b6c-4c43-9d42-6f7e8c1a9b10");
        var fake = FakeSchema.Create();
        var schema = new SchemaCatalog(
            [.. fake.Attributes, new AttributeDefinition { LdapName = "ms-Mcs-AdmPwd", Syntax = AttributeSyntax.CaseIgnoreString, IsConfidential = true, SchemaIdGuid = admPwd }],
            fake.Classes);
        var builder = new FakeDirectoryBuilder(schema: schema);
        var company = builder.AddSampleCompany();
        var privileged = builder.AddPrivilegedAccessSample(company);
        builder.AddAclDefinitions();
        var da = $"{builder.DomainSid}-{WellKnownRids.DomainAdmins}";
        builder.SetSecurityDescriptor(privileged.ServersOu, da, FakeAce.FullControl(da),
            new FakeAce(FakeAce.AccessAllowedObject, 0x0A, 0x110, builder.SidOf(privileged.Helpdesk), admPwd, FakeDirectoryBuilder.AclGuids.Computer));
        builder.SetSecurityDescriptor(company.ComputersOu, da, FakeAce.FullControl(da),
            new FakeAce(FakeAce.AccessAllowedObject, 0x0A, 0x110, builder.SidOf(privileged.JolantaCwik), admPwd, FakeDirectoryBuilder.AclGuids.Computer));
        var graph = await GroupGraph.ReadAsync(builder.Session, null, 500, Ct);
        var options = new PrivilegedAccessOptions { DomainSid = builder.DomainSid, RootDomainSid = builder.DomainSid };

        var grants = await new DirectoryAclGrantSource().CollectAsync(new PrivilegeScanContext { Session = builder.Session, Groups = graph, Options = options, Now = Now }, Ct);

        var servers = Assert.Single(grants, g => g.ScopeDn == privileged.ServersOu);
        Assert.Equal(PrivilegeLevel.High, servers.Level);
        Assert.Equal(["ReadLapsPassword:ms-Mcs-AdmPwd"], servers.Values);
        var workstations = Assert.Single(grants, g => g.ScopeDn == company.ComputersOu);
        Assert.Equal(PrivilegeLevel.Elevated, workstations.Level);
    }

    [Fact]
    public async Task WithoutTheSchemaAndConfiguration_TheFixedGuidsStillWork_AndNothingFails()
    {
        // No AddAclDefinitions: no classSchema objects, no CN=Extended-Rights - only the default trustees are left out.
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var privileged = builder.AddPrivilegedAccessSample(company);
        var da = $"{builder.DomainSid}-{WellKnownRids.DomainAdmins}";
        builder.Session.SetValues(builder.DomainDn, Attr.NtSecurityDescriptor, FakeSecurityDescriptor.Build(da, da,
            FakeAce.FullControl(da), FakeAce.ExtendedRight(builder.SidOf(privileged.SqlService), FakeDirectoryBuilder.AclGuids.GetChangesAll)));
        var graph = await GroupGraph.ReadAsync(builder.Session, null, 500, Ct);
        var options = new PrivilegedAccessOptions { DomainSid = builder.DomainSid, RootDomainSid = builder.DomainSid };

        var grants = await new DirectoryAclGrantSource().CollectAsync(new PrivilegeScanContext { Session = builder.Session, Groups = graph, Options = options, Now = Now }, Ct);

        var dcsync = Assert.Single(grants);
        Assert.Equal(PrivilegeLevel.Tier0, dcsync.Level);
        Assert.Equal(["ReplicationGetChangesAll"], dcsync.Values);
    }

    [Theory]
    [InlineData("ResetPassword:user", AclRight.ResetPassword, "user")]
    [InlineData("GenericAll", AclRight.GenericAll, null)]
    [InlineData("ReadLapsPassword:msLAPS-Password", AclRight.ReadLapsPassword, "msLAPS-Password")]
    public void AclRightValues_RoundTrip(string value, AclRight right, string? detail)
    {
        Assert.Equal(value, AclRights.Format(right, detail));
        Assert.True(AclRights.TryParse(value, out var parsed, out var parsedDetail));
        Assert.Equal(right, parsed);
        Assert.Equal(detail, parsedDetail);
    }

    [Theory]
    [InlineData("MSSQLSvc/sql01.contoso.test")]
    [InlineData("S-1-5-21-1-2-3-1180")]
    [InlineData("5")]
    [InlineData("")]
    public void OtherValues_AreNoAclRights(string value) => Assert.False(AclRights.TryParse(value, out _, out _));

    private static async Task<IReadOnlyList<PrivilegeGrant>> CollectAsync(Scenario scenario, bool deep = false)
    {
        var options = scenario.Options() with { DeepAclScan = deep };
        var graph = await GroupGraph.ReadAsync(scenario.Builder.Session, null, options.PageSize, Ct);
        return await new DirectoryAclGrantSource().CollectAsync(
            new PrivilegeScanContext { Session = scenario.Builder.Session, Groups = graph, Options = options, Now = Now }, Ct);
    }

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
            Privileged = Builder.AddPrivilegedAccessSample(Company);
            Acl = Builder.AddAclDelegationSample(Company, Privileged);
            foreach (var dn in new[]
            {
                Privileged.SqlService, Privileged.WebService, Privileged.Helpdesk, Privileged.Support, Privileged.JolantaCwik, Privileged.EwaBlaszczyk,
                Privileged.IgnacyWroblewski, Privileged.BozenaGalazka, Privileged.StanislawLecki, Privileged.WojciechZabek, Privileged.HalinaZrebiec,
                Privileged.ErpAdmins, Privileged.DorotaLukasiewicz, Privileged.RemoteDesktopGroup, Privileged.KrystynaSlusarczyk, Company.PawelLewandowski,
            })
            {
                _names[Builder.SidOf(dn)] = Core.Ldap.DistinguishedName.Parse(dn).Name;
            }
        }

        public FakeDirectoryBuilder Builder { get; }

        public SampleCompany Company { get; }

        public PrivilegedSample Privileged { get; }

        public AclSample Acl { get; }

        public static Scenario Create() => new();

        public PrivilegedAccessOptions Options() => new() { DomainSid = Builder.DomainSid, RootDomainSid = Builder.DomainSid };

        public string NameOf(string sid) => _names.GetValueOrDefault(sid, sid);
    }
}
