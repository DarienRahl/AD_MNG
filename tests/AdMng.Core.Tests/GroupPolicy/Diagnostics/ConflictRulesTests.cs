using AdMng.Core.GroupPolicy.Diagnostics;
using static AdMng.Core.Tests.GroupPolicy.Diagnostics.DiagnosticsSnapshotBuilder;

namespace AdMng.Core.Tests.GroupPolicy.Diagnostics;

/// <summary>Conflicting and redundant settings through the modeller per linked container (Full).</summary>
public sealed class ConflictRulesTests
{
    private const string LengthKey = "Computer/Security/System Access/MinimumPasswordLength";

    [Fact]
    public void ConflictingSetting_NamesWinnerAndLoserWithTheirValues_AtTheTopmostContainer()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var domain = builder.Gpo("Default Domain Policy", computer: 3);
        var workstations = builder.Gpo("Stacje robocze – Zasady haseł", computer: 2);
        var firewall = builder.Gpo("Komputery – Zapora", computer: 1);
        builder.Link(DomainDn, domain).Link(Ou("Firma"), workstations).Link(Ou("Firma", "Komputery"), firewall)
            .Folder(domain, Template("[System Access]", "MinimumPasswordLength = 7", "PasswordComplexity = 1"))
            .Folder(workstations, Template("[System Access]", "MinimumPasswordLength = 12", "PasswordComplexity = 1"))
            .Folder(firewall);

        var findings = builder.Analyze();

        var conflict = Findings.Single(findings, GpoFindingCodes.ConflictingSetting);
        Assert.Equal((workstations, Ou("Firma"), LengthKey), (conflict.GpoId!.Value, conflict.ContainerDn!, conflict.SettingKey!));
        Assert.Equal(["Stacje robocze – Zasady haseł", "Default Domain Policy", "12", "7"], conflict.Arguments);
        Assert.Equal([domain], conflict.RelatedGpoIds);
        Assert.Equal((GpoFindingSeverity.Info, GpoFindingArea.Conflict), (conflict.Severity, conflict.Area));

        var redundant = Findings.Single(findings, GpoFindingCodes.RedundantSetting);
        Assert.Equal("Computer/Security/System Access/PasswordComplexity", redundant.SettingKey);
        Assert.Equal(["Stacje robocze – Zasady haseł", "Default Domain Policy", "1"], redundant.Arguments);
        Assert.Equal([domain], redundant.RelatedGpoIds);
    }

    [Fact]
    public void Conflicts_PreferenceItemIsOneSetting_ConditionalItemsAreNotCompared()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var first = builder.Gpo("Pracownicy – Dyski", user: 1, computer: 0);
        var copy = builder.Gpo("Pracownicy – Dyski (kopia)", user: 1, computer: 0);
        builder.Link(Ou("Pracownicy"), first).Link(Ou("Pracownicy"), copy)
            .Folder(first, Drives("{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001}", @"\\fs01.contoso.test\Księgowość"))
            .Folder(copy, Drives("{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001}", @"\\fs02.contoso.test\Księgowość"));

        var conflict = Findings.Single(builder.Analyze(), GpoFindingCodes.ConflictingSetting);

        Assert.Equal((copy, "User/Preferences/Drives/{0D1F7F22-6D1B-4A35-9D9C-0E3D9F3F0001}"), (conflict.GpoId!.Value, conflict.SettingKey!));
        Assert.Equal(["Pracownicy – Dyski (kopia)", "Pracownicy – Dyski", string.Empty, string.Empty], conflict.Arguments);
    }

    [Fact]
    public void Conflicts_OneGpo_OnlyThoseItTakesPartIn()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var a = builder.Gpo("Serwery – Audyt A", computer: 1);
        var b = builder.Gpo("Serwery – Audyt B", computer: 1);
        var c = builder.Gpo("Serwery – Hasła", computer: 1);
        builder.Link(Ou("Serwery"), a).Link(Ou("Serwery"), b).Link(Ou("Serwery"), c)
            .Folder(a, Template("[Event Audit]", "AuditLogonEvents = 3", "[System Access]", "MinimumPasswordLength = 10"))
            .Folder(b, Template("[Event Audit]", "AuditLogonEvents = 1"))
            .Folder(c, Template("[System Access]", "MinimumPasswordLength = 14"));

        builder.Scope = b;
        var forB = Findings.All(builder.Analyze(), GpoFindingCodes.ConflictingSetting);
        Assert.Equal("Computer/Security/Event Audit/AuditLogonEvents", Assert.Single(forB).SettingKey);

        builder.Scope = null;
        Assert.Equal(2, Findings.All(builder.Analyze(), GpoFindingCodes.ConflictingSetting).Count);
    }

    [Fact]
    public void Conflicts_NotEvaluatedWithoutFolders()
    {
        var builder = new DiagnosticsSnapshotBuilder { FoldersRead = false };
        var a = builder.Gpo("A", computer: 1);
        builder.Link(Ou("Serwery"), a);

        Assert.Contains(GpoRuleGroup.Conflicts, builder.Analyze().NotEvaluated);
    }
}
