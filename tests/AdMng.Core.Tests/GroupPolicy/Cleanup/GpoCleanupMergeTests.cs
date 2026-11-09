using AdMng.Core.GroupPolicy.Cleanup;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using static AdMng.Core.Tests.GroupPolicy.Cleanup.CleanupIndex;

namespace AdMng.Core.Tests.GroupPolicy.Cleanup;

/// <summary><see cref="GpoCleanupKind.MergeCandidate"/> (21.6) on hand-built indexes.</summary>
public sealed class GpoCleanupMergeTests
{
    private static List<GpoCleanupProposal> Merges(GpoCleanupResult result) => [.. result.Proposals.Where(p => p.Kind == GpoCleanupKind.MergeCandidate)];

    private static GpoSettingUnit Audit(string guid, string value) => Units.Value("A|{" + guid + "}", value, kind: GpoSettingUnitKind.AuditSubcategory);

    /// <summary>"Pulpit zdalny" (one right) and "Inspekcja" (two subcategories) on OU=Serwery, both for Authenticated Users.</summary>
    private static (CleanupIndex Index, Draft Remote, Draft Audit) Pair()
    {
        var index = new CleanupIndex();
        var remote = index.Gpo("Porządki – Pulpit zdalny", Units.Value(Units.RemoteDesktopRight, "S-1-5-32-555", kind: GpoSettingUnitKind.UserRight));
        var audit = index.Gpo("Porządki – Inspekcja", Audit("0CCE9215-69AE-11D9-BED3-505054503030", "3"), Audit("0CCE923F-69AE-11D9-BED3-505054503030", "1"));
        remote.SecurityDescriptor = AuthenticatedUsers;
        audit.SecurityDescriptor = AuthenticatedUsers;
        index.Link(Serwery, audit).Link(Serwery, remote);
        return (index, remote, audit);
    }

    [Fact]
    public void Merge_TheSameLinksAndFilters_IntoTheGpoWithTheMostUnits()
    {
        var (index, remote, audit) = Pair();

        var proposal = Assert.Single(Merges(index.Analyze()));

        Assert.Equal(remote.Id, proposal.GpoId);
        Assert.Equal([audit.Id], proposal.RelatedGpoIds);
        Assert.Equal([GpoCleanupAction.Merge], proposal.Actions);
        Assert.Equal([Serwery], proposal.ContainerDns);
        Assert.Empty(proposal.Notes);
        Assert.Empty(proposal.BlockingUnitIds);
    }

    [Fact]
    public void Merge_TheSourceRanksAboveTheTarget_LinkOrderDiffers()
    {
        var index = new CleanupIndex();
        var remote = index.Gpo("Porządki – Pulpit zdalny", Units.Value(Units.RemoteDesktopRight, "S-1-5-32-555", kind: GpoSettingUnitKind.UserRight));
        var audit = index.Gpo("Porządki – Inspekcja", Audit("0CCE9215-69AE-11D9-BED3-505054503030", "3"), Audit("0CCE923F-69AE-11D9-BED3-505054503030", "1"));
        index.Link(Serwery, remote).Link(Serwery, audit);

        var proposal = Assert.Single(Merges(index.Analyze()));

        Assert.Equal(remote.Id, proposal.GpoId);
        Assert.Equal([GpoCleanupAction.Merge], proposal.Actions);
        Assert.Equal([GpoCleanupNote.LinkOrderDiffers], proposal.Notes);
    }

    [Fact]
    public void Merge_ThreeGpos_EachIntoTheLargest()
    {
        var (index, remote, audit) = Pair();
        var third = index.Gpo("Porządki – Usługi", Units.Value("O|Computer/Security/Services/Spooler", "StartupMode=4", kind: GpoSettingUnitKind.Service));
        third.SecurityDescriptor = AuthenticatedUsers;
        index.Link(Serwery, third);

        var proposals = Merges(index.Analyze());

        Assert.Equal(new[] { remote.Id, third.Id }.Order(), proposals.Select(p => p.GpoId).Order());
        Assert.All(proposals, p => Assert.Equal([audit.Id], p.RelatedGpoIds));
    }

    [Fact]
    public void Merge_DifferentScope_NoProposal()
    {
        // Another security filter.
        var (filter, filterRemote, _) = Pair();
        filterRemote.SecurityDescriptor = Filter([AccountingGroupSid]);
        Assert.Empty(Merges(filter.Analyze()));

        // Another WMI filter.
        var (wmi, wmiRemote, _) = Pair();
        wmiRemote.WmiFilterId = "{8E0A3C12-6B4D-4F0E-9C1A-2B3D4E5F6A7B}";
        Assert.Empty(Merges(wmi.Analyze()));

        // Another link option (enforced) and another link set.
        var enforced = new CleanupIndex();
        var a = enforced.Gpo("A – Pulpit zdalny", Units.Value(Units.RemoteDesktopRight, "S-1-5-32-555"));
        var b = enforced.Gpo("B – Inspekcja", Audit("0CCE9215-69AE-11D9-BED3-505054503030", "3"), Audit("0CCE923F-69AE-11D9-BED3-505054503030", "1"));
        var c = enforced.Gpo("C – Usługi", Units.Value("O|Computer/Security/Services/Spooler", "StartupMode=4"));
        enforced.Link(Serwery, a, enforced: true).Link(Serwery, b).Link(Serwery, c).Link(Firma, c);
        Assert.Empty(Merges(enforced.Analyze()));
    }

