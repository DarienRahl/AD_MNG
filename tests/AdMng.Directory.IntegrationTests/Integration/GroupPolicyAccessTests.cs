using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.Ldap;
using AdMng.Core.PrivilegedAccess;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Presentation.Session;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// Local rights from GPOs (README U4c) against the Samba lab, in this run's own OU (<c>U4C-GPO …</c>): GPOs created with
/// <c>samba-tool gpo create</c> (display names <c>U4C-…</c>), linked to the OU with <c>setlink</c> (one enforced) and a
/// child OU blocking inheritance with <c>setinheritance</c>; the links, the computers and the inheritance as the review
/// reads them over LDAP; then GptTmpl.inf (UTF-16 with BOM) and Groups.xml written to SYSVOL with smbclient and the whole
/// source end to end through a Linux test implementation of the SYSVOL reader. The GPOs are deleted (links included)
/// at the end; the OU goes with the run's OU.
/// </summary>
public sealed class GroupPolicyAccessTests(LabFixture lab)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LinksComputersAndInheritance_AreReadFromTheLab()
    {
        SambaTools.Require();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var ou = await lab.CreateOuAsync(TestNames.Unique("U4C-GPO"));
        var stations = await lab.CreateOuAsync("Stacje – Łódź", ou);
        var server = await CreateComputerAsync(ou, $"U4CS{suffix}", "Windows Server 2022 Standard");
        var workstation = await CreateComputerAsync(stations, $"U4CW{suffix}", "Windows 11 Enterprise");
        using var tools = SambaTools.Create();
        var gpos = new List<Guid>();
        try
        {
            var winner = await tools.CreateGpoAsync($"U4C-{suffix} Serwery – administratorzy", Ct);
            gpos.Add(winner);
            var loser = await tools.CreateGpoAsync($"U4C-{suffix} Serwery – stara", Ct);
            gpos.Add(loser);
            var enforced = await tools.CreateGpoAsync($"U4C-{suffix} Wymuszona – Gdańsk", Ct);
            gpos.Add(enforced);

            // Samba prepends a new link: winner (link order 1) is linked first.
            await tools.LinkAsync(ou, winner, enforced: false, Ct);
            await tools.LinkAsync(ou, loser, enforced: false, Ct);
            await tools.LinkAsync(ou, enforced, enforced: true, Ct);
            await tools.BlockInheritanceAsync(stations, Ct);

            var topology = await GroupPolicyTopology.ReadAsync(lab.Admin, 500, Ct);

            Assert.Equal($"U4C-{suffix} Wymuszona – Gdańsk", topology.Policies[enforced].DisplayName);
            Assert.False(topology.Policies[winner].ComputerSettingsDisabled);
            Assert.False(topology.Policies[winner].IsPathRedirected(LabSettings.DomainDns));
            var container = Assert.Single(topology.Containers, c => DistinguishedName.AreEqual(c.Dn, ou));
            Assert.Equal(GroupPolicySomKind.OrganizationalUnit, container.Kind);
            Assert.Equal([enforced, loser, winner], container.Links.Select(l => l.GpoId!.Value));
            Assert.Equal([3, 2, 1], container.Links.Select(l => l.Order));
            Assert.Equal([true, false, false], container.Links.Select(l => l.Enforced));
            Assert.False(container.BlocksInheritance);
            Assert.True(Assert.Single(topology.Containers, c => DistinguishedName.AreEqual(c.Dn, stations)).BlocksInheritance);
            Assert.Contains(topology.Containers, c => c.Kind == GroupPolicySomKind.Domain);
            Assert.Equal(ComputerRole.Server, Assert.Single(topology.Computers, c => DistinguishedName.AreEqual(c.Dn, server)).Role);
            Assert.Equal(ComputerRole.Workstation, Assert.Single(topology.Computers, c => DistinguishedName.AreEqual(c.Dn, workstation)).Role);
            Assert.Contains(topology.Computers, c => c.Role == ComputerRole.DomainController);

            // In the OU: the domain's links, then ours by link order, the enforced one last. Below the blocking OU only
            // the enforced link of ours is left.
            var inheritance = new GroupPolicyInheritance(topology.DomainDn, topology.Containers);
            var ours = gpos.ToHashSet();
            Assert.Equal([loser, winner, enforced], inheritance.Resolve(ou, null).Select(a => a.GpoId).Where(ours.Contains));
            Assert.Equal([enforced], inheritance.Resolve(stations, null).Select(a => a.GpoId).Where(ours.Contains));
        }
        finally
        {
            foreach (var gpo in gpos)
            {
                await tools.DeleteGpoAsync(gpo);
            }
        }
    }

    [Fact]
    public async Task LocalRights_EndToEnd_ThroughSysvolOfTheSessionDc()
    {
        SambaTools.Require();
        var admin = lab.Admin;
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var policy = DomainPolicy.FromDomainObject((await admin.GetAsync(LabSettings.DomainDn, DomainPolicy.Attributes, Ct))!);
        var ou = await lab.CreateOuAsync(TestNames.Unique("U4C-GPO"));
        var stations = await lab.CreateOuAsync("Stacje", ou);
        await CreateComputerAsync(ou, $"U4CS{suffix}", "Windows Server 2025 Datacenter");
        await CreateComputerAsync(stations, $"U4CW{suffix}", "Windows 11 Pro");

        // Ludwika through a group (Restricted Groups MemberOf), Bogusław by SID (SeDebugPrivilege), Jędrzej through a group
        // named in Groups.xml without its SID, Grażyna only in the GPO that loses the link order.
        var ludwika = await lab.CreateUserAsync(ou, "Ludwika Ślązak");
        var boguslaw = await lab.CreateUserAsync(ou, "Bogusław Żurek");
        var jedrzej = await lab.CreateUserAsync(ou, "Jędrzej Kęsik");
        var grazyna = await lab.CreateUserAsync(ou, "Grażyna Przegrana");
        var admins = await CreateGroupAsync(ou, $"GG_U4C_Admini_{suffix}", ludwika);
        var rdpName = $"GG_U4C_RDP_{suffix}";
        await CreateGroupAsync(ou, rdpName, jedrzej, samAccountName: rdpName);

        using var tools = SambaTools.Create();
        var gpos = new List<Guid>();
        try
        {
            var winner = await tools.CreateGpoAsync($"U4C-{suffix} Serwery – administratorzy", Ct);
            gpos.Add(winner);
            var loser = await tools.CreateGpoAsync($"U4C-{suffix} Serwery – stara", Ct);
            gpos.Add(loser);
            var enforced = await tools.CreateGpoAsync($"U4C-{suffix} Wymuszona – pulpit zdalny", Ct);
            gpos.Add(enforced);
            await tools.PutAsync(winner, SecurityTemplate.RelativePath, Utf16(
                "[Unicode]", "Unicode=yes", "[Version]", "signature=\"$CHICAGO$\"", "Revision=1",
                "[Group Membership]",
                $"*{await SidAsync(admins)}__Memberof = *S-1-5-32-544",
                $"*{await SidAsync(admins)}__Members =",
                "[Privilege Rights]",
                $"SeDebugPrivilege = *S-1-5-32-544,*{await SidAsync(boguslaw)}"), Ct);
            await tools.PutAsync(loser, SecurityTemplate.RelativePath, Utf16("[Privilege Rights]", $"SeDebugPrivilege = *{await SidAsync(grazyna)}"), Ct);
            await tools.PutAsync(enforced, GppGroupsParser.RelativePath, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><Groups clsid=\"{3125E937-EB16-4b4c-9934-544FC6D24D26}\">"
                + "<Group clsid=\"{6D4A79E4-529C-4481-ABD0-F5BD7EA93BA7}\" name=\"Remote Desktop Users (built-in)\" uid=\"{0E7C6D5B-8C1B-4C55-9C4A-2B1E29D1A001}\">"
                + "<Properties action=\"U\" groupSid=\"S-1-5-32-555\" groupName=\"Remote Desktop Users (built-in)\" deleteAllUsers=\"0\" deleteAllGroups=\"0\">"
                + $"<Members><Member name=\"{LabSettings.NetBiosName}\\{rdpName}\" action=\"ADD\" sid=\"\"/></Members></Properties></Group></Groups>")).ToArray(), Ct);
            await tools.LinkAsync(ou, winner, enforced: false, Ct);
            await tools.LinkAsync(ou, loser, enforced: false, Ct);
            await tools.LinkAsync(ou, enforced, enforced: true, Ct);
            await tools.BlockInheritanceAsync(stations, Ct);

            var reader = new SmbClientSysvolFileReader(SambaTools.Address, LabSettings.AdminUser, LabSettings.AdminPassword);
            var scanner = new PrivilegedAccessScanner(admin, new PrivilegedAccessOptions { DomainSid = policy.DomainSid, RootDomainSid = policy.DomainSid },
                [new GroupPolicyGrantSource(reader)]);
            var evaluator = await scanner.PrepareAsync(DateTimeOffset.UtcNow, Ct);
            var results = new Dictionary<string, AccountPrivileges>(StringComparer.Ordinal);
            await foreach (var account in scanner.ScanAsync(evaluator, ou, SearchScope.Subtree, null, null, Ct))
            {
                results[account.Account.Name] = account;
            }

            // SYSVOL of the session's DC (the name RootDSE gave the session).
            Assert.Equal([admin.Info.Server.ToLowerInvariant()], reader.OpenedOn.Select(s => s.ToLowerInvariant()));
            Assert.DoesNotContain(scanner.Notices, n => n.Arguments.Any(a => a.Contains("U4C-" + suffix, StringComparison.Ordinal)));

            var memberOf = Assert.Single(LocalRights(results, "Ludwika Ślązak"));
            Assert.Equal((LocalRightsCatalog.AdministratorsSid, LocalRightMechanism.RestrictedGroupMemberOf, PrivilegeLevel.High),
                (memberOf.GroupPolicy!.Right, memberOf.GroupPolicy.Mechanism, memberOf.Level));
            Assert.Equal((0, 1, 0), (memberOf.GroupPolicy.DomainControllers, memberOf.GroupPolicy.Servers, memberOf.GroupPolicy.Workstations));
            Assert.Equal(["Ludwika Ślązak", $"GG_U4C_Admini_{suffix}"], memberOf.Path.Select(s => s.Name));
            Assert.True(DistinguishedName.AreEqual(ou, memberOf.ScopeDn));

            var debug = Assert.Single(LocalRights(results, "Bogusław Żurek"));
            Assert.Equal(("SeDebugPrivilege", PrivilegeLevel.High, $"U4C-{suffix} Serwery – administratorzy"),
                (debug.GroupPolicy!.Right, debug.Level, debug.GroupPolicy.GpoName));
            Assert.Empty(LocalRights(results, "Grażyna Przegrana"));

            // The enforced GPO reaches the blocking OU too: the server and the workstation.
            var rdp = Assert.Single(LocalRights(results, "Jędrzej Kęsik"));
            Assert.Equal((LocalRightsCatalog.RemoteDesktopUsersSid, LocalRightMechanism.PreferenceGroups, PrivilegeLevel.Elevated),
                (rdp.GroupPolicy!.Right, rdp.GroupPolicy.Mechanism, rdp.Level));
            Assert.Equal((0, 1, 1), (rdp.GroupPolicy.DomainControllers, rdp.GroupPolicy.Servers, rdp.GroupPolicy.Workstations));
            Assert.True(rdp.GroupPolicy.LinkEnforced);
        }
        finally
        {
            foreach (var gpo in gpos)
            {
                await tools.DeleteGpoAsync(gpo);
            }
        }
    }

    [Fact]
    public async Task WindowsReader_UsesTheSessionsCredential_RefusesADistinguishedNameLogon_AndNeedsWindows()
    {
        LabSettings.RequireLab();

        // The lab session binds with a UPN: its in-memory credential is what the share would get.
        Assert.Equal(LabSettings.AdminUser, SysvolLogon.Of(lab.Admin).UserName);

        // A simple bind with a distinguished name has no logon name for SMB.
        var dnBind = $"CN=Administrator,CN=Users,{LabSettings.DomainDn}";
        await using (var session = await LabFixture.ConnectAsync(LabSettings.Plain(dnBind), LabSettings.AdminPassword, cancellationToken: Ct))
        {
            Assert.Equal(SysvolUnavailableReason.DistinguishedNameLogon, Assert.Throws<SysvolUnavailableException>(() => SysvolLogon.Of(session)).Reason);
        }

        if (!OperatingSystem.IsWindows())
        {
            var error = await Assert.ThrowsAsync<SysvolUnavailableException>(() => new WindowsSysvolFileReader().OpenAsync(lab.Admin, Ct));
            Assert.Equal(SysvolUnavailableReason.NotSupported, error.Reason);
        }
    }

    private async Task<string> CreateComputerAsync(string parentDn, string name, string operatingSystem)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", name).ToString();
        await lab.Admin.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.User, ObjectClasses.Computer],
            [Attr.SamAccountName] = [name + "$"],
            [Attr.UserAccountControl] = [((int)UserAccountControl.WorkstationTrustAccount).ToString(CultureInfo.InvariantCulture)],
            [Attr.OperatingSystem] = [operatingSystem],
        }, Ct);
        return dn;
    }

    private async Task<string> CreateGroupAsync(string parentDn, string cn, string member, string? samAccountName = null)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", cn).ToString();
        await lab.Admin.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", ObjectClasses.Group],
            [Attr.SamAccountName] = [samAccountName ?? TestNames.SamAccountName()],
            [Attr.Member] = [member],
        }, Ct);
        return dn;
    }

    private async Task<string> SidAsync(string dn) =>
        (await lab.Admin.GetAsync(dn, [Attr.ObjectSid], Ct))?.ObjectSid ?? throw new InvalidOperationException($"{dn} has no objectSid.");

    private static byte[] Utf16(params string[] lines) =>
        [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(string.Join("\r\n", lines) + "\r\n")];

    private static List<PrivilegeFinding> LocalRights(Dictionary<string, AccountPrivileges> results, string name) =>
        [.. results[name].Findings.Where(f => f.Kind == PrivilegeFindingKind.LocalRight)];
}
