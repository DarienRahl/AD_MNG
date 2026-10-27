using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Filters;
using AdMng.Core.PrivilegedAccess;
using AdMng.Testing;

namespace AdMng.Core.Tests.PrivilegedAccess;

/// <summary>
/// The privileged access review (README U4a) end to end on the fake DC: the Polish sample company with
/// <see cref="FakeDirectoryBuilder.AddPrivilegedAccessSample"/> - groups recognised by SID only (Polish names), nested
/// paths, the primary group, a membership cycle, a distribution group without rights, the risky settings and the peers.
/// </summary>
public class PrivilegedAccessScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Scan_FindsEveryPrivilegedAccount_WithTheLevelAndThePath()
    {
        var (builder, company, sample) = Create();
        var results = await ScanAsync(builder);

        Assert.Equal(
        [
            "Administrator", "DC01", "Dorota Łukasiewicz", "Ewa Błaszczyk", "gmsa-kopie", "Henryk Żuławski", "Ignacy Wróblewski",
            "Jan Kowalski", "Jolanta Ćwik", "krbtgt", "Łukasz Wiśniewski", "SRV-APP01", "SRV-WEB01", "Stanisław Łęcki", "Usługa SQL",
            "Usługa WWW", "Wojciech Ząbek",
        ], results.Values.Where(r => r.HasFindings).Select(r => r.Account.Name).Order(StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("pl-PL"), true)));

        // Nested path to Account Operators (Polish name, recognised by S-1-5-32-548).
        var henryk = Finding(results, sample.HenrykZulawski, PrivilegeFindingKind.BuiltInGroup);
        Assert.Equal("AccountOperators", henryk.CatalogId);
        Assert.Equal(PrivilegeLevel.Tier0, henryk.Level);
        Assert.Equal(["Henryk Żuławski", "GG_Helpdesk", "GG_Wsparcie_IT", "Operatorzy kont"], henryk.Path.Select(s => s.Name));
        Assert.Equal(sample.Helpdesk, henryk.DirectGroupDn);
        Assert.Equal(PrivilegeScopeKind.Domain, henryk.Scope);

        // Jan: Domain Admins directly, Administrators through it, Group Policy Creator Owners, adminCount of a protected member.
        var jan = results[Key(company.JanKowalski)];
        Assert.Equal(PrivilegeLevel.Tier0, jan.Level);
        var janGroups = jan.Findings.Where(f => f.Kind == PrivilegeFindingKind.BuiltInGroup).ToDictionary(f => f.CatalogId!, f => f.Path.Select(s => s.Name).ToList());
        Assert.Equal(["Jan Kowalski", "Domain Admins"], janGroups["DomainAdmins"]);
        Assert.Equal(["Jan Kowalski", "Domain Admins", "Administratorzy"], janGroups["Administrators"]);
        Assert.Equal(["Jan Kowalski", "Twórcy-właściciele zasad grupy"], janGroups["GroupPolicyCreatorOwners"]);
        Assert.Contains(jan.Findings, f => f.Kind == PrivilegeFindingKind.AdminCount && f.Level == PrivilegeLevel.Minor);
        Assert.Equal(PrivilegeLevel.Tier0, jan.Findings[0].Level);

        // The primary group: DC01 is a domain controller through primaryGroupID 516; its own delegation is expected.
        var dc = results[Key(builder.DomainControllerDn)];
        Assert.Equal(PrivilegedAccountType.DomainController, dc.Type);
        var dcGroup = Assert.Single(dc.Findings);
        Assert.Equal("DomainControllers", dcGroup.CatalogId);
        Assert.True(dcGroup.IsThroughPrimaryGroup);
        Assert.Null(dcGroup.DirectGroupDn);
        Assert.Equal(PrivilegePathStepKind.PrimaryGroup, dcGroup.Path[1].Kind);

        // A membership cycle ends: Łukasz → GG_Cykl_A → GG_Cykl_B → Event Log Readers (minor).
        var lukasz = Finding(results, company.LukaszWisniewski, PrivilegeFindingKind.BuiltInGroup);
        Assert.Equal(["Łukasz Wiśniewski", "GG_Cykl_A", "GG_Cykl_B", "Czytelnicy dziennika zdarzeń"], lukasz.Path.Select(s => s.Name));
        Assert.Equal(PrivilegeLevel.Minor, lukasz.Level);

        // A distribution group inside Domain Admins grants nothing; Paweł has no finding.
        Assert.False(results[Key(company.PawelLewandowski)].HasFindings);
        Assert.DoesNotContain(sample.Distribution, results[Key(company.PawelLewandowski)].DirectGroupDns);
        Assert.Contains(company.GroupSales, results[Key(company.PawelLewandowski)].DirectGroupDns);

        // The built-in accounts.
        Assert.Contains(results[Key(builder.AdministratorDn)].Findings, f => f.Kind == PrivilegeFindingKind.WellKnownAccount && f.Level == PrivilegeLevel.Tier0);
        var krbtgt = results[Key(builder.KrbtgtDn)];
        Assert.Equal([PrivilegeFindingKind.WellKnownAccount, PrivilegeFindingKind.AdminCount], krbtgt.Findings.Select(f => f.Kind));
        Assert.True(krbtgt.IsDisabled);

        // The gMSA among the backup operators.
        var gmsa = results[Key(sample.Gmsa)];
        Assert.Equal(PrivilegedAccountType.ServiceAccount, gmsa.Type);
        Assert.Equal("BackupOperators", Assert.Single(gmsa.Findings).CatalogId);
    }

    [Fact]
    public async Task Scan_RiskySettings_OfTheAccountsThemselves()
    {
        var (builder, _, sample) = Create();
        var results = await ScanAsync(builder);

        var orphaned = Assert.Single(results[Key(sample.IgnacyWroblewski)].Findings);
        Assert.Equal(PrivilegeFindingKind.OrphanedAdminCount, orphaned.Kind);
        Assert.Equal(PrivilegeLevel.Minor, orphaned.Level);
        Assert.Equal(PrivilegeSource.AccountSettings, orphaned.Source);

        Assert.Equal(PrivilegeFindingKind.PreauthNotRequired, Assert.Single(results[Key(sample.JolantaCwik)].Findings).Kind);

        var history = Assert.Single(results[Key(sample.StanislawLecki)].Findings);
        Assert.Equal(PrivilegeFindingKind.SidHistory, history.Kind);
        Assert.Equal(PrivilegeLevel.Elevated, history.Level);
        Assert.Equal([$"{FakeDirectoryBuilder.OldDomainSid}-1180"], history.Values);

        var sql = results[Key(sample.SqlService)].Findings;
        Assert.Equal([PrivilegeFindingKind.ConstrainedDelegation, PrivilegeFindingKind.Kerberoastable], sql.Select(f => f.Kind));
        Assert.Equal(PrivilegeLevel.High, sql[0].Level);
        Assert.Equal(["cifs/fs01.contoso.test"], sql[0].Values);
        Assert.Equal(["MSSQLSvc/sql01.contoso.test:1433", "MSSQLSvc/sql01.contoso.test"], sql[1].Values);

        // Protocol transition towards the DC's LDAP service is Tier 0.
        var web = Assert.Single(results[Key(sample.WebService)].Findings);
        Assert.Equal(PrivilegeFindingKind.ProtocolTransition, web.Kind);
        Assert.Equal(PrivilegeLevel.Tier0, web.Level);

        var app = Assert.Single(results[Key(sample.AppServer)].Findings);
        Assert.Equal(PrivilegeFindingKind.UnconstrainedDelegation, app.Kind);
        Assert.Equal(PrivilegeLevel.Tier0, app.Level);
        Assert.Equal(PrivilegedAccountType.Computer, results[Key(sample.AppServer)].Type);

        var rbcd = Assert.Single(results[Key(sample.WebServer)].Findings);
        Assert.Equal(PrivilegeFindingKind.ResourceBasedDelegation, rbcd.Kind);
        Assert.Equal(PrivilegeLevel.Elevated, rbcd.Level);

        // SID history with the SID of Domain Admins of this domain is Tier 0.
        builder.Session.SetValues(sample.StanislawLecki, Attr.SidHistory, AdConvert.SidToBytes($"{builder.DomainSid}-512"));
        var again = await ScanAsync(builder);
        Assert.Equal(PrivilegeLevel.Tier0, Assert.Single(again[Key(sample.StanislawLecki)].Findings).Level);
    }

    [Fact]
    public async Task Peers_TheGroupsNoOtherAccountantHolds_AndTheUnusualPrivilegedGroupsOfIt()
    {
        var (builder, company, sample) = Create();
        var results = await ScanAsync(builder);

        var dorota = results[Key(sample.DorotaLukasiewicz)];
        var peer = Assert.Single(dorota.Findings, f => f.Kind == PrivilegeFindingKind.PeerOutlier);
        Assert.Equal(PrivilegeSource.Peers, peer.Source);
        Assert.Equal(PrivilegeLevel.Elevated, peer.Level);
        Assert.Equal(PeerDimension.OrganizationalUnit, peer.Peer!.Dimension);
        Assert.Equal(6, peer.Peer.PeerCount);
        Assert.Equal(company.AccountingOu, peer.ScopeDn);
        Assert.Equal(["GG_Kadry_Płace", "GG_Serwis_RDP"], peer.Peer.Groups.Select(g => g.Name));
        Assert.All(peer.Peer.Groups, g => Assert.Equal(0, g.Holders));
        Assert.Equal([PrivilegeLevel.None, PrivilegeLevel.Elevated], peer.Peer.Groups.Select(g => g.GrantedLevel));
        Assert.True(dorota.IsPeerOutlier);

        // The department gives the same peers: no second finding. The other accountants are not unusual.
        Assert.False(results[Key(sample.BozenaGalazka)].HasFindings);
        Assert.False(results[Key(company.MalgorzataWojcik)].HasFindings);

        // In IT the unusual groups are the privileged ones; a minor one makes a minor finding.
        Assert.Equal(PrivilegeLevel.Elevated, Assert.Single(results[Key(sample.HenrykZulawski)].Findings, f => f.Kind == PrivilegeFindingKind.PeerOutlier).Level);
        var lukasz = Assert.Single(results[Key(company.LukaszWisniewski)].Findings, f => f.Kind == PrivilegeFindingKind.PeerOutlier);
        Assert.Equal(PrivilegeLevel.Minor, lukasz.Level);
        Assert.Equal(["GG_Cykl_A"], lukasz.Values);

        // Thresholds: with at least 8 peers nobody is compared (IT has 7 besides each account); without OU and
        // department nobody either.
        var strict = await ScanAsync(builder, o => o with { Peers = new PeerOptions { MinPeers = 8 } });
        Assert.DoesNotContain(strict.Values, r => r.IsPeerOutlier);
        var none = await ScanAsync(builder, o => o with { Peers = new PeerOptions { ByOrganizationalUnit = false, ByDepartment = false } });
        Assert.DoesNotContain(none.Values, r => r.IsPeerOutlier);
    }

    [Fact]
    public async Task OperatorMarks_LevelOverrides_AndFilterRules()
    {
        var (builder, company, sample) = Create();
        var results = await ScanAsync(builder, o => o with
        {
            LevelOverrides = new Dictionary<string, PrivilegeLevel> { ["EventLogReaders"] = PrivilegeLevel.High },
            Marks =
            [
                new PrivilegeMark(null, sample.Payroll, PrivilegeLevel.High, "Dane płacowe"),
                new PrivilegeMark(builder.Session.GetValues(company.PawelLewandowski, Attr.ObjectSid).Single(), null, PrivilegeLevel.Elevated, "Dyrektor"),
            ],
            Rules =
            [
                new PrivilegeConditionRule("Administratorzy IT", FilterDefinition.Of(new FilterCondition(Attr.EmployeeType, FilterOperator.Equals, "IT-Admin")), PrivilegeLevel.High, PrivilegeRuleTarget.Accounts),
                new PrivilegeConditionRule("Grupy uprzywilejowane", FilterDefinition.Of(new FilterCondition(Attr.Description, FilterOperator.StartsWith, "Uprzywilejowana")), PrivilegeLevel.Elevated, PrivilegeRuleTarget.Groups, "opis grupy"),
                new PrivilegeConditionRule("Zablokowane w IT", FilterDefinition.Of(
                    new FilterCondition(Attr.Department, FilterOperator.Equals, "IT"),
                    new FilterCondition(Attr.UserAccountControlComputed, FilterOperator.BitSet, "16")), PrivilegeLevel.Minor, PrivilegeRuleTarget.Accounts),
                new PrivilegeConditionRule("Zepsuta", FilterDefinition.Raw("(&(x=1)"), PrivilegeLevel.Minor, PrivilegeRuleTarget.Accounts),
            ],
        }, out var scanner);

        Assert.Equal(["Zepsuta"], scanner.SkippedRules);
        Assert.Equal(PrivilegeLevel.High, Finding(results, company.LukaszWisniewski, PrivilegeFindingKind.BuiltInGroup).Level);

        var payroll = Finding(results, sample.DorotaLukasiewicz, PrivilegeFindingKind.MarkedGroup);
        Assert.Equal(PrivilegeSource.OperatorRules, payroll.Source);
        Assert.Equal(PrivilegeLevel.High, payroll.Level);
        Assert.Equal("Dane płacowe", payroll.Note);
        Assert.Equal(["Dorota Łukasiewicz", "GG_Kadry_Płace"], payroll.Path.Select(s => s.Name));

        var pawel = Finding(results, company.PawelLewandowski, PrivilegeFindingKind.MarkedAccount);
        Assert.Equal(PrivilegeLevel.Elevated, pawel.Level);
        Assert.Single(pawel.Path);

        var wojciech = Finding(results, sample.WojciechZabek, PrivilegeFindingKind.ConditionAccount);
        Assert.Equal("Administratorzy IT", wojciech.Note);
        Assert.Equal(PrivilegeLevel.High, wojciech.Level);

        var erp = Finding(results, sample.EwaBlaszczyk, PrivilegeFindingKind.ConditionGroup);
        Assert.Equal("Grupy uprzywilejowane – opis grupy", erp.Note);
        Assert.Equal(["Ewa Błaszczyk", "GG_ERP_Administratorzy"], erp.Path.Select(s => s.Name));

        // The constructed condition is checked on the client: nobody in IT is locked out.
        Assert.DoesNotContain(results.Values.SelectMany(r => r.Findings), f => f.Note == "Zablokowane w IT");
    }

    [Fact]
    public async Task Implicit_AuthenticatedUsersInAPrivilegedGroup_GivesItToEveryAccount_AndOptionsLeaveTypesOut()
    {
        var (builder, company, sample) = Create();
        builder.AddMembers(sample.RemoteDesktopUsers, sample.AuthenticatedUsers);

        var results = await ScanAsync(builder, o => o with { IncludeComputers = false, IncludeServiceAccounts = false });

        var bartlomiej = Finding(results, company.BartlomiejKozlowski, PrivilegeFindingKind.BuiltInGroup);
        Assert.Equal(["Bartłomiej Kozłowski", "S-1-5-11", "Użytkownicy pulpitu zdalnego"], bartlomiej.Path.Select(s => s.Name));
        Assert.Equal(PrivilegePathStepKind.ImplicitGroup, bartlomiej.Path[1].Kind);
        Assert.Null(bartlomiej.DirectGroupDn);
        Assert.DoesNotContain(results.Values, r => r.Type is PrivilegedAccountType.Computer or PrivilegedAccountType.DomainController or PrivilegedAccountType.ServiceAccount);
    }

    [Fact]
    public async Task Queries_OneGroupSearch_OneAccountSearch_NoPerAccountRoundTrips()
    {
        var (builder, company, _) = Create();
        var before = builder.Session.Operations.Count;
        var results = await ScanAsync(builder, scope: (company.CompanyOu, "(department=IT)"));

        var searches = builder.Session.Operations.Skip(before).Where(o => o.Kind == FakeOperationKind.Search).Select(o => o.Search!).ToList();
        Assert.Equal(3, searches.Count);
        Assert.Contains(searches, s => s.Filter == GroupGraph.Filter && s.BaseDn == builder.DomainDn);
        Assert.Contains(searches, s => s.Filter == PrivilegedAccessScanner.DomainControllerFilter);
        var accounts = Assert.Single(searches, s => s.BaseDn == company.CompanyOu);
        Assert.Equal($"(&{PrivilegedAccessScanner.AccountFilter}(department=IT))", accounts.Filter);
        Assert.Equal(SearchScope.Subtree, accounts.Scope);
        Assert.All(results.Values, r => Assert.Equal("IT", r.Account.GetString(Attr.Department)));
        Assert.Equal(8, results.Count);
    }

    private static (FakeDirectoryBuilder Builder, SampleCompany Company, PrivilegedSample Sample) Create()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var sample = builder.AddPrivilegedAccessSample(company);
        return (builder, company, sample);
    }

    private static Task<Dictionary<string, AccountPrivileges>> ScanAsync(
        FakeDirectoryBuilder builder,
        Func<PrivilegedAccessOptions, PrivilegedAccessOptions>? configure = null,
        (string BaseDn, string? Filter)? scope = null) =>
        ScanAsync(builder, configure ?? (o => o), out _, scope);

    private static Task<Dictionary<string, AccountPrivileges>> ScanAsync(
        FakeDirectoryBuilder builder,
        Func<PrivilegedAccessOptions, PrivilegedAccessOptions> configure,
        out PrivilegedAccessScanner scanner,
        (string BaseDn, string? Filter)? scope = null)
    {
        var options = configure(new PrivilegedAccessOptions { DomainSid = builder.DomainSid, RootDomainSid = builder.DomainSid });
        var created = new PrivilegedAccessScanner(builder.Session, options);
        scanner = created;
        return RunAsync(created, builder, scope);
    }

    private static async Task<Dictionary<string, AccountPrivileges>> RunAsync(PrivilegedAccessScanner scanner, FakeDirectoryBuilder builder, (string BaseDn, string? Filter)? scope)
    {
        var evaluator = await scanner.PrepareAsync(Now, Ct);
        var list = new List<AccountPrivileges>();
        await foreach (var account in scanner.ScanAsync(evaluator, scope?.BaseDn ?? builder.DomainDn, SearchScope.Subtree, scope?.Filter, null, Ct))
        {
            list.Add(account);
        }

        var peers = PrivilegedAccessScanner.ComparePeers(list, evaluator);
        return list.ToDictionary(
            a => Key(a.DistinguishedName),
            a => peers.TryGetValue(Key(a.DistinguishedName), out var found) ? a.WithPeerFindings(found) : a);
    }

    private static PrivilegeFinding Finding(Dictionary<string, AccountPrivileges> results, string dn, PrivilegeFindingKind kind) =>
        Assert.Single(results[Key(dn)].Findings, f => f.Kind == kind);

    private static string Key(string dn) => PrivilegedAccessScanner.KeyOf(dn);
}