    [Fact]
    public void Merge_ANotMergeableSource_GetsTheProposalWithoutMerge_AndTheReasons()
    {
        var index = new CleanupIndex();
        var target = index.Gpo("Serwery – Ustawienia ogólne", Units.Registry("a.jpg"), Units.Value(Units.MinimumPasswordLength, "9"),
            Units.Value(Units.RemoteDesktopRight, "S-1-5-32-555"), Units.Value(Units.LogonAudit, "3"), Units.Value("K|q", "1"), Units.Value("K|r", "2"));
        var blocked = index.Gpo("Serwery – Skrypt", Units.Script(), Units.Value("K|s", "1"));
        var directory = index.Gpo("Serwery – Instalacja", Units.Value("K|t", "1"));
        directory.DirectorySettings = 3;
        var otherFiles = index.Gpo("Serwery – Przekierowanie", Units.Value("K|u", "1"));
        otherFiles.OtherFiles.Add("User/Documents & Settings/fdeploy1.ini");
        var partDisabled = index.Gpo("Serwery – Użytkownicy wyłączeni", Units.Registry("b.jpg") with { Id = "K|v", BaseId = "K|v" });
        partDisabled.GpoStatus = GpoStatus.UserSettingsDisabled;
        var removePolicy = index.Gpo("Serwery – Dysk usuwany", Units.Drive(@"\\fs01\Serwis", "{R}") with { RemovePolicy = true });
        foreach (var gpo in new[] { target, blocked, directory, otherFiles, partDisabled, removePolicy })
        {
            index.Link(Serwery, gpo);
        }

        var proposals = Merges(index.Analyze()).ToDictionary(p => p.GpoId);

        Assert.Equal(5, proposals.Count);
        Assert.All(proposals.Values, p => Assert.Equal([target.Id], p.RelatedGpoIds));

        Assert.Empty(proposals[blocked.Id].Actions);
        Assert.Equal([GpoCleanupNote.NonCopyableUnits], proposals[blocked.Id].Notes);
        Assert.Equal([Units.Script().Id], proposals[blocked.Id].BlockingUnitIds);

        Assert.Empty(proposals[directory.Id].Actions);
        Assert.Equal([GpoCleanupNote.DirectorySettings], proposals[directory.Id].Notes);
        Assert.Empty(proposals[otherFiles.Id].Actions);
        Assert.Equal([GpoCleanupNote.OtherFiles], proposals[otherFiles.Id].Notes);
        Assert.Empty(proposals[partDisabled.Id].Actions);
        Assert.Equal([GpoCleanupNote.PartDisabled], proposals[partDisabled.Id].Notes);

        Assert.Equal([GpoCleanupAction.Merge], proposals[removePolicy.Id].Actions);
        Assert.Equal([GpoCleanupNote.RemovePolicy], proposals[removePolicy.Id].Notes);
    }

    [Fact]
    public void Merge_ADisabledHalfWithoutSettings_StaysMergeable_CommentsAndScriptFilesDoNotBlock()
    {
        var (index, remote, _) = Pair();
        remote.GpoStatus = GpoStatus.UserSettingsDisabled;
        remote.Units.Add(Units.Comment());
        remote.Units.Add(Units.ScriptFile());

        var proposal = Assert.Single(Merges(index.Analyze()));

        Assert.Equal([GpoCleanupAction.Merge], proposal.Actions);
        Assert.Empty(proposal.Notes);
    }

    [Fact]
    public void Merge_DefaultDisabledIncompleteAndEmptyGpos_NeverTakePart()
    {
        var index = new CleanupIndex();
        var ddp = index.DefaultDomainPolicy(Units.Value(Units.MinimumPasswordLength, "14"));
        var custom = index.Gpo("Firma – Hasła", Units.Value("K|Computer/Security/System Access/LockoutBadCount", "5"));
        var disabled = index.Gpo("Firma – Wyłączony", Units.Registry("a.jpg"));
        disabled.GpoStatus = GpoStatus.AllSettingsDisabled;
        var incomplete = index.Gpo("Firma – Niepełny", Units.Registry("b.jpg"));
        incomplete.Status = GpoIndexStatus.Incomplete;
        var empty = index.Gpo("Firma – Pusty", Units.Comment());
        index.Link(Domain, ddp).Link(Domain, custom).Link(Domain, disabled).Link(Domain, incomplete).Link(Domain, empty);

        Assert.Empty(Merges(index.Analyze()));
    }

    [Fact]
    public void Merge_AllLinksDisabled_NeverTakePart()
    {
        var (index, _, _) = Pair();
        var linkless = new CleanupIndex();
        var a = linkless.Gpo("A – Pulpit zdalny", Units.Value(Units.RemoteDesktopRight, "S-1-5-32-555"));
        var b = linkless.Gpo("B – Inspekcja", Audit("0CCE9215-69AE-11D9-BED3-505054503030", "3"));
        linkless.Link(Serwery, a, disabled: true).Link(Serwery, b, disabled: true);

        Assert.Empty(Merges(linkless.Analyze()));
        Assert.Single(Merges(index.Analyze()));
    }
}
