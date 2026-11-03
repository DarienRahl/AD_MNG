using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Testing;
using static AdMng.Core.Tests.GroupPolicy.Diagnostics.DiagnosticsSnapshotBuilder;

namespace AdMng.Core.Tests.GroupPolicy.Diagnostics;

/// <summary>Logical errors in the settings of one GPO: Restricted Groups, GPP local groups, user rights, audit, passwords.</summary>
public sealed class ContentRulesTests
{
    private const string Administrators = "S-1-5-32-544";
    private const string RemoteDesktopUsers = "S-1-5-32-555";

    private static Guid OneGpo(DiagnosticsSnapshotBuilder builder, params (string, byte[])[] files)
    {
        var gpo = builder.Gpo("Serwery – Grupy lokalne", computer: 3);
        builder.Link(Ou("Serwery"), gpo).Folder(gpo, files);
        return gpo;
    }

    [Fact]
    public void RestrictedAndPreferenceSameGroup_MembersFormAndGppForOneGroup_NotForMemberOf()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = OneGpo(builder,
            Template("[Group Membership]", $"*{Administrators}__Members = *{DomainAdmins},*{GroupSerwisanci}", $"*{GroupSerwisanci}__Memberof = *{RemoteDesktopUsers}"),
            Groups(
                FakeDirectoryBuilder.LocalGroup("Administrators (built-in)", Administrators, [("CONTOSO\\GG_Serwisanci", GroupSerwisanci, "ADD")]),
                FakeDirectoryBuilder.LocalGroup("Remote Desktop Users (built-in)", RemoteDesktopUsers, [("CONTOSO\\GG_Księgowość", GroupKsiegowosc, "ADD")])));
        builder.Existing(GroupSerwisanci, GroupKsiegowosc);

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.RestrictedAndPreferenceSameGroup);

        Assert.Equal((gpo, Administrators), (finding.GpoId!.Value, finding.Arguments[0]));
        Assert.Equal($"Computer/Security/Group Membership/{Administrators}/Members", finding.SettingKey);
    }

    [Theory]
    [InlineData("*" + DomainAdmins, false)]
    [InlineData("CONTOSO\\Administrator", false)]
    [InlineData("Administratorzy domeny", false)]
    [InlineData("*" + GroupSerwisanci, true)]
    [InlineData("", true)]
    public void RestrictedAdministratorsNoAdmins_MembersWithoutAdministratorOrDomainAdmins(string members, bool expected)
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = OneGpo(builder, Template("[Group Membership]", $"*{Administrators}__Members = {members}"));
        builder.Existing(GroupSerwisanci);

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.RestrictedAdministratorsNoAdmins);

        Assert.Equal(expected, findings.Count == 1);
        Assert.All(findings, f => Assert.Equal((gpo, GpoFindingSeverity.High), (f.GpoId!.Value, f.Severity)));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("4,0", true)]
    [InlineData("4,1", false)]
    public void LegacyAndAdvancedAudit_BothFormsWithoutForcingSubcategories(string? force, bool expected)
    {
        var builder = new DiagnosticsSnapshotBuilder();
        string[] lines = force is null
            ? ["[Event Audit]", "AuditLogonEvents = 3"]
            : ["[Event Audit]", "AuditLogonEvents = 3", "[Registry Values]", @"MACHINE\System\CurrentControlSet\Control\Lsa\SCENoApplyLegacyAuditPolicy=" + force];
        var gpo = OneGpo(builder, Template(lines), AuditCsv());

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.LegacyAndAdvancedAudit);

        Assert.Equal(expected, findings.Count == 1);
        Assert.All(findings, f => Assert.Equal(
            (gpo, @"Computer/Security/Registry Values/HKLM\System\CurrentControlSet\Control\Lsa!SCENoApplyLegacyAuditPolicy"),
            (f.GpoId!.Value, f.SettingKey!)));
    }

    [Fact]
    public void LegacyAndAdvancedAudit_NotForTheLegacyFormAlone()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        OneGpo(builder, Template("[Event Audit]", "AuditLogonEvents = 3"));

        Findings.None(builder.Analyze(), GpoFindingCodes.LegacyAndAdvancedAudit);
    }

    [Theory]
    [InlineData("15", null, true)]
    [InlineData("15", "4,1", false)]
    [InlineData("14", null, false)]
    public void PasswordLengthNeedsRelax_AboveFourteenWithoutTheRelaxOption(string length, string? relax, bool expected)
    {
        var builder = new DiagnosticsSnapshotBuilder();
        string[] lines = relax is null
            ? ["[System Access]", "MinimumPasswordLength = " + length]
            : ["[System Access]", "MinimumPasswordLength = " + length, "[Registry Values]", @"MACHINE\System\CurrentControlSet\Control\SAM\RelaxMinimumPasswordLengthLimits=" + relax];
        OneGpo(builder, Template(lines));

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.PasswordLengthNeedsRelax);

        Assert.Equal(expected, findings.Count == 1);
        Assert.All(findings, f => Assert.Equal(("15", "Computer/Security/System Access/MinimumPasswordLength"), (f.Arguments[0], f.SettingKey!)));
    }

    [Fact]
    public void TypedPrincipalName_NamesInRightsRestrictedGroupsAndGpp()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = OneGpo(builder,
            Template("[Privilege Rights]", $"SeBackupPrivilege = *{Administrators},CONTOSO\\GG_Kopie_Zapasowe",
                "[Group Membership]", "GG_Lokalni_Serwisanci__Memberof = *S-1-5-32-555"),
            Groups(FakeDirectoryBuilder.LocalGroup("Administrators (built-in)", Administrators, [("CONTOSO\\Łukasz.Wiśniewski", null, "ADD")])));

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.TypedPrincipalName);

        Assert.Equal(["CONTOSO\\GG_Kopie_Zapasowe", "CONTOSO\\Łukasz.Wiśniewski", "GG_Lokalni_Serwisanci"], findings.Select(f => f.Arguments[0]).Order(StringComparer.Ordinal));
        Assert.Contains(findings, f => f.SettingKey == "Computer/Security/Privilege Rights/SeBackupPrivilege");
        Assert.All(findings, f => Assert.Equal((gpo, GpoFindingSeverity.Info), (f.GpoId!.Value, f.Severity)));
    }

    [Fact]
    public void UnresolvedPrincipal_DomainSidsThatResolveToNothing_OnlyWhenResolved()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = OneGpo(builder,
            Template("[Privilege Rights]", $"SeServiceLogonRight = *{Deleted},*{GroupSerwisanci},*S-1-5-20,*{DomainSid}-516",
                "[Group Membership]", $"*{Administrators}__Members = *{DomainAdmins},*{DomainSid}-2002"),
            Groups(FakeDirectoryBuilder.LocalGroup("Remote Desktop Users (built-in)", RemoteDesktopUsers, [("CONTOSO\\GG_Byli", DomainSid + "-2003", "ADD")])));
        builder.Existing(GroupSerwisanci);

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.UnresolvedPrincipal);

        Assert.Equal([Deleted, DomainSid + "-2002", DomainSid + "-2003"], findings.Select(f => f.Arguments[0]).Order(StringComparer.Ordinal));
        Assert.All(findings, f => Assert.Equal(gpo, f.GpoId));

        builder.Depth = GpoDiagnosticsDepth.Quick;
        Findings.None(builder.Analyze(), GpoFindingCodes.UnresolvedPrincipal);
    }

    [Fact]
    public void DenyLogonBroadGroup_EveryoneAuthenticatedUsersAdministratorsDomainUsers_NotGuests()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        OneGpo(builder, Template("[Privilege Rights]",
            "SeDenyNetworkLogonRight = *S-1-5-32-546,*S-1-1-0",
            $"SeDenyRemoteInteractiveLogonRight = Administrators,*{DomainUsers}",
            "SeDenyInteractiveLogonRight = *S-1-5-114",
            "SeNetworkLogonRight = *S-1-5-11"));

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.DenyLogonBroadGroup);

        Assert.Equal(["Administrators", "S-1-1-0", DomainUsers], findings.Select(f => f.Arguments[0]).Order(StringComparer.Ordinal));
        Assert.All(findings, f => Assert.Equal(GpoFindingSeverity.High, f.Severity));
    }

    [Fact]
    public void DangerousRightBroadGroup_SensitiveRightsForBroadGroups_NotLogonRights()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        OneGpo(builder, Template("[Privilege Rights]",
            $"SeDebugPrivilege = *{Administrators},*S-1-5-11",
            "SeBackupPrivilege = Wszyscy",
            $"SeImpersonatePrivilege = *{DomainComputers}",
            "SeInteractiveLogonRight = *S-1-5-32-545",
            "SeChangeNotifyPrivilege = *S-1-1-0"));

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.DangerousRightBroadGroup);

        Assert.Equal(
            [("Computer/Security/Privilege Rights/SeBackupPrivilege", "Wszyscy"), ("Computer/Security/Privilege Rights/SeDebugPrivilege", "S-1-5-11"),
                ("Computer/Security/Privilege Rights/SeImpersonatePrivilege", DomainComputers)],
            findings.Select(f => (f.SettingKey!, f.Arguments[0])).OrderBy(f => f.Item1, StringComparer.Ordinal));
        Assert.All(findings, f => Assert.Equal((GpoFindingSeverity.High, GpoFindingArea.Security), (f.Severity, f.Area)));
    }

    [Fact]
    public void BroadGroupInPrivilegedLocalGroup_RestrictedGroupsBothFormsAndGpp()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        OneGpo(builder,
            Template("[Group Membership]",
                $"*{Administrators}__Members = *{DomainAdmins},*S-1-5-11",
                $"*{DomainUsers}__Memberof = *{RemoteDesktopUsers}",
                "*S-1-5-32-559__Members = *S-1-1-0"),
            Groups(
                FakeDirectoryBuilder.LocalGroup("Backup Operators (built-in)", "S-1-5-32-551", [("Wszyscy", null, "ADD"), ("CONTOSO\\Domain Users", null, "REMOVE")]),
                FakeDirectoryBuilder.LocalGroup("Remote Desktop Users (built-in)", RemoteDesktopUsers, [("Everyone", "S-1-1-0", "ADD")], disabled: true)));

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.BroadGroupInPrivilegedLocalGroup);

        Assert.Equal(
            [("S-1-5-11", Administrators), (DomainUsers, RemoteDesktopUsers), ("Wszyscy", "S-1-5-32-551")],
            findings.Select(f => (f.Arguments[0], f.Arguments[1])).OrderBy(f => f.Item1, StringComparer.Ordinal));
    }
}
