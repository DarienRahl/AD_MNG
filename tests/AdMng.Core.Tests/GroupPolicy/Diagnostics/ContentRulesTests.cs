using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Testing;
using static AdMng.Core.Tests.GroupPolicy.Diagnostics.DiagnosticsSnapshotBuilder;

namespace AdMng.Core.Tests.GroupPolicy.Diagnostics;

/// <summary>Logical errors in the settings of one GPO: Restricted Groups, GPP local groups, user rights, audit, passwords.</summary>
public sealed class ContentRulesTests
{
    private const string Administrators = "S-1-5-32-544";
    private const string RemoteDesktopUsers = "S-1-5-32-555";
    private const string Relax = @"MACHINE\System\CurrentControlSet\Control\SAM\RelaxMinimumPasswordLengthLimits=";

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
    [InlineData("4,0", GpoFindingCodes.LegacyAndAdvancedAudit, GpoFindingSeverity.Medium)]
    [InlineData(null, GpoFindingCodes.LegacyAndAdvancedAuditByDefault, GpoFindingSeverity.Low)]
    [InlineData("4,1", null, GpoFindingSeverity.Info)]
    public void LegacyAndAdvancedAudit_TheBasicCategoriesWinOnlyWhenForcingIsOff_UndefinedForcingIsOnByDefault(string? force, string? expected, GpoFindingSeverity severity)
    {
        var builder = new DiagnosticsSnapshotBuilder();
        string[] lines = force is null
            ? ["[Event Audit]", "AuditLogonEvents = 3"]
            : ["[Event Audit]", "AuditLogonEvents = 3", "[Registry Values]", @"MACHINE\System\CurrentControlSet\Control\Lsa\SCENoApplyLegacyAuditPolicy=" + force];
        var gpo = OneGpo(builder, Template(lines), AuditCsv());

        var findings = builder.Analyze()
            .Where(f => f.Code is GpoFindingCodes.LegacyAndAdvancedAudit or GpoFindingCodes.LegacyAndAdvancedAuditByDefault).ToList();

        Assert.Equal(expected is null ? Array.Empty<string>() : [expected], findings.Select(f => f.Code));
        Assert.All(findings, f => Assert.Equal(
            (gpo, @"Computer/Security/Registry Values/HKLM\System\CurrentControlSet\Control\Lsa!SCENoApplyLegacyAuditPolicy", severity),
            (f.GpoId!.Value, f.SettingKey!, f.Severity)));
    }

    [Fact]
    public void LegacyAndAdvancedAudit_NotForTheLegacyFormAlone()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        OneGpo(builder, Template("[Event Audit]", "AuditLogonEvents = 3"));

        var findings = builder.Analyze();

        Findings.None(findings, GpoFindingCodes.LegacyAndAdvancedAudit);
        Findings.None(findings, GpoFindingCodes.LegacyAndAdvancedAuditByDefault);
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

    [Theory]
    [InlineData("4,1", false)]
    [InlineData("4,0", true)]
    [InlineData(null, true)]
    public void PasswordLengthNeedsRelax_ADomainPolicy_TheRelaxOptionOfTheDomainControllersCounts(string? relaxOnDcs, bool expected)
    {
        // Default Domain Policy requires 16 characters; the relax option is a setting of the DCs that check domain passwords.
        var builder = new DiagnosticsSnapshotBuilder();
        var domain = builder.Gpo("Default Domain Policy", computer: 3);
        var controllers = builder.Gpo("Default Domain Controllers Policy", computer: 3);
        builder.Link(DomainDn, domain).Link(DomainControllersOu, controllers)
            .Folder(domain, Template("[System Access]", "MinimumPasswordLength = 16"))
            .Folder(controllers, relaxOnDcs is null
                ? Template("[System Access]", "LockoutBadCount = 5")
                : Template("[Registry Values]", Relax + relaxOnDcs));

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.PasswordLengthNeedsRelax);

        Assert.Equal(expected ? [domain] : Array.Empty<Guid>(), findings.Select(f => f.GpoId!.Value));
    }

    [Fact]
    public void PasswordLengthNeedsRelax_AnOuPolicy_EveryChainThatAppliesItNeedsTheRelaxOption()
    {
        // "Serwery – Hasła lokalne" requires 15 characters for local accounts; a domain-wide GPO enables the relax option.
        // Linked also to OU=Magazyn, which blocks inheritance, the GPO applies there without the relax option.
        var builder = new DiagnosticsSnapshotBuilder();
        var local = builder.Gpo("Serwery – Hasła lokalne", computer: 2);
        var relax = builder.Gpo("Domena – Długie hasła", computer: 2);
        builder.Link(DomainDn, relax).Link(Ou("Serwery"), local)
            .Folder(local, Template("[System Access]", "MinimumPasswordLength = 15"))
            .Folder(relax, Template("[Registry Values]", Relax + "4,1"));

        Findings.None(builder.Analyze(), GpoFindingCodes.PasswordLengthNeedsRelax);

        builder.Link(Ou("Magazyn"), local).Container(Ou("Magazyn"), blocksInheritance: true);
        Assert.Equal(local, Findings.Single(builder.Analyze(), GpoFindingCodes.PasswordLengthNeedsRelax).GpoId);
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
