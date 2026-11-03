using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Testing;
using static AdMng.Core.Tests.GroupPolicy.Diagnostics.DiagnosticsSnapshotBuilder;

namespace AdMng.Core.Tests.GroupPolicy.Diagnostics;

/// <summary>Security filtering, permissions, WMI filters and the folder path (Quick, except the SID resolution).</summary>
public sealed class FilteringRulesTests
{
    private const int Read = 0x00020094;
    private const int Edit = 0x000200BF;

    private static FakeAce Allow(string sid, int mask) => new(FakeAce.AccessAllowed, 0x02, mask, sid);

    private static FakeAce Apply(string sid) => new(FakeAce.AccessAllowedObject, 0x02, 0x100, sid, GroupPolicySecurityFilter.ApplyGroupPolicyRight);

    private static FakeAce DenyApply(string sid) => new(FakeAce.AccessDeniedObject, 0x02, 0x100, sid, GroupPolicySecurityFilter.ApplyGroupPolicyRight);

    [Fact]
    public void NoApplyRight_ALinkedGpoNobodyApplies_NotWhenSomeoneStillApplies()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var nobody = builder.Gpo("Księgowość – Bez filtrowania", descriptor: Descriptor(Allow(DomainAdmins, 0xF00FF), Allow("S-1-5-11", Read)));
        var denied = builder.Gpo("Księgowość – Zabronione", descriptor: Descriptor(Allow("S-1-5-11", Read), Apply(GroupKsiegowosc), DenyApply(GroupKsiegowosc)));
        var filtered = builder.Gpo("Księgowość – Tylko grupa", descriptor: Descriptor(Allow("S-1-5-11", Read), Apply(GroupKsiegowosc)));
        builder.Link(Ou("Księgowość"), nobody).Link(Ou("Księgowość"), denied).Link(Ou("Księgowość"), filtered);

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.NoApplyRight);

        Assert.Equal(new[] { denied, nobody }.Order(), findings.Select(f => f.GpoId!.Value).Order());
        Assert.All(findings, f => Assert.Equal((GpoFindingSeverity.High, GpoFindingArea.Filtering), (f.Severity, f.Area)));
    }

    [Fact]
    public void Ms16072_UserSettingsComputersCannotRead_OffersAuthenticatedUsersRead()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var broken = builder.Gpo("Kadry – Mapowanie dysków", user: 2, computer: 0,
            descriptor: Descriptor(Allow(GroupKsiegowosc, Read), Apply(GroupKsiegowosc)));
        var fixedByComputers = builder.Gpo("Kadry – Drukarki", user: 2, computer: 0,
            descriptor: Descriptor(Allow(GroupKsiegowosc, Read), Apply(GroupKsiegowosc), Allow(DomainComputers, Read)));
        var computerOnly = builder.Gpo("Kadry – Zapora", user: 0, computer: 2,
            descriptor: Descriptor(Allow(GroupKsiegowosc, Read), Apply(GroupKsiegowosc)));
        builder.Link(Ou("Kadry"), broken).Link(Ou("Kadry"), fixedByComputers).Link(Ou("Kadry"), computerOnly);

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.Ms16072ComputersCannotRead);

        Assert.Equal(broken, finding.GpoId);
        Assert.Equal(new GpoQuickFix.GrantRead(broken, "S-1-5-11"), finding.Fix);
    }

    [Fact]
    public void Ms16072_AGpoLinkedOnlyToTheDomainControllers_IsJudgedWithTheirToken()
    {
        // Tier 0 user settings applied on the DCs (loopback): Authenticated Users removed, the DCs keep their Read.
        var builder = new DiagnosticsSnapshotBuilder();
        var tier0 = Descriptor(Allow(GroupSerwisanci, Read), Apply(GroupSerwisanci), Allow("S-1-5-9", Read));
        var byEnterpriseDcs = builder.Gpo("Tier 0 – Pulpit administratorów", user: 2, computer: 1, descriptor: tier0);
        var byDomainControllers = builder.Gpo("Tier 0 – Skróty", user: 2, computer: 1,
            descriptor: Descriptor(Allow(GroupSerwisanci, Read), Apply(GroupSerwisanci), Allow(DomainSid + "-516", Read)));
        var unreadable = builder.Gpo("Tier 0 – Bez odczytu", user: 2, computer: 1, descriptor: Descriptor(Allow(GroupSerwisanci, Read), Apply(GroupSerwisanci)));
        var onServers = builder.Gpo("Serwery – Pulpit administratorów", user: 2, computer: 1, descriptor: tier0);
        builder.Link(DomainControllersOu, byEnterpriseDcs).Link(DomainControllersOu, byDomainControllers).Link(DomainControllersOu, unreadable)
            .Link(Ou("Serwery"), onServers);

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.Ms16072ComputersCannotRead);

        Assert.Equal(new[] { unreadable, onServers }.Order(), findings.Select(f => f.GpoId!.Value).Order());
    }

    [Fact]
    public void Ms16072_NotForAUserPartWhoseSettingsWereAllRemoved()
    {
        // Versions only grow: the user part had settings once (version 2); the folder holds none any more.
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Kadry – Zapora", user: 2, computer: 3, descriptor: Descriptor(Allow(GroupKsiegowosc, Read), Apply(GroupKsiegowosc)));
        builder.Link(Ou("Kadry"), gpo).Folder(gpo, RegistryPol(user: true), RegistryPol(false, (@"Software\Policies\Microsoft\WindowsFirewall\DomainProfile", "EnableFirewall", 1)));

        Findings.None(builder.Analyze(), GpoFindingCodes.Ms16072ComputersCannotRead);

        // Quick (no folders): the version is all there is.
        builder.Depth = GpoDiagnosticsDepth.Quick;
        Findings.Single(builder.Analyze(), GpoFindingCodes.Ms16072ComputersCannotRead, gpo);
    }

    [Fact]
    public void WmiFilterMissing_OffersToClearTheLink_WmiFilterUnused_OffersToDeleteTheFilter()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        builder.WmiFilter("{11111111-2222-4333-8444-555555555555}", "Tylko laptopy").WmiFilter("{66666666-7777-4888-8999-AAAAAAAAAAAA}", "Tylko serwery");
        var missing = builder.Gpo("Laptopy – Zasilanie", wmiFilterId: "{0F0F0F0F-0F0F-4F0F-8F0F-0F0F0F0F0F0F}");
        var uses = builder.Gpo("Laptopy – VPN", wmiFilterId: "{11111111-2222-4333-8444-555555555555}");
        builder.Link(Ou("Laptopy"), missing).Link(Ou("Laptopy"), uses);

        var findings = builder.Analyze();

        var finding = Findings.Single(findings, GpoFindingCodes.WmiFilterMissing);
        Assert.Equal((missing, "{0F0F0F0F-0F0F-4F0F-8F0F-0F0F0F0F0F0F}"), (finding.GpoId, finding.Arguments[0]));
        Assert.Equal(new GpoQuickFix.ClearWmiFilter(missing), finding.Fix);
        var unused = Findings.Single(findings, GpoFindingCodes.WmiFilterUnused);
        Assert.Equal(["Tylko serwery", "{66666666-7777-4888-8999-AAAAAAAAAAAA}"], unused.Arguments);
        Assert.Equal(new GpoQuickFix.DeleteWmiFilter($"CN={{66666666-7777-4888-8999-AAAAAAAAAAAA}},CN=SOM,CN=WMIPolicy,CN=System,{DomainDn}"), unused.Fix);
        Assert.Null(unused.GpoId);
    }

    [Fact]
    public void DenyInSecurityFiltering_OneFindingPerDeniedTrustee()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Sprzedaż – Wyjątki", descriptor: DefaultDescriptor(DenyApply(GroupSerwisanci), DenyApply(GroupSerwisanci), DenyApply(GroupKsiegowosc)));
        var plain = builder.Gpo("Sprzedaż – Zwykła");
        builder.Link(Ou("Sprzedaż"), gpo).Link(Ou("Sprzedaż"), plain);

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.DenyInSecurityFiltering);

        Assert.Equal([GroupSerwisanci, GroupKsiegowosc], findings.Select(f => f.Arguments[0]).Order(StringComparer.Ordinal));
        Assert.All(findings, f => Assert.Equal((gpo, GpoFindingSeverity.Info), (f.GpoId!.Value, f.Severity)));
    }

    [Fact]
    public void OrphanedTrustee_AnUnresolvedDomainSid_RemovableWhenExplicit_BuiltInRidsAndOtherDomainsNever()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Serwis – Delegowanie", descriptor: DefaultDescriptor(
            Allow(Deleted, Edit),
            Allow(GroupSerwisanci, Edit),
            new FakeAce(FakeAce.AccessAllowed, 0x12, Read, DomainSid + "-2001"),
            Allow("S-1-5-21-99-98-97-1500", Read),
            Allow(DomainSid + "-526", Read)));
        builder.Link(Ou("Serwis"), gpo).Existing(GroupSerwisanci);

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.OrphanedTrustee);

        Assert.Equal([Deleted, DomainSid + "-2001"], findings.Select(f => f.Arguments[0]).Order(StringComparer.Ordinal));
        Assert.Equal(new GpoQuickFix.RemoveTrustee(gpo, Deleted), findings.Single(f => f.Arguments[0] == Deleted).Fix);
        Assert.Null(findings.Single(f => f.Arguments[0] != Deleted).Fix); // inherited only: nothing to remove on the GPO
    }

    [Fact]
    public void OrphanedTrustee_NotEvaluatedInQuick()
    {
        var builder = new DiagnosticsSnapshotBuilder { Depth = GpoDiagnosticsDepth.Quick };
        var gpo = builder.Gpo("Serwis – Delegowanie", descriptor: DefaultDescriptor(Allow(Deleted, Edit)));
        builder.Link(Ou("Serwis"), gpo);

        var result = builder.Analyze();

        Findings.None(result, GpoFindingCodes.OrphanedTrustee);
        Assert.Contains(GpoRuleGroup.Principals, result.NotEvaluated);
    }

    [Theory]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-11")]
    [InlineData(DomainUsers)]
    [InlineData(DomainComputers)]
    public void BroadEditRights_ABroadGroupMayEdit(string broad)
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Wszyscy – Edycja", descriptor: DefaultDescriptor(Allow(broad, Edit)));
        var safe = builder.Gpo("Serwisanci – Edycja", descriptor: DefaultDescriptor(Allow(GroupSerwisanci, Edit)));
        builder.Link(Ou("Firma"), gpo).Link(Ou("Firma"), safe);

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.BroadEditRights);

        Assert.Equal((gpo, broad, GpoFindingArea.Security), (finding.GpoId!.Value, finding.Arguments[0], finding.Area));
    }

    [Fact]
    public void PathRedirected_APathOutsideTheDomainsSysvol_NotForTheSamePathInOtherCase()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var redirected = builder.Gpo("Oddział – Przekierowana", fileSysPath: @"\\fs01.contoso.test\Zasady\{ABCDEF01-2345-4678-9ABC-DEF012345678}");
        var sambaId = Guid.Parse("5C1D2E3F-4A5B-4C6D-8E7F-901A2B3C4D5E");
        builder.Gpo("Oddział – Samba", id: sambaId, fileSysPath: @"\\contoso.test\sysvol\contoso.test\Policies\{5c1d2e3f-4a5b-4c6d-8e7f-901a2b3c4d5e}\");
        builder.Link(Ou("Oddział"), redirected).Link(Ou("Oddział"), sambaId);

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.PathRedirected);

        Assert.Equal((redirected, GpoFindingSeverity.High), (finding.GpoId, finding.Severity));
        Assert.Equal(@"\\fs01.contoso.test\Zasady\{ABCDEF01-2345-4678-9ABC-DEF012345678}", finding.Arguments[0]);
    }
}
