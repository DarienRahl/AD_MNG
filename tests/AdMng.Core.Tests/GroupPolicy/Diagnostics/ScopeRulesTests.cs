using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Core.GroupPolicy.Modeling;
using static AdMng.Core.Tests.GroupPolicy.Diagnostics.DiagnosticsSnapshotBuilder;

namespace AdMng.Core.Tests.GroupPolicy.Diagnostics;

/// <summary>Which objects the GPOs reach (Full: object counts through the inheritance, loopback, account policy).</summary>
public sealed class ScopeRulesTests
{
    [Fact]
    public void UserSettingsWithoutUsers_LinksReachOnlyComputers()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Komputery – Mapowanie dysków", user: 2, computer: 0);
        var users = builder.Gpo("Pracownicy – Mapowanie dysków", user: 2, computer: 0);
        builder.Link(Ou("Firma", "Komputery"), gpo).Link(Ou("Firma", "Pracownicy"), users)
            .Objects(Ou("Firma", "Komputery"), users: 0, computers: 3)
            .Objects(Ou("Firma", "Pracownicy", "IT"), users: 2, computers: 0)
            .Folder(gpo).Folder(users);

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.UserSettingsWithoutUsers);

        Assert.Equal((gpo, GpoFindingSeverity.Medium, GpoFindingArea.Scope), (finding.GpoId!.Value, finding.Severity, finding.Area));
    }

    [Fact]
    public void UserSettingsWithoutUsers_NotWhenLoopbackReachesTheComputers()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Sala szkoleniowa – Pulpit", user: 2, computer: 1);
        var loopback = builder.Gpo("Sala szkoleniowa – Sprzężenie zwrotne", user: 0, computer: 1);
        builder.Link(Ou("Sala szkoleniowa"), gpo).Link(Ou("Sala szkoleniowa"), loopback)
            .Objects(Ou("Sala szkoleniowa"), users: 0, computers: 12)
            .Folder(gpo).Folder(loopback, RegistryPol(false, (GroupPolicyModeler.LoopbackKey, GroupPolicyModeler.LoopbackValueName, 1)));

        var findings = builder.Analyze();

        Findings.None(findings, GpoFindingCodes.UserSettingsWithoutUsers);
        Findings.None(findings, GpoFindingCodes.LoopbackWithoutComputers);
    }

    [Fact]
    public void ComputerSettingsWithoutComputers_BlockedInheritanceCounts_SiteLinksAreNotJudged()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Pracownicy – Zapora", user: 0, computer: 4);
        var blocked = builder.Gpo("Domena – Aktualizacje", user: 0, computer: 2);
        var site = builder.Gpo("Lokacja Warszawa – Drukarki", user: 0, computer: 2);
        builder.Link(Ou("Pracownicy"), gpo)
            .Link(DomainDn, blocked).Container(Ou("Odizolowane"), blocksInheritance: true)
            .Link(SiteDn("Warszawa"), site)
            .Objects(Ou("Pracownicy", "Księgowość"), users: 5, computers: 0)
            .Objects(Ou("Odizolowane"), users: 0, computers: 7);

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.ComputerSettingsWithoutComputers);

        Assert.Equal(new[] { blocked, gpo }.Order(), findings.Select(f => f.GpoId!.Value).Order());
    }

    [Fact]
    public void LoopbackWithoutComputers_LoopbackSetWhereNoComputerIs()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Pracownicy – Sprzężenie zwrotne", user: 1, computer: 1);
        builder.Link(Ou("Pracownicy"), gpo)
            .Objects(Ou("Pracownicy"), users: 8, computers: 0)
            .Folder(gpo, RegistryPol(false, (GroupPolicyModeler.LoopbackKey, GroupPolicyModeler.LoopbackValueName, 2)));

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.LoopbackWithoutComputers);

        Assert.Equal((gpo, "Replace"), (finding.GpoId!.Value, finding.Arguments[0]));
        Assert.Equal(@"Computer/Registry/HKLM\Software\Policies\Microsoft\Windows\System!UserPolicyMode", finding.SettingKey);
    }

    [Fact]
    public void AccountPolicyBelowDomain_PasswordPolicyLinkedToAnOu_NotOnTheDomainHead()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var ou = builder.Gpo("Stacje robocze – Zasady haseł", computer: 2);
        var domain = builder.Gpo("Domena – Zasady haseł", computer: 2);
        var kerberos = builder.Gpo("Serwery – Kerberos", computer: 2);
        builder.Link(Ou("Stacje robocze"), ou).Link(DomainDn, domain).Link(Ou("Serwery"), kerberos)
            .Folder(ou, Template("[System Access]", "MinimumPasswordLength = 12", "LockoutBadCount = 5"))
            .Folder(domain, Template("[System Access]", "MinimumPasswordLength = 12"))
            .Folder(kerberos, Template("[Kerberos Policy]", "MaxTicketAge = 8"));

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.AccountPolicyBelowDomain);

        Assert.Equal(
            [(kerberos, "Computer/Security/Kerberos Policy/MaxTicketAge"), (ou, "Computer/Security/System Access/MinimumPasswordLength")],
            findings.Select(f => (f.GpoId!.Value, f.SettingKey!)).OrderBy(f => f.Item2, StringComparer.Ordinal));
    }

    [Fact]
    public void ScopeRules_NeedTheCounts()
    {
        var builder = new DiagnosticsSnapshotBuilder { Depth = GpoDiagnosticsDepth.Quick };
        var gpo = builder.Gpo("Pracownicy – Zapora", user: 0, computer: 4);
        builder.Link(Ou("Pracownicy"), gpo);

        var result = builder.Analyze();

        Findings.None(result, GpoFindingCodes.ComputerSettingsWithoutComputers);
        Assert.Contains(GpoRuleGroup.ObjectCounts, result.NotEvaluated);
    }
}
