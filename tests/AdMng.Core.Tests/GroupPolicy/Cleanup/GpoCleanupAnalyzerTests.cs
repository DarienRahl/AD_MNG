using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Cleanup;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using static AdMng.Core.Tests.GroupPolicy.Cleanup.CleanupIndex;

namespace AdMng.Core.Tests.GroupPolicy.Cleanup;

/// <summary>The GPO-level rules, empty and single-setting GPOs of the cleanup advisor (21.6) on hand-built indexes.</summary>
public sealed class GpoCleanupAnalyzerTests
{
    private static GpoCleanupProposal Single(GpoCleanupResult result, GpoCleanupKind kind, Guid gpo) =>
        Assert.Single(result.Proposals, p => p.Kind == kind && p.GpoId == gpo);

    private static void None(GpoCleanupResult result, GpoCleanupKind kind, Guid gpo) =>
        Assert.DoesNotContain(result.Proposals, p => p.Kind == kind && p.GpoId == gpo);

    [Fact]
    public void EmptyIndex_NoProposals()
    {
        var result = new CleanupIndex().Analyze();

        Assert.Empty(result.Proposals);
        Assert.Empty(result.NotEvaluated);
        Assert.Equal("0 proposals, 0 not evaluated", result.ToString());
    }

    [Fact]
    public void Unlinked_OffersDeleteAndDisable_ForEveryReadStatus()
    {
        var index = new CleanupIndex();
        var read = index.Gpo("Stary – Łódź", Units.Value(Units.MinimumPasswordLength, "8"));
        var notRead = index.Gpo("Nieodczytany – Gdańsk");
        notRead.Status = GpoIndexStatus.NotRead;

        var result = index.Analyze();

        foreach (var gpo in new[] { read, notRead })
        {
            var proposal = Single(result, GpoCleanupKind.UnlinkedGpo, gpo.Id);
            Assert.Equal([GpoCleanupAction.Delete, GpoCleanupAction.Disable], proposal.Actions);
            Assert.Empty(proposal.Notes);
            Assert.Empty(proposal.ContainerDns);
        }

        Assert.Equal([notRead.Id], result.NotEvaluated);
    }

    [Fact]
    public void Unlinked_OtherDomains_AreNotSearched_AndDefaultGpo_IsNeverDeleted()
    {
        var index = new CleanupIndex();
        index.OtherDomains.Add("oddzial.contoso.test");
        var ddp = index.DefaultDomainPolicy(Units.Value(Units.MinimumPasswordLength, "14"));
        var disabled = index.Gpo("Wyłączony – Kraków");
        disabled.GpoStatus = GpoStatus.AllSettingsDisabled;

        var result = index.Analyze();

        var defaultProposal = Single(result, GpoCleanupKind.UnlinkedGpo, ddp.Id);
        Assert.Equal([GpoCleanupAction.Disable], defaultProposal.Actions);
        Assert.Equal([GpoCleanupNote.DefaultGpo, GpoCleanupNote.OtherDomainsNotSearched], defaultProposal.Notes);

        // Already disabled: no Disable action; its own GpoDisabled proposal offers Delete only (no links).
        Assert.Equal([GpoCleanupAction.Delete], Single(result, GpoCleanupKind.UnlinkedGpo, disabled.Id).Actions);
        Assert.Equal([GpoCleanupAction.Delete], Single(result, GpoCleanupKind.GpoDisabled, disabled.Id).Actions);
    }

    [Fact]
    public void AllLinksDisabled_UnlinkDisableDelete_WithTheContainers()
    {
        var index = new CleanupIndex();
        var gpo = index.Gpo("Kadry – Pulpit", Units.Registry("kadry.jpg"));
        var partly = index.Gpo("Księgowość – Pulpit", Units.Registry("ksiegowosc.jpg"));
        index.Link(Kadry, gpo, disabled: true).Link(Ksiegowosc, gpo, disabled: true);
        index.Link(Kadry, partly, disabled: true).Link(Ksiegowosc, partly);

        var result = index.Analyze();

        var proposal = Single(result, GpoCleanupKind.AllLinksDisabled, gpo.Id);
        Assert.Equal([GpoCleanupAction.Unlink, GpoCleanupAction.Disable, GpoCleanupAction.Delete], proposal.Actions);
        Assert.Equal([Kadry, Ksiegowosc], proposal.ContainerDns.Order(StringComparer.Ordinal));
        None(result, GpoCleanupKind.AllLinksDisabled, partly.Id);
        None(result, GpoCleanupKind.UnlinkedGpo, gpo.Id);
    }

