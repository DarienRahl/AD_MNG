using System.Globalization;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.GroupPolicy.Diagnostics;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy.Diagnostics;

/// <summary>
/// "Napraw…" of the diagnostics (K6, design note 20.2): every <see cref="GpoQuickFix"/> record goes to its planner call and
/// through the change plan preview (scripted: executed as the operator would), on the fake directory and SYSVOL.
/// </summary>
public sealed class GpoQuickFixRunnerTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task Unlink_RemovesTheLink()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);

        await RunAsync(h, new GpoQuickFix.Unlink(h.Company.EmployeesOu, h.Sample.DriveMappings), "Użytkownicy – Mapowanie dysków",
            "Usunięcie połączenia „Użytkownicy – Mapowanie dysków”");

        Assert.DoesNotContain(await LinksAsync(h, h.Company.EmployeesOu), l => l.GpoId == h.Sample.DriveMappings);
    }

    [Fact]
    public async Task EnableLink_EnablesTheLink_AndKeepsItsOrder()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);

        await RunAsync(h, new GpoQuickFix.EnableLink(h.Company.ComputersOu, h.Sample.WorkstationRemoteDesktop), "Stacje robocze – Pulpit zdalny",
            "Połączenie „Stacje robocze – Pulpit zdalny”");

        var link = Assert.Single(await LinksAsync(h, h.Company.ComputersOu), l => l.GpoId == h.Sample.WorkstationRemoteDesktop);
        Assert.True(link.Enabled);
        Assert.Equal(2, link.Order);
    }

    [Fact]
    public async Task RemoveDuplicateLinks_KeepsTheLinkThatCounts()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var passwords = h.Sample.WorkstationPasswords;
        h.B2.Builder.SetGroupPolicyLinks(h.Company.ComputersOu, [(passwords, GroupPolicyLink.EnforcedOption), (h.Sample.WorkstationRemoteDesktop, 0), (passwords, 0)]);

        await RunAsync(h, new GpoQuickFix.RemoveDuplicateLinks(h.Company.ComputersOu, passwords), "Stacje robocze – Zasady haseł",
            "Pozostawienie jednego połączenia „Stacje robocze – Zasady haseł”");

        var link = Assert.Single(await LinksAsync(h, h.Company.ComputersOu), l => l.GpoId == passwords);
        Assert.False(link.Enforced); // the entry nearest the end (link order 1) counts
        Assert.Contains(await LinksAsync(h, h.Company.ComputersOu), l => l.GpoId == h.Sample.WorkstationRemoteDesktop);
    }

    [Fact]
    public async Task SetStatus_SetsTheGpoStatus()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);

        await RunAsync(h, new GpoQuickFix.SetStatus(h.Sample.DriveMappings, GpoStatus.ComputerSettingsDisabled), "Użytkownicy – Mapowanie dysków",
            "Stan obiektu GPO „Użytkownicy – Mapowanie dysków”");

        Assert.Equal(GpoStatus.ComputerSettingsDisabled, (await GpoAsync(h, h.Sample.DriveMappings)).Status);
    }

    [Fact]
    public async Task GrantRead_GivesAuthenticatedUsersRead_AndKeepsTheSecurityFiltering()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var it = h.B2.Builder.SidOf(h.Company.GroupIt);
        var gpo = h.B2.Builder.AddManagedGroupPolicy(h.Sysvol, "IT – Mapowanie dysków", 1, 0, extraAces:
        [
            new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, it),
            new FakeAce(FakeAce.AccessAllowedObject, 0x02, 0x00000100, it, GroupPolicySecurityFilter.ApplyGroupPolicyRight),
        ]);

        await RunAsync(h, new GpoQuickFix.GrantRead(gpo, GroupPolicySecurityFilter.AuthenticatedUsersSid), "IT – Mapowanie dysków",
            "Delegowanie „IT – Mapowanie dysków”");

        var permissions = GpoPermissionLevels.Read((await GpoAsync(h, gpo)).SecurityDescriptor);
        Assert.Contains(permissions, p => p.Sid == GroupPolicySecurityFilter.AuthenticatedUsersSid && p.Level == GpoPermissionLevel.Read && !p.Applies);
        Assert.Contains(permissions, p => p.Sid == it && p.Applies);
    }

    [Fact]
    public async Task ClearWmiFilter_ClearsTheReference()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);

        await RunAsync(h, new GpoQuickFix.ClearWmiFilter(h.Sample.WorkstationPasswords), "Stacje robocze – Zasady haseł",
            "Filtr WMI obiektu GPO „Stacje robocze – Zasady haseł”");

        Assert.Null((await GpoAsync(h, h.Sample.WorkstationPasswords)).WmiFilterId);
    }

    [Fact]
    public async Task DeleteWmiFilter_DeletesTheFilter_NamedAfterTheFilter()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var row = new GpoFindingRowViewModel
        {
            Finding = new GpoFinding(GpoFindingCodes.WmiFilterUnused, GpoFindingSeverity.Info, GpoFindingArea.Filtering, null, null, null,
                ["Tylko stacje robocze", h.Sample.WmiFilterId], new GpoQuickFix.DeleteWmiFilter(h.Sample.WmiFilterDn)),
            SeverityText = string.Empty,
            AreaText = string.Empty,
            GpoText = "–",
            WhereText = string.Empty,
            Title = string.Empty,
            Message = string.Empty,
            Explanation = string.Empty,
            Recommendation = string.Empty,
            AffectedLines = [],
            FixText = string.Empty,
            Key = string.Empty,
            SearchText = string.Empty,
        };
        Assert.Equal("Tylko stacje robocze", GpoQuickFixRunner.SubjectOf(row));

        await RunAsync(h, row.Finding.Fix!, GpoQuickFixRunner.SubjectOf(row), "Usunięcie filtru WMI „Tylko stacje robocze”");

        Assert.False(h.Session.Exists(h.Sample.WmiFilterDn));
    }

    [Fact]
    public async Task AlignVersions_AlignsAdAndGptIni()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);

        await RunAsync(h, new GpoQuickFix.AlignVersions(h.Sample.DriveMappings), "Użytkownicy – Mapowanie dysków",
            "Wyrównanie wersji „Użytkownicy – Mapowanie dysków”");

        Assert.False((await GpoAsync(h, h.Sample.DriveMappings, withSysvol: true)).VersionMismatch);
    }

    [Fact]
    public async Task RepairFolderSecurity_MapsTheFolderAclAgain()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var gpo = h.Sample.WorkstationPasswords;
        h.Sysvol.SetFolderSecurity(gpo, GpoFolderAcl.Build([new GpoFolderAcl.FileAce("S-1-1-0", 0x03, GpoFolderAcl.ReadAndExecute)]));

        await RunAsync(h, new GpoQuickFix.RepairFolderSecurity(gpo), "Stacje robocze – Zasady haseł", "Naprawa uprawnień folderu „Stacje robocze – Zasady haseł”");

        await using var sysvol = await h.Sysvol.OpenForWriteAsync(h.Session, CancellationToken.None);
        var folder = await sysvol.GetGpoFolderSecurityAsync(gpo, CancellationToken.None);
        Assert.True(GpoFolderAcl.IsConsistent(folder!, (await GpoAsync(h, gpo)).SecurityDescriptor));
    }

    [Fact]
    public async Task RemoveTrustee_RemovesEveryEntryOfTheSid()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var orphan = h.B2.Builder.DomainSid + "-4321";
        var gpo = h.B2.Builder.AddManagedGroupPolicy(h.Sysvol, "Serwis – Stare delegowanie", 0, 1,
            extraAces: [new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000200BF, orphan), new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, "S-1-5-11")]);

        await RunAsync(h, new GpoQuickFix.RemoveTrustee(gpo, orphan), "Serwis – Stare delegowanie", "Delegowanie „Serwis – Stare delegowanie”");

        Assert.DoesNotContain(GpoPermissionLevels.Read((await GpoAsync(h, gpo)).SecurityDescriptor), p => p.Sid == orphan);
    }

    [Fact]
    public async Task RepairExtensionNames_AddsTheSecurityTemplatePair()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var gpo = h.Sample.DefaultDomainControllersPolicy;

        await RunAsync(h, new GpoQuickFix.RepairExtensionNames(gpo), "Default Domain Controllers Policy",
            "Naprawa nazw rozszerzeń „Default Domain Controllers Policy”");

        Assert.Equal("[{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]",
            h.Value(h.GpoDn(gpo), GroupPolicyAttributes.MachineExtensionNames));
    }

    [Fact]
    public async Task ASysvolRepair_WithoutSysvol_ShowsWhy_AndWritesNothing()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable;

        Assert.False(await new GpoQuickFixRunner(h.Actions).RunAsync(new GpoQuickFix.AlignVersions(h.Sample.DriveMappings), "Użytkownicy – Mapowanie dysków"));

        Assert.Empty(h.Windows.Previews);
        Assert.Contains(h.B2.Dialogs.Errors, e => e.Message.Contains("nie można połączyć", StringComparison.Ordinal));
    }

    private static async Task RunAsync(GroupPolicyHarness h, GpoQuickFix fix, string subject, string title)
    {
        Assert.True(await new GpoQuickFixRunner(h.Actions).RunAsync(fix, subject));
        var preview = Assert.Single(h.Windows.Previews);
        Assert.Equal(title, preview.Title);
        Assert.Equal((0, false), (preview.Result!.FailedCount, preview.Result.WasCancelled));
        Assert.True(preview.Result.SucceededCount > 0, preview.Title);
        Assert.NotEmpty(preview.Plan.Objects);
    }

    private static async Task<IReadOnlyList<GpLinkEntry>> LinksAsync(GroupPolicyHarness h, string containerDn) =>
        (await h.Planner.Reader.ReadContainerAsync(h.Session, containerDn, CancellationToken.None))!.Links.Links;

    private static async Task<GpoInfo> GpoAsync(GroupPolicyHarness h, Guid id, bool withSysvol = false)
    {
        var gpo = (await h.Planner.Reader.ReadPolicyAsync(h.Session, id, CancellationToken.None))!;
        if (!withSysvol)
        {
            return gpo;
        }

        await using var sysvol = await h.Sysvol.OpenForWriteAsync(h.Session, CancellationToken.None);
        return await h.Planner.Reader.WithSysvolVersionAsync(sysvol, gpo, CancellationToken.None);
    }
}
