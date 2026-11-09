using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Cleanup;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using static AdMng.Core.Tests.GroupPolicy.Cleanup.CleanupIndex;

namespace AdMng.Core.Tests.GroupPolicy.Cleanup;

/// <summary><see cref="GpoCleanupKind.DuplicateSetting"/> and <see cref="GpoCleanupKind.DifferentValues"/> (21.6) on hand-built indexes.</summary>
public sealed class GpoCleanupDuplicatesTests
{
    private static List<GpoCleanupProposal> Of(GpoCleanupResult result, GpoCleanupKind kind) => [.. result.Proposals.Where(p => p.Kind == kind)];

    /// <summary>The same wallpaper in a GPO on the domain head and in one on OU=Księgowość (below it).</summary>
    private static (CleanupIndex Index, Draft Head, Draft Ou) HeadAndOu(string headValue = "firma.jpg", string ouValue = "firma.jpg")
    {
        var index = new CleanupIndex();
        var head = index.Gpo("Firma – Pulpit", Units.Registry(headValue));
        var ou = index.Gpo("Księgowość – Pulpit", Units.Registry(ouValue));
        index.Link(Domain, head).Link(Ksiegowosc, ou);
        return (index, head, ou);
    }

    [Fact]
    public void Duplicate_TheDomainHeadCoversTheOu_RemoveFromTheOuGpo()
    {
        var (index, head, ou) = HeadAndOu();

        var result = index.Analyze();

        var proposal = Assert.Single(Of(result, GpoCleanupKind.DuplicateSetting));
        Assert.Equal(ou.Id, proposal.GpoId);
        Assert.Equal([head.Id], proposal.RelatedGpoIds);
        Assert.Equal(Units.Wallpaper, proposal.UnitBaseId);
        Assert.Equal([GpoCleanupAction.RemoveSetting], proposal.Actions);
        Assert.Empty(proposal.Notes);
        Assert.Empty(Of(result, GpoCleanupKind.DifferentValues));
    }

    [Fact]
    public void Duplicate_DisjointOus_NoProposal()
    {
        var index = new CleanupIndex();
        var accounting = index.Gpo("Porządki – Dyski (Księgowość)", Units.Drive(@"\\fs01\Wspólne", "{11111111-0000-0000-0000-000000000001}"));
        var hr = index.Gpo("Porządki – Dyski (Kadry)", Units.Drive(@"\\fs01\Wspólne", "{22222222-0000-0000-0000-000000000002}"));
        index.Link(Ksiegowosc, accounting).Link(Kadry, hr);

        var result = index.Analyze();

        Assert.Empty(Of(result, GpoCleanupKind.DuplicateSetting));
        Assert.Empty(Of(result, GpoCleanupKind.DifferentValues));
    }

    [Fact]
    public void Duplicate_TheOtherOccurrenceDoesNotApply_NoProposal()
    {
        var index = new CleanupIndex();
        var ou = index.Gpo("Księgowość – Pulpit", Units.Registry("firma.jpg"));
        var unlinked = index.Gpo("Porządki – Bez powiązań", Units.Registry("firma.jpg"));
        var linkDisabled = index.Gpo("Firma – Wyłączone połączenie", Units.Registry("firma.jpg"));
        var userOff = index.Gpo("Firma – Bez części użytkownika", Units.Registry("firma.jpg"));
        userOff.GpoStatus = GpoStatus.UserSettingsDisabled;
        var gpoOff = index.Gpo("Firma – Wyłączony", Units.Registry("firma.jpg"));
        gpoOff.GpoStatus = GpoStatus.AllSettingsDisabled;
        index.Link(Ksiegowosc, ou).Link(Domain, linkDisabled, disabled: true).Link(Domain, userOff).Link(Domain, gpoOff);

        var result = index.Analyze();

        Assert.Empty(Of(result, GpoCleanupKind.DuplicateSetting));
        Assert.Empty(Of(result, GpoCleanupKind.DifferentValues));
    }