    [Fact]
    public void GpoDisabled_DeleteAndUnlinkWhenLinked_PartlyDisabledIsNot()
    {
        var index = new CleanupIndex();
        var disabled = index.Gpo("Porządki – Wyłączony", Units.Registry("stary.jpg"));
        disabled.GpoStatus = GpoStatus.AllSettingsDisabled;
        var userOff = index.Gpo("Serwery – Bez użytkowników", Units.Value(Units.RemoteDesktopRight, "S-1-5-32-544"));
        userOff.GpoStatus = GpoStatus.UserSettingsDisabled;
        index.Link(Firma, disabled).Link(Serwery, userOff);

        var result = index.Analyze();

        var proposal = Single(result, GpoCleanupKind.GpoDisabled, disabled.Id);
        Assert.Equal([GpoCleanupAction.Delete, GpoCleanupAction.Unlink], proposal.Actions);
        Assert.Equal([Firma], proposal.ContainerDns);
        None(result, GpoCleanupKind.GpoDisabled, userOff.Id);
    }

    [Fact]
    public void EmptyGpo_LinkedOrNot_CommentsAndScriptFilesDoNotCount()
    {
        var index = new CleanupIndex();
        var linked = index.Gpo("Porządki – Pusty GPO");
        var commented = index.Gpo("Pusty z opisem – Żyrardów", Units.Comment(), Units.ScriptFile());
        index.Link(Firma, linked);

        var result = index.Analyze();

        var proposal = Single(result, GpoCleanupKind.EmptyGpo, linked.Id);
        Assert.Equal([GpoCleanupAction.Delete, GpoCleanupAction.Unlink], proposal.Actions);
        Assert.Equal([GpoCleanupNote.Linked], proposal.Notes);
        Assert.Equal([Firma], proposal.ContainerDns);

        var unlinked = Single(result, GpoCleanupKind.EmptyGpo, commented.Id);
        Assert.Equal([GpoCleanupAction.Delete], unlinked.Actions);
        Assert.Empty(unlinked.Notes);
    }

    [Fact]
    public void EmptyGpo_DefaultGpo_HasNoDelete()
    {
        var index = new CleanupIndex();
        var ddp = index.DefaultDomainPolicy();
        index.Link(Domain, ddp);

        var proposal = Single(index.Analyze(), GpoCleanupKind.EmptyGpo, ddp.Id);

        Assert.Equal([GpoCleanupAction.Unlink], proposal.Actions);
        Assert.Equal([GpoCleanupNote.Linked, GpoCleanupNote.DefaultGpo], proposal.Notes);
    }

    [Fact]
    public void EmptyGpo_Negatives()
    {
        var index = new CleanupIndex();
        var dormant = index.Gpo("Uśpiony – element wyłączony", Units.Drive(@"\\fs01\Kadry", "{A}") with { Disabled = true });
        var unreadable = index.Gpo("Nieczytelny – Registry.pol", Units.Unreadable());
        var otherFiles = index.Gpo("Przekierowanie folderów – Wrocław");
        otherFiles.OtherFiles.Add("User/Documents & Settings/fdeploy1.ini");
        var directory = index.Gpo("Instalacja oprogramowania – Poznań");
        directory.DirectorySettings = 2;
        var incomplete = index.Gpo("Za duży – Szczecin");
        incomplete.Status = GpoIndexStatus.Incomplete;
        var failed = index.Gpo("Brak dostępu – Lublin");
        failed.Status = GpoIndexStatus.AccessDenied;
        foreach (var gpo in new[] { dormant, unreadable, otherFiles, directory, incomplete, failed })
        {
            index.Link(Firma, gpo);
        }

        var result = index.Analyze();

        Assert.DoesNotContain(result.Proposals, p => p.Kind == GpoCleanupKind.EmptyGpo);
        Assert.Equal([failed.Id, incomplete.Id], result.NotEvaluated);
    }

