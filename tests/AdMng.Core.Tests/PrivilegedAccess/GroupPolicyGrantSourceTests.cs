using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.PrivilegedAccess;
using AdMng.Testing;

namespace AdMng.Core.Tests.PrivilegedAccess;

/// <summary>
/// U4c end to end on the fake DC: the Polish sample company with the privileged sample and
/// <see cref="FakeDirectoryBuilder.AddGroupPolicySample"/> - links (enforced, block inheritance, disabled, link order),
/// Restricted Groups in both forms, User Rights Assignment, GPP with removals and item-level targeting, names resolved to
/// SIDs, levels by the computers reached (Tier 0 on the DCs), and the notices.
/// </summary>
public class GroupPolicyGrantSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Scan_ReportsTheLocalRightsOfTheGpos_WithLevelsScopesAndComputers()
    {
        var (builder, company, sample, gpo, sysvol) = Create();
        var (results, scanner) = await ScanAsync(builder, sysvol);

        // Restricted Groups (Members) on OU=Serwery: Ewa through GG_Administratorzy_Serwerów, High on the two servers.
        var ewa = LocalRights(results, sample.EwaBlaszczyk);
        var admin = Assert.Single(ewa, f => f.GroupPolicy!.Right == LocalRightsCatalog.AdministratorsSid && f.GroupPolicy.Mechanism == LocalRightMechanism.RestrictedGroupMembers);
        Assert.Equal(PrivilegeLevel.High, admin.Level);
        Assert.Equal(PrivilegeSource.GroupPolicy, admin.Source);
        Assert.Equal(PrivilegeScopeKind.Computers, admin.Scope);
        Assert.Equal(sample.ServersOu, admin.ScopeDn);
        Assert.Equal(["Ewa Błaszczyk", "GG_Administratorzy_Serwerów"], admin.Path.Select(s => s.Name));
        Assert.Equal(gpo.ServerAdmins, admin.DirectGroupDn);
        Assert.Equal(["Serwery – Administratorzy lokalni", LocalRightsCatalog.AdministratorsSid], admin.Values);
        Assert.Equal((0, 2, 0), (admin.GroupPolicy!.DomainControllers, admin.GroupPolicy.Servers, admin.GroupPolicy.Workstations));
        Assert.Equal(1, admin.GroupPolicy.LinkOrder);
        Assert.False(admin.GroupPolicy.IsConditional);

        // Link order: the last link of OU=Serwery sets SeDebugPrivilege; the older GPO's GG_Przegrana loses it.
        var debug = Assert.Single(ewa, f => f.GroupPolicy!.Right == "SeDebugPrivilege");
        Assert.Equal(PrivilegeLevel.High, debug.Level);
        Assert.Equal(LocalRightMechanism.UserRightsAssignment, debug.GroupPolicy!.Mechanism);
        Assert.Empty(LocalRights(results, company.GrazynaDabrowska));

        // Restricted Groups (MemberOf): GG_Kopie_Zapasowe in Backup Operators on the servers.
        var backup = Assert.Single(LocalRights(results, sample.StanislawLecki));
        Assert.Equal((LocalRightsCatalog.BackupOperatorsSid, LocalRightMechanism.RestrictedGroupMemberOf, PrivilegeLevel.High),
            (backup.GroupPolicy!.Right, backup.GroupPolicy.Mechanism, backup.Level));

        // The Default Domain Controllers Policy: Tier 0 for a user named by SID, Elevated logon for one named by name;
        // the built-in default holders (Administrators, operators, Enterprise Domain Controllers) give no findings.
        var lukasz = Assert.Single(LocalRights(results, company.LukaszWisniewski));
        Assert.Equal(("SeDebugPrivilege", PrivilegeLevel.Tier0, builder.DomainControllersDn), (lukasz.GroupPolicy!.Right, lukasz.Level, lukasz.ScopeDn));
        Assert.Equal((1, 0, 0), (lukasz.GroupPolicy.DomainControllers, lukasz.GroupPolicy.Servers, lukasz.GroupPolicy.Workstations));
        Assert.Equal(["Łukasz Wiśniewski"], lukasz.Path.Select(s => s.Name));
        var malgorzata = Assert.Single(LocalRights(results, company.MalgorzataWojcik));
        Assert.Equal(("SeInteractiveLogonRight", PrivilegeLevel.Elevated), (malgorzata.GroupPolicy!.Right, malgorzata.Level));
        Assert.Empty(LocalRights(results, company.JanKowalski));

        // A domain link (MemberOf to Remote Desktop Users): the workstations and the DC, not OU=Serwery (blocks inheritance).
        var dorota = LocalRights(results, sample.DorotaLukasiewicz);
        var domainRdp = Assert.Single(dorota, f => f.GroupPolicy!.GpoId == gpo.DomainRemoteDesktop);
        Assert.Equal((1, 0, 3), (domainRdp.GroupPolicy!.DomainControllers, domainRdp.GroupPolicy.Servers, domainRdp.GroupPolicy.Workstations));
        Assert.Equal(PrivilegeLevel.Elevated, domainRdp.Level);
        Assert.Equal(builder.DomainDn, domainRdp.ScopeDn);
        Assert.Equal(["Dorota Łukasiewicz", "GG_Kadry_Płace"], domainRdp.Path.Select(s => s.Name));

        // GPP with item-level targeting, a member given by name only: conditional.
        var targeted = Assert.Single(dorota, f => f.GroupPolicy!.GpoId == gpo.WorkstationSupport);
        Assert.True(targeted.GroupPolicy!.ItemLevelTargeting);
        Assert.Equal(LocalRightsCatalog.RemoteDesktopUsersSid, targeted.GroupPolicy.Right);
        Assert.Equal(LocalRightMechanism.PreferenceGroups, targeted.GroupPolicy.Mechanism);
        Assert.Equal(["Dorota Łukasiewicz", "GG_Serwis_RDP"], targeted.Path.Select(s => s.Name));

        // An enforced domain link reaches OU=Serwery despite the block: Event Log Readers everywhere (minor).
        var zbigniew = Assert.Single(LocalRights(results, company.ZbigniewKazmierczak));
        Assert.Equal((1, 2, 3), (zbigniew.GroupPolicy!.DomainControllers, zbigniew.GroupPolicy.Servers, zbigniew.GroupPolicy.Workstations));
        Assert.Equal(PrivilegeLevel.Minor, zbigniew.Level);
        Assert.True(zbigniew.GroupPolicy.LinkEnforced);

        // GPP on OU=Komputery: Henryk through GG_Wsparcie_Stacji (Elevated on workstations); Bartłomiej added by one GPO
        // and removed by the next one (link order 1) has nothing; the disabled link and the GPO without computer
        // settings grant nothing.
        var henryk = Assert.Single(LocalRights(results, sample.HenrykZulawski));
        Assert.Equal((LocalRightsCatalog.AdministratorsSid, PrivilegeLevel.Elevated, company.ComputersOu),
            (henryk.GroupPolicy!.Right, henryk.Level, henryk.ScopeDn));
        Assert.Empty(LocalRights(results, company.BartlomiejKozlowski));
        Assert.Empty(LocalRights(results, company.PawelLewandowski));
        Assert.DoesNotContain(results.Values.SelectMany(r => r.Findings), f => f.GroupPolicy?.GpoId == gpo.DisabledLink || f.GroupPolicy?.GpoId == gpo.NoComputerSettings);

        // The unknown name is reported; nothing else.
        var notice = Assert.Single(scanner.Notices);
        Assert.Equal(GroupPolicyGrantSource.Notices.UnresolvedPrincipals, notice.Code);
        Assert.Equal(["1", @"CONTOSO\nieistniejący.użytkownik"], notice.Arguments);

        // SYSVOL of the session's DC only, every file read in memory and the connection closed.
        Assert.Equal(["dc01.contoso.test"], sysvol.OpenedOn);
        Assert.Equal(0, sysvol.OpenConnections);
        Assert.DoesNotContain(sysvol.Reads, r => r.StartsWith(SysvolPaths.FolderName(gpo.NoComputerSettings), StringComparison.Ordinal));
    }

    [Fact]
    public async Task OperatorLevels_OfTheBuiltInGroups_ApplyOnTheDomainControllers()
    {
        var (builder, _, sample, _, sysvol) = Create();
        var (results, _) = await ScanAsync(builder, sysvol, o => o with
        {
            LevelOverrides = new Dictionary<string, PrivilegeLevel> { ["RemoteDesktopUsers"] = PrivilegeLevel.High },
        });

        var domainRdp = Assert.Single(LocalRights(results, sample.DorotaLukasiewicz), f => !f.GroupPolicy!.ItemLevelTargeting);
        Assert.Equal(PrivilegeLevel.High, domainRdp.Level);
    }

    [Fact]
    public async Task SysvolUnavailable_IsReported_AndTheRunGoesOn()
    {
        var (builder, company, _, _, sysvol) = Create();
        sysvol.Unavailable = SysvolUnavailableReason.LogonFailure;

        var (results, scanner) = await ScanAsync(builder, sysvol);

        Assert.DoesNotContain(results.Values.SelectMany(r => r.Findings), f => f.Source == PrivilegeSource.GroupPolicy);
        Assert.Contains(results[Key(company.JanKowalski)].Findings, f => f.Kind == PrivilegeFindingKind.BuiltInGroup);
        var notice = Assert.Single(scanner.Notices);
        Assert.Equal("GroupPolicy.SysvolUnavailable.LogonFailure", notice.Code);
        Assert.Equal(["dc01.contoso.test"], notice.Arguments);
    }

    [Fact]
    public async Task UnreadableAndInvalidFiles_AreReported_TheOtherGposStillCount()
    {
        var (builder, company, sample, gpo, sysvol) = Create();
        sysvol.Fail(gpo.DomainControllersPolicy, SecurityTemplate.RelativePath, SysvolFileStatus.AccessDenied);
        sysvol.Put(gpo.ServersPolicy, SecurityTemplate.RelativePath, "[Group Membership]\r\n*S-1-5-32-544__Members = Zażółć"u8.ToArray()[..^1]);
        sysvol.PutGroups(gpo.EnforcedEventLog, "<Groups><Group>");

        var (results, scanner) = await ScanAsync(builder, sysvol);

        Assert.Empty(LocalRights(results, company.LukaszWisniewski));
        Assert.Empty(LocalRights(results, sample.StanislawLecki));
        Assert.Empty(LocalRights(results, company.ZbigniewKazmierczak));
        Assert.Single(LocalRights(results, sample.HenrykZulawski));
        Assert.Equal(
            [
                (GroupPolicyGrantSource.Notices.FilesUnreadable, "1|Default Domain Controllers Policy"),
                (GroupPolicyGrantSource.Notices.FilesInvalid, "2|Serwery – Administratorzy lokalni, Zasady wymuszone – Dziennik zdarzeń"),
                (GroupPolicyGrantSource.Notices.UnresolvedPrincipals, @"1|CONTOSO\nieistniejący.użytkownik"),
            ],
            scanner.Notices.Select(n => (n.Code, string.Join('|', n.Arguments))));
    }

    [Fact]
    public async Task EveryoneAndAuthenticatedUsers_GiveEveryAccountTheRight_EvenWithoutANestingGroup()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var sysvol = new FakeSysvolFileReader();
        var policy = builder.AddGroupPolicyObject("Stacje – Debugowanie dla wszystkich");
        sysvol.PutSecurityTemplate(policy, FakeDirectoryBuilder.Template("[Privilege Rights]", "SeDebugPrivilege = *S-1-5-11", "SeImpersonatePrivilege = Wszyscy"));
        builder.SetGroupPolicyLinks(company.ComputersOu, [(policy, 0)]);

        var (results, scanner) = await ScanAsync(builder, sysvol);

        Assert.Empty(scanner.Notices);
        var malgorzata = LocalRights(results, company.MalgorzataWojcik);
        Assert.Equal(["SeDebugPrivilege", "SeImpersonatePrivilege"], malgorzata.Select(f => f.GroupPolicy!.Right).Order(StringComparer.Ordinal));
        Assert.All(malgorzata, f => Assert.Equal(PrivilegeLevel.High, f.Level));
        Assert.Equal(PrivilegePathStepKind.ImplicitGroup, malgorzata.Single(f => f.GroupPolicy!.Right == "SeDebugPrivilege").Path[1].Kind);
        Assert.Equal(PrivilegedGroupCatalog.EveryoneSid, malgorzata.Single(f => f.GroupPolicy!.Right == "SeImpersonatePrivilege").Path[1].Name);
        Assert.All(results.Values.Where(r => r.Type == PrivilegedAccountType.User), r => Assert.NotEmpty(LocalRights(results, r.DistinguishedName)));
    }

    [Fact]
    public async Task TargetedPreferenceItem_DoesNotDisplaceAnUnconditionalAdd_OfTheSameMember()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var sysvol = new FakeSysvolFileReader();
        var helpdesk = builder.AddGroup("GG_Pomoc_Techniczna", company.GroupsOu, members: [company.MalgorzataWojcik]);
        (string, string?, string)[] member = [(@"CONTOSO\GG_Pomoc_Techniczna", builder.SidOf(helpdesk), "ADD")];

        // The domain GPO adds the group everywhere (and again, targeted, in a second item); the OU GPO only on laptops.
        var everywhere = builder.AddGroupPolicyObject("Domena – Pomoc techniczna");
        sysvol.PutGroups(everywhere, FakeDirectoryBuilder.Groups(
            FakeDirectoryBuilder.LocalGroup("Administrators (built-in)", "S-1-5-32-544", members: member),
            FakeDirectoryBuilder.LocalGroup("Administratorzy (wbudowane)", "S-1-5-32-544", targeted: true, members: member)));
        var laptops = builder.AddGroupPolicyObject("Stacje – Pomoc na laptopach");
        sysvol.PutGroups(laptops, FakeDirectoryBuilder.Groups(
            FakeDirectoryBuilder.LocalGroup("Administrators (built-in)", "S-1-5-32-544", targeted: true, members: member)));
        builder.SetGroupPolicyLinks(builder.DomainDn, [(everywhere, 0)]);
        builder.SetGroupPolicyLinks(company.ComputersOu, [(laptops, 0)]);

        var (results, _) = await ScanAsync(builder, sysvol);

        var findings = LocalRights(results, company.MalgorzataWojcik);
        var unconditional = Assert.Single(findings, f => f.GroupPolicy!.GpoId == everywhere);
        Assert.False(unconditional.GroupPolicy!.IsConditional);
        Assert.Equal((1, 0, 3), (unconditional.GroupPolicy.DomainControllers, unconditional.GroupPolicy.Servers, unconditional.GroupPolicy.Workstations));
        Assert.Equal(PrivilegeLevel.Tier0, unconditional.Level);
        var conditional = Assert.Single(findings, f => f.GroupPolicy!.GpoId == laptops);
        Assert.True(conditional.GroupPolicy!.ItemLevelTargeting);
        Assert.Equal((0, 0, 3), (conditional.GroupPolicy.DomainControllers, conditional.GroupPolicy.Servers, conditional.GroupPolicy.Workstations));
        Assert.Equal(PrivilegeLevel.Elevated, conditional.Level);
    }

    [Fact]
    public async Task RestrictedGroups_OnAPrivilegedDomainGroup_AreReportedForTheDomainControllers()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var sysvol = new FakeSysvolFileReader();
        var domainAdmins = builder.SidOf(builder.DomainAdminsDn);

        // A GPO on OU=Domain Controllers keeps Małgorzata in Domain Admins (MemberOf by SID) and sets its members by name;
        // the same kind of line on OU=Komputery changes nothing (member computers cannot set domain groups).
        var dcPolicy = builder.AddGroupPolicyObject("Kontrolery – Utrzymanie członkostwa");
        sysvol.PutSecurityTemplate(dcPolicy, FakeDirectoryBuilder.Template(
            "[Group Membership]",
            $"*{builder.SidOf(company.MalgorzataWojcik)}__Memberof = *{domainAdmins}",
            $"*{builder.SidOf(company.MalgorzataWojcik)}__Members =",
            $@"CONTOSO\Domain Admins__Members = *{builder.SidOf(company.PawelLewandowski)}"));
        var workstations = builder.AddGroupPolicyObject("Stacje – Administratorzy domeny");
        sysvol.PutSecurityTemplate(workstations, FakeDirectoryBuilder.Template(
            "[Group Membership]",
            $"*{builder.SidOf(company.BartlomiejKozlowski)}__Memberof = *{domainAdmins}"));
        builder.SetGroupPolicyLinks(builder.DomainControllersDn, [(dcPolicy, 0)]);
        builder.SetGroupPolicyLinks(company.ComputersOu, [(workstations, 0)]);

        var (results, scanner) = await ScanAsync(builder, sysvol);

        Assert.Empty(scanner.Notices);
        var malgorzata = Assert.Single(LocalRights(results, company.MalgorzataWojcik));
        Assert.Equal(
            (LocalRightKind.DomainGroup, "DomainAdmins", domainAdmins, LocalRightMechanism.RestrictedGroupMemberOf, PrivilegeLevel.Tier0),
            (malgorzata.GroupPolicy!.Definition.Kind, malgorzata.GroupPolicy.Definition.Id, malgorzata.GroupPolicy.Right, malgorzata.GroupPolicy.Mechanism, malgorzata.Level));
        Assert.Equal((1, 0, 0), (malgorzata.GroupPolicy.DomainControllers, malgorzata.GroupPolicy.Servers, malgorzata.GroupPolicy.Workstations));
        Assert.Equal(builder.DomainControllersDn, malgorzata.ScopeDn);
        var pawel = Assert.Single(LocalRights(results, company.PawelLewandowski));
        Assert.Equal(("DomainAdmins", LocalRightMechanism.RestrictedGroupMembers, PrivilegeLevel.Tier0),
            (pawel.GroupPolicy!.Definition.Id, pawel.GroupPolicy.Mechanism, pawel.Level));
        Assert.Empty(LocalRights(results, company.BartlomiejKozlowski));
    }

    [Fact]
    public async Task SecurityFiltering_LimitsAGpo_ToTheComputersAllowedToApplyIt()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var sample = builder.AddPrivilegedAccessSample(company);
        var sysvol = new FakeSysvolFileReader();
        var sqlServers = builder.AddGroup("Serwery SQL", company.GroupsOu, members: [sample.AppServer]);
        var sqlAdmins = builder.AddGroup("GG_SQL_Admini", company.GroupsOu, members: [company.PawelLewandowski]);
        var read = new FakeAce(FakeAce.AccessAllowed, 0, 0x20094, PrivilegedGroupCatalog.AuthenticatedUsersSid);
        var apply = GroupPolicySecurityFilter.ApplyGroupPolicyRight;
        var domainControllers = builder.SidOf(sample.DomainControllers);

        // Linked at the domain head, filtered to "Serwery SQL" and denied to the DCs: local admins on one SQL server, not Tier 0.
        var sql = builder.AddGroupPolicyObject("Admini SQL");
        sysvol.PutSecurityTemplate(sql, FakeDirectoryBuilder.Template("[Group Membership]", $"*{builder.SidOf(sqlAdmins)}__Memberof = *S-1-5-32-544"));
        SetAcl(builder, sql, read, FakeAce.ExtendedRight(builder.SidOf(sqlServers), apply),
            new FakeAce(FakeAce.AccessDeniedObject, 0, 0x100, domainControllers, apply));

        // Applied by Authenticated Users but denied to Enterprise Domain Controllers: every member computer, no DC.
        var debug = AddDefaultAclGpo(builder, sysvol, "Debugowanie poza kontrolerami", $"SeDebugPrivilege = *{builder.SidOf(company.ZbigniewKazmierczak)}");
        SetAcl(builder, debug, read, FakeAce.ExtendedRight(PrivilegedGroupCatalog.AuthenticatedUsersSid, apply),
            new FakeAce(FakeAce.AccessDeniedObject, 0, 0x100, GroupPolicySecurityFilter.EnterpriseDomainControllersSid, apply));
        builder.SetGroupPolicyLinks(builder.DomainDn, [(sql, 0), (debug, 0)]);

        // On OU=Komputery, applied by the laptop's computer account only (an inherit-only Apply ACE does not count).
        var laptop = builder.AddGroupPolicyObject("Stacje – Laptop serwisowy");
        sysvol.PutSecurityTemplate(laptop, FakeDirectoryBuilder.Template("[Group Membership]", $"*{builder.SidOf(company.GrazynaDabrowska)}__Memberof = *S-1-5-32-555"));
        SetAcl(builder, laptop, read, FakeAce.ExtendedRight(builder.SidOf(company.Laptop), apply),
            new FakeAce(FakeAce.AccessAllowedObject, 0x0A, 0x100, PrivilegedGroupCatalog.AuthenticatedUsersSid, apply));
        builder.SetGroupPolicyLinks(company.ComputersOu, [(laptop, 0)]);

        var (results, scanner) = await ScanAsync(builder, sysvol);

        Assert.Empty(scanner.Notices);
        var pawel = Assert.Single(LocalRights(results, company.PawelLewandowski));
        Assert.Equal((PrivilegeLevel.High, 0, 1, 0), (pawel.Level, pawel.GroupPolicy!.DomainControllers, pawel.GroupPolicy.Servers, pawel.GroupPolicy.Workstations));
        Assert.False(pawel.GroupPolicy.IsConditional);
        var zbigniew = Assert.Single(LocalRights(results, company.ZbigniewKazmierczak));
        Assert.Equal((PrivilegeLevel.High, 0, 2, 3), (zbigniew.Level, zbigniew.GroupPolicy!.DomainControllers, zbigniew.GroupPolicy.Servers, zbigniew.GroupPolicy.Workstations));
        var grazyna = Assert.Single(LocalRights(results, company.GrazynaDabrowska));
        Assert.Equal((PrivilegeLevel.Elevated, 0, 0, 1), (grazyna.Level, grazyna.GroupPolicy!.DomainControllers, grazyna.GroupPolicy.Servers, grazyna.GroupPolicy.Workstations));
    }

    [Fact]
    public void SecurityFilter_FromTheDescriptor_AllowAndDenyOfApplyGroupPolicy()
    {
        var apply = GroupPolicySecurityFilter.ApplyGroupPolicyRight;
        var descriptor = FakeSecurityDescriptor.Build("S-1-5-21-1-2-3-512", null,
            FakeAce.FullControl("S-1-5-18"),
            new FakeAce(FakeAce.AccessAllowed, 0, 0xF00FF, "S-1-5-21-1-2-3-512"),
            FakeAce.ExtendedRight("S-1-5-21-1-2-3-1105", apply),
            FakeAce.ExtendedRight("S-1-5-21-1-2-3-1106", new Guid("1131f6aa-9c07-11d1-f79f-00c04fc2dcd2")),
            new FakeAce(FakeAce.AccessDeniedObject, 0, 0x100, "S-1-5-21-1-2-3-516", apply),
            new FakeAce(FakeAce.AccessAllowedObject, 0x0A, 0x100, "S-1-5-11", apply));

        var filter = GroupPolicySecurityFilter.FromDescriptor(descriptor)!;

        Assert.Equal(["S-1-5-18", "S-1-5-21-1-2-3-1105"], filter.Allowed.Order(StringComparer.Ordinal));
        Assert.Equal(["S-1-5-21-1-2-3-516"], filter.Denied);
        Assert.False(filter.AppliesToEveryComputer);
        Assert.True(filter.Applies(new HashSet<string> { "S-1-5-11", "S-1-5-21-1-2-3-1105" }));
        Assert.False(filter.Applies(new HashSet<string> { "S-1-5-21-1-2-3-1105", "S-1-5-21-1-2-3-516" }));
        Assert.False(filter.Applies(new HashSet<string> { "S-1-5-11", "S-1-5-21-1-2-3-1106" }));
        Assert.Null(GroupPolicySecurityFilter.FromDescriptor([1, 2, 3]));
        Assert.Null(GroupPolicySecurityFilter.FromDescriptor(null));
    }

    [Fact]
    public async Task NoGpoReachesAComputer_NothingIsReadFromSysvol()
    {
        var builder = new FakeDirectoryBuilder();
        builder.AddSampleCompany();
        var sysvol = new FakeSysvolFileReader();
        var unlinked = builder.AddGroupPolicyObject("Niepołączona");
        sysvol.PutSecurityTemplate(unlinked, FakeDirectoryBuilder.Template("[Privilege Rights]", "SeDebugPrivilege = *S-1-5-11"));

        var (results, scanner) = await ScanAsync(builder, sysvol);

        Assert.Empty(sysvol.OpenedOn);
        Assert.Empty(scanner.Notices);
        Assert.DoesNotContain(results.Values.SelectMany(r => r.Findings), f => f.Source == PrivilegeSource.GroupPolicy);
    }

    [Fact]
    public void Catalogue_LevelsByRole_AndLocalGroupNames()
    {
        var options = new PrivilegedAccessOptions();
        var administrators = LocalRightsCatalog.Find("S-1-5-32-544")!;
        Assert.Equal(
            [PrivilegeLevel.Elevated, PrivilegeLevel.High, PrivilegeLevel.Tier0],
            Enum.GetValues<ComputerRole>().Select(r => LocalRightsCatalog.LevelOn(administrators, r, options)));
        var logon = LocalRightsCatalog.Find("SeInteractiveLogonRight")!;
        Assert.Equal([PrivilegeLevel.None, PrivilegeLevel.None, PrivilegeLevel.Elevated], Enum.GetValues<ComputerRole>().Select(r => LocalRightsCatalog.LevelOn(logon, r, options)));
        var accountOperators = LocalRightsCatalog.Find("S-1-5-32-548")!;
        Assert.Equal([PrivilegeLevel.None, PrivilegeLevel.None, PrivilegeLevel.Tier0], Enum.GetValues<ComputerRole>().Select(r => LocalRightsCatalog.LevelOn(accountOperators, r, options)));
        foreach (var right in new[] { "SeCreateTokenPrivilege", "seManageVolumePrivilege", "SeRelabelPrivilege" })
        {
            Assert.Equal([PrivilegeLevel.High, PrivilegeLevel.High, PrivilegeLevel.Tier0],
                Enum.GetValues<ComputerRole>().Select(r => LocalRightsCatalog.LevelOn(LocalRightsCatalog.Find(right)!, r, options)));
        }

        var credentialManager = LocalRightsCatalog.Find("SeTrustedCredManAccessPrivilege")!;
        Assert.Equal([PrivilegeLevel.Elevated, PrivilegeLevel.Elevated, PrivilegeLevel.High],
            Enum.GetValues<ComputerRole>().Select(r => LocalRightsCatalog.LevelOn(credentialManager, r, options)));
        Assert.Null(LocalRightsCatalog.Find("SeChangeNotifyPrivilege"));
        Assert.Null(LocalRightsCatalog.Find("SeSecurityPrivilege"));
        Assert.Null(LocalRightsCatalog.Find("S-1-5-32-545"));

        Assert.Equal("S-1-5-32-544", LocalRightsCatalog.LocalGroupSid("Administratorzy (wbudowane)"));
        Assert.Equal("S-1-5-32-555", LocalRightsCatalog.LocalGroupSid(@"BUILTIN\Użytkownicy pulpitu zdalnego"));
        Assert.Equal("S-1-5-32-547", LocalRightsCatalog.LocalGroupSid("Power Users (built-in)"));
        Assert.Equal("S-1-5-32-573", LocalRightsCatalog.LocalGroupSid("*s-1-5-32-573"));
        Assert.Equal("S-1-5-32-573", LocalRightsCatalog.LocalGroupSid("Czytelnicy dziennika zdarzeń", n => n == "Czytelnicy dziennika zdarzeń" ? "S-1-5-32-573" : null));
        Assert.Null(LocalRightsCatalog.LocalGroupSid("SQL Admins"));
    }

    private static (FakeDirectoryBuilder Builder, SampleCompany Company, PrivilegedSample Sample, GroupPolicySample Gpo, FakeSysvolFileReader Sysvol) Create()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var sample = builder.AddPrivilegedAccessSample(company);
        var sysvol = new FakeSysvolFileReader();
        var gpo = builder.AddGroupPolicySample(company, sample, sysvol);
        return (builder, company, sample, gpo, sysvol);
    }

    /// <summary>A GPO with one User Rights Assignment line.</summary>
    private static Guid AddDefaultAclGpo(FakeDirectoryBuilder builder, FakeSysvolFileReader sysvol, string name, string line)
    {
        var gpo = builder.AddGroupPolicyObject(name);
        sysvol.PutSecurityTemplate(gpo, FakeDirectoryBuilder.Template("[Privilege Rights]", line));
        return gpo;
    }

    /// <summary>The nTSecurityDescriptor (DACL) of a GPO's container.</summary>
    private static void SetAcl(FakeDirectoryBuilder builder, Guid gpo, params FakeAce[] aces) =>
        builder.Session.SeedAttributes(builder.GroupPolicyDn(gpo), new Dictionary<string, object?>
        {
            [Attr.NtSecurityDescriptor] = FakeSecurityDescriptor.Build($"{builder.DomainSid}-512", null, aces),
        });

    private static async Task<(Dictionary<string, AccountPrivileges> Results, PrivilegedAccessScanner Scanner)> ScanAsync(
        FakeDirectoryBuilder builder, FakeSysvolFileReader sysvol, Func<PrivilegedAccessOptions, PrivilegedAccessOptions>? configure = null)
    {
        var options = (configure ?? (o => o))(new PrivilegedAccessOptions { DomainSid = builder.DomainSid, RootDomainSid = builder.DomainSid });
        var scanner = new PrivilegedAccessScanner(builder.Session, options, [new GroupPolicyGrantSource(sysvol)]);
        var evaluator = await scanner.PrepareAsync(Now, Ct);
        var results = new Dictionary<string, AccountPrivileges>(StringComparer.Ordinal);
        await foreach (var account in scanner.ScanAsync(evaluator, builder.DomainDn, SearchScope.Subtree, null, null, Ct))
        {
            results[Key(account.DistinguishedName)] = account;
        }

        return (results, scanner);
    }

    private static List<PrivilegeFinding> LocalRights(Dictionary<string, AccountPrivileges> results, string dn) =>
        [.. results[Key(dn)].Findings.Where(f => f.Kind == PrivilegeFindingKind.LocalRight)];

    private static string Key(string dn) => PrivilegedAccessScanner.KeyOf(dn);
}