    [Fact]
    public void Duplicate_AThirdGpoWithAnotherValue_NoDuplicate_ButDifferentValues()
    {
        var (index, head, ou) = HeadAndOu();
        var hr = index.Gpo("Kadry – Pulpit", Units.Registry("kadry.jpg"));
        index.Link(Kadry, hr);

        var result = index.Analyze();

        Assert.Empty(Of(result, GpoCleanupKind.DuplicateSetting));
        var different = Assert.Single(Of(result, GpoCleanupKind.DifferentValues));
        Assert.Equal(head.Id, different.GpoId);
        Assert.Equal([hr.Id, ou.Id], different.RelatedGpoIds);
        Assert.Equal(Units.Wallpaper, different.UnitBaseId);
        Assert.Empty(different.Actions);

        Assert.Empty(Of(index.Analyze(new GpoCleanupOptions { IncludeDifferentValues = false }), GpoCleanupKind.DifferentValues));
    }

    [Theory]
    [InlineData(GpoUnitComparability.Masked)]
    [InlineData(GpoUnitComparability.Conditional)]
    [InlineData(GpoUnitComparability.Ambiguous)]
    [InlineData(GpoUnitComparability.Truncated)]
    public void Duplicate_ANonComparableOccurrence_BlocksTheBaseId(GpoUnitComparability comparability)
    {
        var (index, _, _) = HeadAndOu();
        var third = index.Gpo("Kadry – Pulpit", Units.Registry("firma.jpg") with { Comparability = comparability });
        index.Link(Kadry, third);

        var result = index.Analyze();

        Assert.Empty(Of(result, GpoCleanupKind.DuplicateSetting));
        Assert.Empty(Of(result, GpoCleanupKind.DifferentValues));
    }

    [Fact]
    public void Duplicate_MaskedConditionalAndScripts_NeverDuplicates()
    {
        var index = new CleanupIndex();
        var head = index.Gpo("Firma – Ukryte", Units.Registry("") with { Comparability = GpoUnitComparability.Masked, Refusal = GpoUnitRefusal.Secret },
            Units.Drive(@"\\fs01\Wspólne", "{H}") with { Comparability = GpoUnitComparability.Conditional, FiltersFingerprint = "group=GG_Kadry" },
            Units.Script());
        var ou = index.Gpo("Księgowość – Ukryte", Units.Registry("") with { Comparability = GpoUnitComparability.Masked, Refusal = GpoUnitRefusal.Secret },
            Units.Drive(@"\\fs01\Wspólne", "{O}") with { Comparability = GpoUnitComparability.Conditional, FiltersFingerprint = "group=GG_Kadry" },
            Units.Script());
        index.Link(Domain, head).Link(Ksiegowosc, ou);

        var result = index.Analyze();

        Assert.Empty(Of(result, GpoCleanupKind.DuplicateSetting));
        Assert.Empty(Of(result, GpoCleanupKind.DifferentValues));
    }

    [Fact]
    public void Duplicate_ADisabledItemWithAnotherValue_DoesNotBlock()
    {
        var index = new CleanupIndex();
        var head = index.Gpo("Firma – Dyski", Units.Drive(@"\\fs01\Wspólne", "{H}"));
        var ou = index.Gpo("Księgowość – Dyski", Units.Drive(@"\\fs01\Wspólne", "{O}"));
        var dormant = index.Gpo("Kadry – Dyski (uśpione)", Units.Drive(@"\\fs01\Kadry", "{D}") with { Disabled = true });
        var disabledSame = index.Gpo("Serwery – Dyski (uśpione)", Units.Drive(@"\\fs01\Wspólne", "{S}") with { Disabled = true });
        index.Link(Domain, head).Link(Ksiegowosc, ou).Link(Kadry, dormant).Link(Serwery, disabledSame);

        var proposal = Assert.Single(Of(index.Analyze(), GpoCleanupKind.DuplicateSetting));

        Assert.Equal(ou.Id, proposal.GpoId);
        Assert.Equal([head.Id], proposal.RelatedGpoIds);
    }

    [Fact]
    public void Duplicate_AnIncompleteGpoAppliesTheSetting_NoProposal()
    {
        var (index, _, _) = HeadAndOu();
        var incomplete = index.Gpo("Kadry – Pulpit (za duży)", Units.Registry("firma.jpg"));
        incomplete.Status = GpoIndexStatus.Incomplete;
        index.Link(Kadry, incomplete);

        Assert.Empty(Of(index.Analyze(), GpoCleanupKind.DuplicateSetting));
    }