    [Fact]
    public void SingleSetting_CopyToThenDisable_MergeIntoAGpoWithTheSameLinks()
    {
        var index = new CleanupIndex();
        var single = index.Gpo("Porządki – Pulpit zdalny", Units.Value(Units.RemoteDesktopRight, "S-1-5-32-555", kind: GpoSettingUnitKind.UserRight));
        var bigger = index.Gpo("Serwery – Inspekcja", Units.Value(Units.LogonAudit, "3", kind: GpoSettingUnitKind.AuditSubcategory),
            Units.Value("A|{0CCE923F-69AE-11D9-BED3-505054503030}", "1", kind: GpoSettingUnitKind.AuditSubcategory));
        var otherLinks = index.Gpo("Serwery – Hasła", Units.Value(Units.MinimumPasswordLength, "12"), Units.Value("K|Computer/Security/System Access/PasswordComplexity", "1"));
        var enforced = index.Gpo("Serwery – Wymuszone", Units.Value("K|Computer/Security/Event Audit/AuditLogonEvents", "3"), Units.Value("K|Computer/Security/Event Audit/AuditPrivilegeUse", "2"));
        index.Link(Serwery, bigger).Link(Serwery, single).Link(Serwery, otherLinks).Link(Firma, otherLinks).Link(Serwery, enforced, enforced: true);

        var result = index.Analyze();

        var proposal = Single(result, GpoCleanupKind.SingleSettingGpo, single.Id);
        Assert.Equal([GpoCleanupAction.CopyTo, GpoCleanupAction.Disable, GpoCleanupAction.Merge], proposal.Actions);
        Assert.Equal([bigger.Id], proposal.RelatedGpoIds);
        Assert.Equal(Units.RemoteDesktopRight, proposal.UnitBaseId);
        Assert.Equal([Serwery], proposal.ContainerDns);
        Assert.Empty(proposal.Notes);
        Assert.Empty(proposal.BlockingUnitIds);
    }

    [Fact]
    public void SingleSetting_MergeTargets_GetTheLinkOrderNotes()
    {
        // OU=Serwery: Pulpit zdalny (1), Inspekcja (2) – the single setting ranks above its target; and Inspekcja (1),
        // IT – Zabezpieczenia (2, same right), Pulpit zdalny B (3) – a GPO between sets the setting.
        var index = new CleanupIndex();
        var above = index.Gpo("Porządki – Pulpit zdalny", Units.Value(Units.RemoteDesktopRight, "S-1-5-32-555", kind: GpoSettingUnitKind.UserRight));
        var target = index.Gpo("Serwery – Inspekcja", Units.Value(Units.LogonAudit, "3", kind: GpoSettingUnitKind.AuditSubcategory),
            Units.Value("A|{0CCE923F-69AE-11D9-BED3-505054503030}", "1", kind: GpoSettingUnitKind.AuditSubcategory));
        index.Link(Serwery, above).Link(Serwery, target);
        var second = new CleanupIndex();
        var targetB = second.Gpo("Serwery – Inspekcja", Units.Value(Units.LogonAudit, "3", kind: GpoSettingUnitKind.AuditSubcategory),
            Units.Value("A|{0CCE923F-69AE-11D9-BED3-505054503030}", "1", kind: GpoSettingUnitKind.AuditSubcategory));
        var middle = second.Gpo("IT – Zabezpieczenia", Units.Value(Units.RemoteDesktopRight, "S-1-5-32-544", kind: GpoSettingUnitKind.UserRight),
            Units.Value(Units.MinimumPasswordLength, "12"));
        middle.SecurityDescriptor = Filter([AccountingGroupSid]);
        var below = second.Gpo("Porządki – Pulpit zdalny B", Units.Value(Units.RemoteDesktopRight, "S-1-5-32-555", kind: GpoSettingUnitKind.UserRight));
        second.Link(Serwery, targetB).Link(Serwery, middle).Link(Serwery, below);

        var ranksAbove = Single(index.Analyze(), GpoCleanupKind.SingleSettingGpo, above.Id);
        var linkedBetween = Single(second.Analyze(), GpoCleanupKind.SingleSettingGpo, below.Id);

        Assert.Contains(GpoCleanupAction.Merge, ranksAbove.Actions);
        Assert.Equal([GpoCleanupNote.LinkOrderDiffers], ranksAbove.Notes);
        Assert.Equal([targetB.Id], linkedBetween.RelatedGpoIds);
        Assert.Equal([GpoCleanupNote.LinkedBetween], linkedBetween.Notes);
    }

