using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Cleanup;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Cleanup;

/// <summary>
/// The cleanup sample (<see cref="FakeDirectoryBuilder.AddGroupPolicyCleanupSample"/>): its directory data and files now,
/// and end to end through <see cref="GpoSettingsIndexBuilder"/> once W1's builder is merged (21.10).
/// </summary>
public sealed class GroupPolicyCleanupSampleTests
{
    private const string WaveOneMerge = "K7 wave 1 merge";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeDirectoryBuilder _builder = new();
    private readonly FakeSysvol _sysvol = new();
    private readonly GroupPolicyReader _reader = new();

    private (SampleCompany Company, GroupPolicyCleanupSample Sample) Create(bool withManagementSample = false)
    {
        var company = _builder.AddSampleCompany();
        if (withManagementSample)
        {
            _builder.AddGroupPolicyManagementSample(company, _sysvol);
        }

        return (company, _builder.AddGroupPolicyCleanupSample(company, _sysvol));
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<GpoLinkInfo>>> LinksAsync() =>
        GroupPolicyReader.LinksByGpo(await _reader.ReadLinkingContainersAsync(_builder.Session, Ct));

    private async Task<IReadOnlyList<GpoSetting>> SettingsAsync(Guid gpo)
    {
        await using var sysvol = await _sysvol.OpenForWriteAsync(_builder.Session, Ct);
        return GroupPolicyReader.Settings(await GroupPolicyReader.ReadFolderAsync(sysvol, gpo, Ct));
    }

    [Fact]
    public async Task Sample_GposLinksAndStatus()
    {
        var (company, sample) = Create();

        var gpos = (await _reader.ReadPoliciesAsync(_builder.Session, Ct)).ToDictionary(g => g.Id);
        var links = await LinksAsync();
        IReadOnlyList<GpoLinkInfo> LinksOf(Guid id) => links.TryGetValue(id, out var l) ? l : [];

        Assert.Equal(9, gpos.Count);
        Assert.Equal("Porządki – Pusty GPO", gpos[sample.EmptyGpo].DisplayName);
        Assert.Equal(new GpoVersion(0, 2), gpos[sample.EmptyGpo].DirectoryVersion);
        Assert.Equal(company.CompanyOu, Assert.Single(LinksOf(sample.EmptyGpo)).ContainerDn);
        Assert.Empty(LinksOf(sample.Unlinked));
        Assert.Equal(GroupPolicySomKind.Domain, Assert.Single(LinksOf(sample.Templates)).Kind);
        Assert.Equal(company.ComputersOu, Assert.Single(LinksOf(sample.TemplatesBranch)).ContainerDn);
        Assert.Equal(company.AccountingOu, Assert.Single(LinksOf(sample.DrivesAccounting)).ContainerDn);
        Assert.Equal(sample.HrOu, Assert.Single(LinksOf(sample.DrivesHr)).ContainerDn);

        // "Inspekcja" at link order 1, "Pulpit zdalny" below it, both enabled, nothing enforced, the same default filter.
        var audit = Assert.Single(LinksOf(sample.Audit));
        var remote = Assert.Single(LinksOf(sample.RemoteDesktop));
        Assert.Equal((company.ItOu, 1, true, false), (audit.ContainerDn, audit.Order, audit.Enabled, audit.Enforced));
        Assert.Equal((company.ItOu, 2, true, false), (remote.ContainerDn, remote.Order, remote.Enabled, remote.Enforced));
        Assert.Equal(gpos[sample.Audit].SecurityFilter!.Allowed.Order(), gpos[sample.RemoteDesktop].SecurityFilter!.Allowed.Order());
        Assert.Null(gpos[sample.Audit].WmiFilterId);

        Assert.Equal(GpoStatus.AllSettingsDisabled, gpos[sample.Disabled].Status);
        Assert.False(Assert.Single(LinksOf(sample.Disabled)).Enabled);
        Assert.Equal(FakeDirectoryBuilder.DriveMapsUserExtensionNames, gpos[sample.DrivesAccounting].UserExtensionNames);
    }

    [Fact]
    public async Task Sample_FilesParseIntoTheExpectedSettings()
    {
        var (_, sample) = Create();

        Assert.Equal(["GPT.INI"], _sysvol.Files(sample.EmptyGpo));
        Assert.Empty(await SettingsAsync(sample.EmptyGpo));

        foreach (var gpo in new[] { sample.Unlinked, sample.Templates, sample.TemplatesBranch })
        {
            var setting = Assert.Single(await SettingsAsync(gpo));
            Assert.Equal(@"Computer/Registry/HKLM\Software\Policies\Contoso\Eksplorator!NoPreview", setting.Key, ignoreCase: true);
        }

        Assert.Equal(["Computer/Security/Privilege Rights/SeRemoteInteractiveLogonRight"], (await SettingsAsync(sample.RemoteDesktop)).Select(s => s.Key));
        Assert.Equal(2, (await SettingsAsync(sample.Audit)).Count);

        // The same drive K: in both department GPOs, under different per-GPO item keys (uids).
        var accounting = await SettingsAsync(sample.DrivesAccounting);
        var hr = await SettingsAsync(sample.DrivesHr);
        Assert.DoesNotContain(accounting.Concat(hr), s => s.Kind == GpoSettingKind.Unreadable);
        Assert.Contains(accounting, s => s.Value == FakeDirectoryBuilder.CleanupDrivePath);
        Assert.Contains(hr, s => s.Value == FakeDirectoryBuilder.CleanupDrivePath);
        Assert.Empty(accounting.Select(s => s.Key).Intersect(hr.Select(s => s.Key), StringComparer.OrdinalIgnoreCase));
        Assert.Contains(hr, s => s.Key.StartsWith("Computer/Preferences/Groups/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Sample_CombinesWithTheManagementSample_WithoutChangingIt()
    {
        var (_, sample) = Create(withManagementSample: true);

        var gpos = await _reader.ReadPoliciesAsync(_builder.Session, Ct);
        var links = await LinksAsync();

        Assert.Equal(7 + 9, gpos.Count);
        var head = (await _reader.ReadContainerAsync(_builder.Session, _builder.DomainDn, Ct))!;
        Assert.Equal(3, head.Links.Links.Count);
        Assert.True(head.Links.Contains(FakeDirectoryBuilder.DefaultDomainPolicyGuid));
        Assert.True(head.Links.Contains(sample.Templates));

        // The management sample's own links keep their order; the cleanup GPO comes last (lowest precedence).
        Assert.Equal(3, head.Links.Find(sample.Templates)!.Order);
        Assert.Equal(GroupPolicySomKind.Domain, Assert.Single(links[sample.Templates]).Kind);
    }

    /// <summary>The proposals the sample is built for (with or without the ADMX set: policy unit or raw registry value).</summary>
    [Theory(Skip = WaveOneMerge)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EndToEnd_TheIndexOfTheSample_GivesTheDesignedProposals(bool withDefinitions)
    {
        var (_, sample) = Create();
        await using var sysvol = await _sysvol.OpenForWriteAsync(_builder.Session, Ct);
        var index = await GpoSettingsIndexBuilder.BuildAsync(_builder.Session, sysvol,
            new GpoSettingsIndexRequest { Definitions = withDefinitions ? SampleAdministrativeTemplates.LoadPolish() : null }, null, Ct);

        var result = GpoCleanupAnalyzer.Analyze(index);

        Assert.Empty(result.NotEvaluated);
        var kinds = result.Proposals.Select(p => (p.Kind, p.GpoId)).ToHashSet();
        Assert.Contains((GpoCleanupKind.EmptyGpo, sample.EmptyGpo), kinds);
        Assert.Contains((GpoCleanupKind.UnlinkedGpo, sample.Unlinked), kinds);
        Assert.Contains((GpoCleanupKind.AllLinksDisabled, sample.Disabled), kinds);
        Assert.Contains((GpoCleanupKind.GpoDisabled, sample.Disabled), kinds);

        var duplicate = Assert.Single(result.Proposals, p => p.Kind == GpoCleanupKind.DuplicateSetting);
        Assert.Equal(sample.TemplatesBranch, duplicate.GpoId);
        Assert.Equal([sample.Templates], duplicate.RelatedGpoIds);
        Assert.Equal([GpoCleanupAction.RemoveSetting], duplicate.Actions);

        var merge = Assert.Single(result.Proposals, p => p.Kind == GpoCleanupKind.MergeCandidate);
        Assert.Equal(sample.RemoteDesktop, merge.GpoId);
        Assert.Equal([sample.Audit], merge.RelatedGpoIds);
        Assert.Equal([GpoCleanupAction.Merge], merge.Actions);
        Assert.Empty(merge.Notes);

        var single = Assert.Single(result.Proposals, p => p.Kind == GpoCleanupKind.SingleSettingGpo && p.GpoId == sample.RemoteDesktop);
        Assert.Equal([GpoCleanupAction.CopyTo, GpoCleanupAction.Disable, GpoCleanupAction.Merge], single.Actions);
        Assert.Equal([sample.Audit], single.RelatedGpoIds);

        // The drive GPOs on disjoint OUs: no duplicate, no different values; the unlinked GPO never takes part.
        Assert.DoesNotContain(result.Proposals, p => p.Kind == GpoCleanupKind.DifferentValues);
        Assert.DoesNotContain(result.Proposals, p => p.Kind == GpoCleanupKind.DuplicateSetting
            && (p.GpoId == sample.DrivesAccounting || p.GpoId == sample.DrivesHr || p.GpoId == sample.Unlinked));
        Assert.DoesNotContain(result.Proposals, p => p.Kind == GpoCleanupKind.EmptyGpo && p.GpoId != sample.EmptyGpo);
    }

    [Fact(Skip = WaveOneMerge)]
    public async Task EndToEnd_WithTheManagementSample_TheCleanupProposalsStay()
    {
        var (_, sample) = Create(withManagementSample: true);
        await using var sysvol = await _sysvol.OpenForWriteAsync(_builder.Session, Ct);
        var index = await GpoSettingsIndexBuilder.BuildAsync(_builder.Session, sysvol, new GpoSettingsIndexRequest(), null, Ct);

        var result = GpoCleanupAnalyzer.Analyze(index);

        var duplicate = Assert.Single(result.Proposals, p => p.Kind == GpoCleanupKind.DuplicateSetting && p.GpoId == sample.TemplatesBranch);
        Assert.Equal([sample.Templates], duplicate.RelatedGpoIds);
        Assert.Contains(result.Proposals, p => p.Kind == GpoCleanupKind.MergeCandidate && p.GpoId == sample.RemoteDesktop);

        // Default GPOs are never deleted or merged.
        Assert.DoesNotContain(result.Proposals, p => (p.GpoId == GpoInfo.DefaultDomainPolicyId || p.GpoId == GpoInfo.DefaultDomainControllersPolicyId)
            && (p.Actions.Contains(GpoCleanupAction.Delete) || p.Actions.Contains(GpoCleanupAction.Merge)));
    }
}