    [Fact]
    public void Duplicate_TwoOccurrencesInOneGpo_NoProposal()
    {
        var (index, _, ou) = HeadAndOu();
        ou.Units.Add(Units.Second(Units.Registry("firma.jpg")));

        Assert.Empty(Of(index.Analyze(), GpoCleanupKind.DuplicateSetting));
    }

    [Fact]
    public void Duplicate_MutualCoverage_OnlyOneSideIsRemoved_TheGpoWithMoreSettingsKeepsIt()
    {
        var index = new CleanupIndex();
        var small = index.Gpo("Firma – Pulpit", Units.Registry("firma.jpg"));
        var large = index.Gpo("Firma – Ustawienia ogólne", Units.Registry("firma.jpg"), Units.Value(Units.RemoteDesktopRight, "S-1-5-32-555", kind: GpoSettingUnitKind.UserRight));
        index.Link(Domain, small).Link(Domain, large);

        var proposal = Assert.Single(Of(index.Analyze(), GpoCleanupKind.DuplicateSetting));

        Assert.Equal(small.Id, proposal.GpoId);
        Assert.Equal([large.Id], proposal.RelatedGpoIds);
    }

    [Fact]
    public void Duplicate_ThreeEqualGpos_TheTopRankedIsNeverRemoved_AndEveryKeeperIsListed()
    {
        var index = new CleanupIndex();
        var head = index.Gpo("Firma – Ustawienia ogólne", Units.Registry("firma.jpg"), Units.Value(Units.MinimumPasswordLength, "8"));
        var firma = index.Gpo("Firma – Pulpit", Units.Registry("firma.jpg"));
        var ou = index.Gpo("Księgowość – Pulpit", Units.Registry("firma.jpg"));
        index.Link(Domain, head).Link(Firma, firma).Link(Ksiegowosc, ou);

        var proposals = Of(index.Analyze(), GpoCleanupKind.DuplicateSetting);

        Assert.Equal(new[] { firma.Id, ou.Id }.Order(), proposals.Select(p => p.GpoId).Order());
        Assert.Equal([head.Id], proposals.Single(p => p.GpoId == firma.Id).RelatedGpoIds);
        Assert.Equal([head.Id, firma.Id], proposals.Single(p => p.GpoId == ou.Id).RelatedGpoIds);
        Assert.DoesNotContain(proposals, p => p.GpoId == head.Id);
    }

    [Fact]
    public void Duplicate_BlockInheritance_StopsCoverage_UnlessTheLinkIsEnforced()
    {
        var (index, _, _) = HeadAndOu();
        index.BlockInheritance(Firma);
        Assert.Empty(Of(index.Analyze(), GpoCleanupKind.DuplicateSetting));

        var enforced = new CleanupIndex();
        var enforcedHead = enforced.Gpo("Firma – Pulpit", Units.Registry("firma.jpg"));
        var enforcedOu = enforced.Gpo("Księgowość – Pulpit", Units.Registry("firma.jpg"));
        enforced.Link(Domain, enforcedHead, enforced: true).Link(Ksiegowosc, enforcedOu).BlockInheritance(Firma);

        var proposal = Assert.Single(Of(enforced.Analyze(), GpoCleanupKind.DuplicateSetting));
        Assert.Equal(enforcedOu.Id, proposal.GpoId);
        Assert.Equal([enforcedHead.Id], proposal.RelatedGpoIds);
    }

    [Fact]
    public void Duplicate_SiteLinks_CoverOnlyWhereTheKeeperApplies()
    {
        // The covered GPO is linked to the site only: the domain head GPO applies wherever it does.
        var index = new CleanupIndex();
        var head = index.Gpo("Firma – Pulpit", Units.Registry("firma.jpg"));
        var site = index.Gpo("Lokacja Warszawa – Pulpit", Units.Registry("firma.jpg"));
        index.Link(Domain, head).Link(Warszawa, site);

        var proposal = Assert.Single(Of(index.Analyze(), GpoCleanupKind.DuplicateSetting));
        Assert.Equal(site.Id, proposal.GpoId);
        Assert.Equal([head.Id], proposal.RelatedGpoIds);

        // The other way round: the site GPO does not reach objects of other sites, so it covers nothing.
        var reverse = new CleanupIndex();
        var siteKeeper = reverse.Gpo("Lokacja Warszawa – Ustawienia", Units.Registry("firma.jpg"), Units.Value(Units.MinimumPasswordLength, "8"));
        var ou = reverse.Gpo("Księgowość – Pulpit", Units.Registry("firma.jpg"));
        reverse.Link(Warszawa, siteKeeper).Link(Ksiegowosc, ou);
        Assert.Empty(Of(reverse.Analyze(), GpoCleanupKind.DuplicateSetting));
    }