    [Fact]
    public void SingleSetting_NoMergeIntoATargetWhoseHalfOfTheSettingIsDisabled()
    {
        var index = new CleanupIndex();
        var single = index.Gpo("Porządki – Pulpit zdalny", Units.Value(Units.RemoteDesktopRight, "S-1-5-32-555", kind: GpoSettingUnitKind.UserRight));
        var userOnly = index.Gpo("Serwery – Tapety", Units.Registry("a.jpg"), Units.Registry("b.jpg") with { Id = "K|b", BaseId = "K|b" });
        userOnly.GpoStatus = GpoStatus.ComputerSettingsDisabled;
        index.Link(Serwery, single).Link(Serwery, userOnly);

        var proposal = Single(index.Analyze(), GpoCleanupKind.SingleSettingGpo, single.Id);

        Assert.Equal([GpoCleanupAction.CopyTo, GpoCleanupAction.Disable], proposal.Actions);
        Assert.Empty(proposal.RelatedGpoIds);
    }

    [Fact]
    public void SingleSetting_WithoutAGpoOfTheSameLinksOrFilters_HasNoMerge()
    {
        var index = new CleanupIndex();
        var single = index.Gpo("Księgowość – Tapeta", Units.Registry("ksiegowosc.jpg"));
        var filtered = index.Gpo("Księgowość – Dyski", Units.Drive(@"\\fs01\Księgowość", "{K}"), Units.Registry("inna.jpg") with { Id = "K|x", BaseId = "K|x" });
        filtered.SecurityDescriptor = Filter([AccountingGroupSid]);
        single.SecurityDescriptor = AuthenticatedUsers;
        index.Link(Ksiegowosc, single).Link(Ksiegowosc, filtered);
        var unlinked = index.Gpo("Bez powiązań – jedno ustawienie", Units.Registry("x.jpg"));
        var unlinkedTarget = index.Gpo("Bez powiązań – dwa ustawienia", Units.Registry("y.jpg"), Units.Value(Units.MinimumPasswordLength, "9"));

        var result = index.Analyze();

        var proposal = Single(result, GpoCleanupKind.SingleSettingGpo, single.Id);
        Assert.Equal([GpoCleanupAction.CopyTo, GpoCleanupAction.Disable], proposal.Actions);
        Assert.Empty(proposal.RelatedGpoIds);
        Assert.Empty(Single(result, GpoCleanupKind.SingleSettingGpo, unlinked.Id).RelatedGpoIds);
        None(result, GpoCleanupKind.SingleSettingGpo, unlinkedTarget.Id);
    }

    [Fact]
    public void SingleSetting_ARefusedUnit_NoCopyNoMerge()
    {
        var index = new CleanupIndex();
        var script = index.Gpo("Skrypt logowania – Opole", Units.Script());
        var target = index.Gpo("Stacje – Dwa ustawienia", Units.Registry("a.jpg"), Units.Value(Units.MinimumPasswordLength, "10"));
        index.Link(Firma, script).Link(Firma, target);

        var proposal = Single(index.Analyze(), GpoCleanupKind.SingleSettingGpo, script.Id);

        Assert.Equal([GpoCleanupAction.Disable], proposal.Actions);
        Assert.Equal([GpoCleanupNote.NonCopyableUnits], proposal.Notes);
        Assert.Equal([Units.Script().Id], proposal.BlockingUnitIds);
        Assert.Empty(proposal.RelatedGpoIds);
    }

