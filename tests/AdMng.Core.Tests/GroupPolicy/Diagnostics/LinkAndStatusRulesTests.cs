using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Core.GroupPolicy.Management;
using static AdMng.Core.Tests.GroupPolicy.Diagnostics.DiagnosticsSnapshotBuilder;

namespace AdMng.Core.Tests.GroupPolicy.Diagnostics;

/// <summary>The rules about links and the GPO status (Quick, LDAP only) on pure snapshots.</summary>
public sealed class LinkAndStatusRulesTests
{
    [Fact]
    public void GpoNotLinked_OnlyForAGpoWithoutAnyLink()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var archive = builder.Gpo("Archiwum – Stare hasła");
        var linked = builder.Gpo("Kraków – Drukarki");
        builder.Link(Ou("Kraków"), linked);

        var findings = builder.Analyze();

        var finding = Findings.Single(findings, GpoFindingCodes.GpoNotLinked);
        Assert.Equal((archive, "Archiwum – Stare hasła", GpoFindingSeverity.Info, GpoFindingArea.Links), (finding.GpoId, finding.GpoName, finding.Severity, finding.Area));
        Assert.Null(finding.Fix);
    }

    [Fact]
    public void GpoLinksAllDisabled_OffersToEnableTheFirstLink_NotWhenOneLinkIsEnabled()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var off = builder.Gpo("Gdańsk – Tapeta");
        var mixed = builder.Gpo("Łódź – Tapeta");
        builder.Link(Ou("Gdańsk"), off, enabled: false).Link(Ou("Gdańsk", "Biuro"), off, enabled: false)
            .Link(Ou("Łódź"), mixed, enabled: false).Link(Ou("Łódź", "Biuro"), mixed);

        var findings = builder.Analyze();

        var finding = Findings.Single(findings, GpoFindingCodes.GpoLinksAllDisabled);
        Assert.Equal((off, GpoFindingSeverity.Low, "2"), (finding.GpoId, finding.Severity, finding.Arguments[0]));
        var fix = Assert.IsType<GpoQuickFix.EnableLink>(finding.Fix);
        Assert.Equal(off, fix.GpoId);
        Assert.Equal(finding.ContainerDn, fix.ContainerDn);
        Findings.None(findings, GpoFindingCodes.GpoNotLinked);
    }

    [Fact]
    public void LinkToMissingGpo_OffersTheUnlink()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gone = Guid.Parse("1D2E3F40-5A6B-4C7D-8E9F-0A1B2C3D4E5F");
        var present = builder.Gpo("Pracownicy – Zasady");
        builder.Link(Ou("Pracownicy"), gone).Link(Ou("Pracownicy"), present);

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.LinkToMissingGpo);

        Assert.Equal((gone, Ou("Pracownicy"), GpoFindingSeverity.Medium), (finding.GpoId, finding.ContainerDn, finding.Severity));
        Assert.Equal("{1D2E3F40-5A6B-4C7D-8E9F-0A1B2C3D4E5F}", finding.Arguments[0]);
        Assert.Equal(new GpoQuickFix.Unlink(Ou("Pracownicy"), gone), finding.Fix);
    }

    [Fact]
    public void LinkMalformed_CountsUnreadableAndForeignEntries_ASiteMayLinkOtherDomains()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Sprzedaż – Zasady");
        builder.Link(Ou("Sprzedaż"), gpo)
            .Raw(Ou("Sprzedaż"), "[LDAP://cn={0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0},cn=policies,cn=system,DC=oddzial,DC=contoso,DC=test;0]")
            .Raw(Ou("Sprzedaż"), "[to nie jest łącze]")
            .Raw(SiteDn("Wrocław"), "[LDAP://cn={0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0},cn=policies,cn=system,DC=oddzial,DC=contoso,DC=test;0]");

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.LinkMalformed);

        Assert.Equal((Ou("Sprzedaż"), (Guid?)null), (finding.ContainerDn, finding.GpoId));
        Assert.Equal(["1", "1"], finding.Arguments);
    }

    [Fact]
    public void DuplicateLink_OffersToKeepOneLink()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Księgowość – Mapowanie");
        builder.Link(Ou("Księgowość"), gpo).Link(Ou("Księgowość"), gpo, enforced: true).Link(Ou("Kadry"), gpo);

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.DuplicateLink);

        Assert.Equal((gpo, Ou("Księgowość"), "2"), (finding.GpoId, finding.ContainerDn, finding.Arguments[0]));
        Assert.Equal(new GpoQuickFix.RemoveDuplicateLinks(Ou("Księgowość"), gpo), finding.Fix);
    }

    [Fact]
    public void LinkedAllSettingsDisabled_OnlyWithAnEnabledLink()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var linked = builder.Gpo("Serwery – Wyłączona", status: GpoStatus.AllSettingsDisabled);
        var parked = builder.Gpo("Serwery – Odłożona", status: GpoStatus.AllSettingsDisabled);
        builder.Link(Ou("Serwery"), linked).Link(Ou("Serwery"), parked, enabled: false);

        var findings = builder.Analyze();

        Assert.Equal(linked, Findings.Single(findings, GpoFindingCodes.LinkedAllSettingsDisabled).GpoId);

        // Both parts are disabled: one finding about the status, not two about the parts.
        Findings.None(findings, GpoFindingCodes.PartDisabledWithSettings);
    }

    [Fact]
    public void PartDisabledWithSettings_OffersToEnableThePart()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Użytkownicy – Pulpit", user: 3, computer: 0, status: GpoStatus.UserSettingsDisabled);
        var fine = builder.Gpo("Komputery – Zapora", user: 0, computer: 4, status: GpoStatus.UserSettingsDisabled);
        builder.Link(Ou("Pracownicy"), gpo).Link(Ou("Komputery"), fine);

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.PartDisabledWithSettings);

        Assert.Equal((gpo, "User", "3"), (finding.GpoId, finding.Arguments[0], finding.Arguments[1]));
        Assert.Equal(new GpoQuickFix.SetStatus(gpo, GpoStatus.AllSettingsEnabled), finding.Fix);
    }

    [Fact]
    public void PartEnabledWithoutSettings_OffersToDisableTheEmptyPart_OnlyWhenLinked()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Komputery – Aktualizacje", user: 0, computer: 5);
        var unlinked = builder.Gpo("Komputery – Szkic", user: 0, computer: 2);
        builder.Link(Ou("Komputery"), gpo);

        var findings = builder.Analyze();

        var finding = Findings.Single(findings, GpoFindingCodes.PartEnabledWithoutSettings);
        Assert.Equal((gpo, "User", GpoFindingSeverity.Info), (finding.GpoId, finding.Arguments[0], finding.Severity));
        Assert.Equal(new GpoQuickFix.SetStatus(gpo, GpoStatus.UserSettingsDisabled), finding.Fix);
        Findings.None(findings, GpoFindingCodes.PartEnabledWithoutSettings, unlinked);
    }

    [Fact]
    public void EmptyGpoLinked_BothVersionsZero()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var empty = builder.Gpo("Nowa zasada", user: 0, computer: 0);
        var full = builder.Gpo("Pełna zasada", user: 1, computer: 1);
        builder.Link(Ou("Kraków"), empty).Link(Ou("Kraków"), full);

        var findings = builder.Analyze();

        Assert.Equal(empty, Findings.Single(findings, GpoFindingCodes.EmptyGpoLinked).GpoId);
        Findings.None(findings, GpoFindingCodes.PartEnabledWithoutSettings, empty);
    }

    [Fact]
    public void OneGpo_OnlyItsFindings_NoContainerOrWmiFilterFindings()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var target = builder.Gpo("Kraków – Drukarki", user: 0, computer: 0);
        var other = builder.Gpo("Archiwum");
        builder.Link(Ou("Kraków"), target).Link(Ou("Kraków"), Guid.NewGuid()).WmiFilter("{0A0B0C0D-0E0F-4A1B-8C2D-3E4F5A6B7C8D}", "Nieużywany filtr");
        builder.Scope = target;

        var findings = builder.Analyze();

        Assert.All(findings, f => Assert.Equal(target, f.GpoId));
        Findings.Single(findings, GpoFindingCodes.EmptyGpoLinked, target);
        Findings.None(findings, GpoFindingCodes.GpoNotLinked, other);
        Findings.None(findings, GpoFindingCodes.LinkToMissingGpo);
        Findings.None(findings, GpoFindingCodes.WmiFilterUnused);
    }

    [Fact]
    public void Findings_AreOrderedBySeverity_AndToStringCarriesNoDirectoryValues()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Zarząd – Tajne ustawienia", user: 0, computer: 0, descriptor: Descriptor());
        builder.Link(Ou("Zarząd"), gpo);

        var findings = builder.Analyze();

        Assert.Equal(findings.OrderByDescending(f => f.Severity).Select(f => f.Severity), findings.Select(f => f.Severity));
        Assert.Equal(GpoFindingCodes.NoApplyRight, findings[0].Code);
        var text = findings[0].ToString();
        Assert.Contains(SysvolPaths.FolderName(gpo), text, StringComparison.Ordinal);
        Assert.DoesNotContain("Zarząd", text, StringComparison.Ordinal);
        Assert.Equal("RemoveTrustee", new GpoQuickFix.RemoveTrustee(gpo, GroupSerwisanci).ToString());
        Assert.Equal("Unlink", new GpoQuickFix.Unlink(Ou("Zarząd"), gpo).ToString());
    }

    [Fact]
    public void Codes_EveryConstantHasADefinition()
    {
        var constants = typeof(GpoFindingCodes).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).Order().ToList();

        Assert.Equal(constants, GpoFindingCodes.All.Select(d => d.Code).Order());
        Assert.Equal(49, constants.Count);
        Assert.Equal(GpoFindingSeverity.Critical, GpoFindingCodes.Find(GpoFindingCodes.GppPassword).Severity);
        Assert.Throws<KeyNotFoundException>(() => GpoFindingCodes.Find("Nieznany"));
    }
}