    [Fact]
    public void Duplicate_SecurityAndWmiFilters_TheKeeperMustReachEveryoneTheCoveredGpoReaches()
    {
        // Keeper filtered to GG_Księgowość, covered GPO for Authenticated Users: not covered.
        var (narrow, narrowHead, narrowOu) = HeadAndOu();
        narrowHead.SecurityDescriptor = Filter([AccountingGroupSid]);
        narrowOu.SecurityDescriptor = AuthenticatedUsers;
        Assert.Empty(Of(narrow.Analyze(), GpoCleanupKind.DuplicateSetting));

        // Keeper for Authenticated Users, covered GPO filtered: covered.
        var (wide, wideHead, wideOu) = HeadAndOu();
        wideHead.SecurityDescriptor = AuthenticatedUsers;
        wideOu.SecurityDescriptor = Filter([AccountingGroupSid]);
        Assert.Equal(wideOu.Id, Assert.Single(Of(wide.Analyze(), GpoCleanupKind.DuplicateSetting)).GpoId);

        // Keeper with a deny: not covered.
        var (deny, denyHead, denyOu) = HeadAndOu();
        denyHead.SecurityDescriptor = Filter([Core.GroupPolicy.GroupPolicySecurityFilter.AuthenticatedUsersSid], AccountingGroupSid);
        denyOu.SecurityDescriptor = AuthenticatedUsers;
        Assert.Empty(Of(deny.Analyze(), GpoCleanupKind.DuplicateSetting));

        // Keeper with a WMI filter the covered GPO does not have: not covered.
        var (wmi, wmiHead, _) = HeadAndOu();
        wmiHead.WmiFilterId = "{8E0A3C12-6B4D-4F0E-9C1A-2B3D4E5F6A7B}";
        Assert.Empty(Of(wmi.Analyze(), GpoCleanupKind.DuplicateSetting));
    }

    [Fact]
    public void Duplicate_AccountPolicy_OnlyAmongGposOnTheDomainHead()
    {
        var index = new CleanupIndex();
        var ddp = index.DefaultDomainPolicy(Units.Value(Units.MinimumPasswordLength, "12"), Units.Value(Units.MaxTicketAge, "10"));
        var passwords = index.Gpo("Firma – Hasła", Units.Value(Units.MinimumPasswordLength, "12"), Units.Value(Units.MaxTicketAge, "10"),
            Units.Value("K|Computer/Security/System Access/LockoutBadCount", "5"));
        index.Link(Domain, passwords).Link(Domain, ddp);

        var proposals = Of(index.Analyze(), GpoCleanupKind.DuplicateSetting);

        // The default domain policy keeps account policy even though the other GPO holds more settings.
        Assert.Equal([Units.MaxTicketAge, Units.MinimumPasswordLength], proposals.Select(p => p.UnitBaseId).Order(StringComparer.Ordinal));
        Assert.All(proposals, p =>
        {
            Assert.Equal(passwords.Id, p.GpoId);
            Assert.Equal([ddp.Id], p.RelatedGpoIds);
            Assert.Equal([GpoCleanupNote.AccountPolicy], p.Notes);
        });
    }

    [Fact]
    public void Duplicate_AccountPolicyBelowTheDomainHead_IsLeftAlone()
    {
        var index = new CleanupIndex();
        var ddp = index.DefaultDomainPolicy(Units.Value(Units.MinimumPasswordLength, "12"));
        var ou = index.Gpo("Księgowość – Hasła lokalne", Units.Value(Units.MinimumPasswordLength, "12"));
        var other = index.Gpo("Kadry – Hasła lokalne", Units.Value(Units.MinimumPasswordLength, "12"));
        var differentOu = index.Gpo("Serwery – Hasła lokalne", Units.Value(Units.MinimumPasswordLength, "16"));
        index.Link(Domain, ddp).Link(Ksiegowosc, ou).Link(Firma, other).Link(Serwery, differentOu);

        var result = index.Analyze();

        Assert.Empty(Of(result, GpoCleanupKind.DuplicateSetting));
        Assert.Empty(Of(result, GpoCleanupKind.DifferentValues));
    }