    [Fact]
    public void SingleSetting_PartDisabled_NoMerge_ButCopy()
    {
        var index = new CleanupIndex();
        var single = index.Gpo("Użytkownicy – Tapeta (wyłączona część)", Units.Registry("a.jpg"));
        single.GpoStatus = GpoStatus.UserSettingsDisabled;
        var target = index.Gpo("Użytkownicy – Inne", Units.Registry("b.jpg") with { Id = "K|y", BaseId = "K|y" }, Units.Drive(@"\\fs01\Wspólne", "{W}"));
        index.Link(Firma, target).Link(Firma, single);

        var proposal = Single(index.Analyze(), GpoCleanupKind.SingleSettingGpo, single.Id);

        Assert.Equal([GpoCleanupAction.CopyTo, GpoCleanupAction.Disable], proposal.Actions);
        Assert.Equal([GpoCleanupNote.PartDisabled], proposal.Notes);
        Assert.Equal([target.Id], proposal.RelatedGpoIds);
    }

    [Fact]
    public void SingleSetting_Negatives()
    {
        var index = new CleanupIndex();
        var two = index.Gpo("Dwa ustawienia – Toruń", Units.Registry("a.jpg"), Units.Value(Units.MinimumPasswordLength, "8"));
        var withComment = index.Gpo("Jedno z opisem – Bydgoszcz", Units.Registry("b.jpg"), Units.Comment(), Units.ScriptFile());
        var directory = index.Gpo("Jedno + katalog – Kielce", Units.Registry("c.jpg"));
        directory.DirectorySettings = 1;
        var other = index.Gpo("Jedno + pliki – Rzeszów", Units.Registry("d.jpg"));
        other.OtherFiles.Add("Machine/Applications/app.aas");
        var unreadable = index.Gpo("Jedno + nieczytelny – Olsztyn", Units.Registry("e.jpg"), Units.Unreadable());
        var incomplete = index.Gpo("Jedno, niepełny odczyt – Białystok", Units.Registry("f.jpg"));
        incomplete.Status = GpoIndexStatus.Incomplete;
        var ddp = index.DefaultDomainPolicy(Units.Value(Units.MinimumPasswordLength, "14"));
        index.Link(Domain, ddp);

        var result = index.Analyze();

        Single(result, GpoCleanupKind.SingleSettingGpo, withComment.Id);
        foreach (var gpo in new[] { two.Id, directory.Id, other.Id, unreadable.Id, incomplete.Id, ddp.Id })
        {
            None(result, GpoCleanupKind.SingleSettingGpo, gpo);
        }
    }

    [Fact]
    public void Proposals_SortedByKindThenName_AndToStringHoldsNoValues()
    {
        var index = new CleanupIndex();
        var zabki = index.Gpo("Ząbki – pusty");
        var anin = index.Gpo("Anin – pusty");
        var lomianki = index.Gpo("Łomianki – pusty");

        var result = index.Analyze();

        Assert.Equal(
            [(GpoCleanupKind.EmptyGpo, anin.Id), (GpoCleanupKind.EmptyGpo, lomianki.Id), (GpoCleanupKind.EmptyGpo, zabki.Id),
             (GpoCleanupKind.UnlinkedGpo, anin.Id), (GpoCleanupKind.UnlinkedGpo, lomianki.Id), (GpoCleanupKind.UnlinkedGpo, zabki.Id)],
            result.Proposals.Select(p => (p.Kind, p.GpoId)));
        Assert.Equal("EmptyGpo " + SysvolPaths.FolderName(anin.Id), result.Proposals[0].ToString());
        Assert.Equal("6 proposals, 0 not evaluated", result.ToString());
    }

    [Fact]
    public void FiveHundredGpos_SharingSettings_AnalyzeQuickly()
    {
        // 500 GPOs × 20 units, ten of them equal in every GPO: coverage is evaluated once per GPO pair, not per setting.
        string[] containers = [Domain, Firma, Ksiegowosc, Kadry, Serwery];
        var index = new CleanupIndex();
        for (var i = 0; i < 500; i++)
        {
            var units = Enumerable.Range(0, 20).Select(u => u < 10
                ? Units.Value("K|Computer/Security/Registry Values/Wspólne" + u, "1")
                : Units.Value(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"K|Computer/Security/Registry Values/Własne{i}-{u}"), "1"));
            index.Link(containers[i % containers.Length], index.Gpo(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Oddział {i:D3} – zasady"), [.. units]));
        }

        var built = index.Build();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = GpoCleanupAnalyzer.Analyze(built);
        watch.Stop();

        Assert.NotEmpty(result.Proposals);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }
}