    [Fact]
    public void Duplicate_ASecurityOptionInSystemAccess_IsNotAccountPolicy()
    {
        const string guest = "K|Computer/Security/System Access/EnableGuestAccount";
        var index = new CleanupIndex();
        var head = index.Gpo("Firma – Konto gościa", Units.Value(guest, "0"));
        var ou = index.Gpo("Księgowość – Konto gościa", Units.Value(guest, "0"));
        index.Link(Domain, head).Link(Ksiegowosc, ou);

        var proposal = Assert.Single(Of(index.Analyze(), GpoCleanupKind.DuplicateSetting));

        Assert.Equal(ou.Id, proposal.GpoId);
        Assert.Empty(proposal.Notes);
    }

    [Fact]
    public void Duplicate_RemovePolicyItem_GetsTheNote()
    {
        var index = new CleanupIndex();
        var head = index.Gpo("Firma – Dyski", Units.Drive(@"\\fs01\Wspólne", "{H}"));
        var ou = index.Gpo("Księgowość – Dyski", Units.Drive(@"\\fs01\Wspólne", "{O}") with { RemovePolicy = true });
        index.Link(Domain, head).Link(Ksiegowosc, ou);

        var proposal = Assert.Single(Of(index.Analyze(), GpoCleanupKind.DuplicateSetting));

        Assert.Equal([GpoCleanupNote.RemovePolicy], proposal.Notes);
        Assert.Equal([GpoCleanupAction.RemoveSetting], proposal.Actions);
    }

    [Fact]
    public void Duplicate_ARefusedComparableUnit_IsReportedWithoutRemoveSetting()
    {
        const string sacl = "K|Computer/Audit/Option:CrashOnAuditFail";
        var index = new CleanupIndex();
        var head = index.Gpo("Firma – Inspekcja", Units.Value(sacl, "0", kind: GpoSettingUnitKind.AuditOption) with { Refusal = GpoUnitRefusal.AuditOption });
        var ou = index.Gpo("Księgowość – Inspekcja", Units.Value(sacl, "0", kind: GpoSettingUnitKind.AuditOption) with { Refusal = GpoUnitRefusal.AuditOption });
        index.Link(Domain, head).Link(Ksiegowosc, ou);

        var proposal = Assert.Single(Of(index.Analyze(), GpoCleanupKind.DuplicateSetting));

        Assert.Equal(ou.Id, proposal.GpoId);
        Assert.Empty(proposal.Actions);
        Assert.Equal([GpoCleanupNote.NonCopyableUnits], proposal.Notes);
        Assert.Equal([sacl], proposal.BlockingUnitIds);
    }

    [Fact]
    public void Policies_CompareBySetting_NotByReference()
    {
        var index = new CleanupIndex();
        var head = index.Gpo("Firma – Podgląd", Units.Policy(PolicyState.Enabled));
        var ou = index.Gpo("Księgowość – Podgląd", Units.Policy(PolicyState.Enabled));
        var hr = index.Gpo("Kadry – Podgląd", Units.Policy(PolicyState.Disabled));
        index.Link(Domain, head).Link(Ksiegowosc, ou);

        var proposal = Assert.Single(Of(index.Analyze(), GpoCleanupKind.DuplicateSetting));
        Assert.Equal(ou.Id, proposal.GpoId);
        Assert.Equal(Units.NoPreview, proposal.UnitBaseId);

        index.Link(Kadry, hr);
        var result = index.Analyze();
        Assert.Empty(Of(result, GpoCleanupKind.DuplicateSetting));
        Assert.Equal(Units.NoPreview, Assert.Single(Of(result, GpoCleanupKind.DifferentValues)).UnitBaseId);
    }

    [Fact]
    public void Comments_AreNeverDuplicates()
    {
        var index = new CleanupIndex();
        var head = index.Gpo("Firma – Pulpit", Units.Registry("firma.jpg"), Units.Comment());
        var ou = index.Gpo("Księgowość – Inne", Units.Registry("inne.jpg") with { Id = "K|z", BaseId = "K|z" }, Units.Comment());
        index.Link(Domain, head).Link(Ksiegowosc, ou);

        Assert.Empty(Of(index.Analyze(), GpoCleanupKind.DuplicateSetting));
    }
}
