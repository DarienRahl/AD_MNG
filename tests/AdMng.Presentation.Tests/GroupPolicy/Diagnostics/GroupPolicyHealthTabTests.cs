using System.Globalization;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy.Diagnostics;

/// <summary>
/// The "Stan" tab of a GPO on the diagnostics engine (K6, design note 20.2): Full with SYSVOL, read when the tab is shown,
/// one line per area, the findings with their repairs, the existing "Wyrównaj wersje" following the findings.
/// </summary>
public sealed class GroupPolicyHealthTabTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task OpenedOnStan_RunsAFullCheckOfTheGpo_WithOneLinePerArea()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.DriveMappings, GpoDocumentTab.Status);

        Assert.NotEmpty(document.HealthFindings);
        Assert.All(document.HealthFindings, f => Assert.True(f.Finding.GpoId == h.Sample.DriveMappings || f.Finding.RelatedGpoIds.Contains(h.Sample.DriveMappings)));
        var mismatch = Assert.Single(document.HealthFindings, f => f.Code == GpoFindingCodes.VersionMismatch);
        Assert.True(mismatch.HasFix);
        Assert.True(document.HasVersionMismatch);
        Assert.True(document.AlignVersionsCommand.CanExecute(null));
        Assert.StartsWith("Ostatnie sprawdzenie: pełne, ", document.HealthStatusText, StringComparison.Ordinal);
        Assert.Empty(document.HealthNotes);

        Assert.Equal(["Połączenia", "Zakres", "Filtrowanie", "Wersje", "Folder SYSVOL", "Ustawienia", "Konflikty", "Bezpieczeństwo"],
            document.Checks.Select(c => c.Text));
        var versions = document.Checks.Single(c => c.Text == "Wersje");
        Assert.Equal(("Uwagi: 1, najpoważniejsza: średnia", false), (versions.Detail, versions.IsOk));
        Assert.Contains(document.Checks, c => c.Text == "Bezpieczeństwo" && c.IsOk && c.Detail == "Bez uwag");
    }

    [Fact]
    public async Task OnAnotherTab_TheEngineWaits_UntilStanIsSelected()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.WorkstationPasswords);
        Assert.Empty(document.HealthFindings);
        Assert.Empty(document.Checks);
        Assert.DoesNotContain(h.Sysvol.Calls, c => c.Operation == "List" && c.GpoId != h.Sample.WorkstationPasswords);

        document.SelectedTab = GpoDocumentTab.Status;
        await B2Harness.WaitUntilAsync(() => document.HealthFindings.Count > 0 && !document.IsCheckingHealth);

        var account = Assert.Single(document.HealthFindings, f => f.Code == GpoFindingCodes.AccountPolicyBelowDomain);
        Assert.Equal("Minimalna długość hasła", account.WhereText);
        Assert.True(document.EditHealthSettingCommand.CanExecute(null) is false);
        document.SelectedHealthFinding = account;
        Assert.True(document.EditHealthSettingCommand.CanExecute(null));
        Assert.False(document.FixHealthFindingCommand.CanExecute(null)); // no automatic repair
        Assert.False(document.HasVersionMismatch);
        Assert.False(document.HasFolderAclMismatch);
    }

    [Fact]
    public async Task Fix_OfAFinding_RunsThroughThePreview_AndTheTabChecksAgain()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.DriveMappings, GpoDocumentTab.Status);
        document.SelectedHealthFinding = document.HealthFindings.Single(f => f.Code == GpoFindingCodes.VersionMismatch);

        await document.FixHealthFindingCommand.ExecuteAsync(null);

        Assert.Equal("Wyrównanie wersji „Użytkownicy – Mapowanie dysków”", Assert.Single(h.Windows.Previews).Title);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && !document.IsCheckingHealth && !document.HasVersionMismatch);
        Assert.DoesNotContain(document.HealthFindings, f => f.Code == GpoFindingCodes.VersionMismatch);
        Assert.Contains(document.Checks, c => c.Text == "Wersje" && c.IsOk);
    }

    [Fact]
    public async Task FolderAclMismatch_EnablesTheExistingRepair()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.Sysvol.SetFolderSecurity(h.Sample.WorkstationPasswords,
            Core.GroupPolicy.Management.GpoFolderAcl.Build([new Core.GroupPolicy.Management.GpoFolderAcl.FileAce("S-1-1-0", 0x03, Core.GroupPolicy.Management.GpoFolderAcl.ReadAndExecute)]));
        using var document = await h.GpoAsync(h.Sample.WorkstationPasswords, GpoDocumentTab.Status);

        var finding = Assert.Single(document.HealthFindings, f => f.Code == GpoFindingCodes.FolderSecurityMismatch);
        Assert.Equal("Naprawa: napraw uprawnienia folderu", finding.FixText);
        Assert.True(document.HasFolderAclMismatch);
        Assert.True(document.RepairFolderSecurityCommand.CanExecute(null));

        await document.RepairFolderSecurityCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && !document.IsCheckingHealth && !document.HasFolderAclMismatch);
    }

    [Fact]
    public async Task WithoutSysvol_TheAreasSayWhatWasNotChecked_AndWhy()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.Sysvol.Unavailable = Core.GroupPolicy.SysvolUnavailableReason.Unreachable;
        using var document = await h.GpoAsync(h.Sample.DriveMappings, GpoDocumentTab.Status);

        Assert.Equal([document.SysvolWarning!], document.HealthNotes);
        Assert.Contains(document.Checks, c => c.Text == "Wersje" && !c.IsOk && c.Detail == "Nie sprawdzono – SYSVOL jest niedostępny");
        Assert.Contains(document.Checks, c => c.Text == "Połączenia" && !c.Detail.StartsWith("Nie sprawdzono", StringComparison.Ordinal));
        Assert.Contains(document.Checks, c => c.Text == "Folder SYSVOL" && c.Detail.EndsWith("(sprawdzono częściowo – SYSVOL jest niedostępny)", StringComparison.Ordinal));
        Assert.DoesNotContain(document.HealthFindings, f => f.Code == GpoFindingCodes.VersionMismatch);
        Assert.False(document.AlignVersionsCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckAgain_ReadsTheGpoAgain()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.DriveMappings, GpoDocumentTab.Status);
        var shown = document.HealthFindings;

        await document.CheckHealthCommand.ExecuteAsync(null);

        Assert.NotSame(shown, document.HealthFindings);
        Assert.Equal(shown.Select(f => f.Key), document.HealthFindings.Select(f => f.Key));
    }

    [Fact]
    public async Task AReloadOnStan_LeavesCheckAgainAndFixEnabled()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.DriveMappings, GpoDocumentTab.Status);
        document.SelectedHealthFinding = document.HealthFindings.Single(f => f.Code == GpoFindingCodes.VersionMismatch);

        // What a button shows: the command's state each time it says its state changed.
        bool? checkEnabled = null, fixEnabled = null;
        document.CheckHealthCommand.CanExecuteChanged += (_, _) => checkEnabled = document.CheckHealthCommand.CanExecute(null);
        document.FixHealthFindingCommand.CanExecuteChanged += (_, _) => fixEnabled = document.FixHealthFindingCommand.CanExecute(null);

        await document.RefreshCommand.ExecuteAsync(null); // F5: the check runs inside the load

        Assert.False(document.IsLoading);
        Assert.Equal(GpoFindingCodes.VersionMismatch, document.SelectedHealthFinding?.Code);
        Assert.True(checkEnabled);
        Assert.True(fixEnabled);
    }

    [Fact]
    public async Task StanSelectedWhileTheLoadEnds_IsCheckedWhenTheLoadEnds()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.DriveMappings);
        var selected = false;
        h.Session.BeforeOperation = operation =>
        {
            // The PDC note is read after the load's own check point: the operator clicks "Stan" just then.
            if (!selected && operation.Kind == FakeOperationKind.Get && operation.Attributes.Contains("fSMORoleOwner"))
            {
                selected = true;
                document.SelectedTab = GpoDocumentTab.Status;
            }
        };

        await document.RefreshCommand.ExecuteAsync(null);
        h.Session.BeforeOperation = null;

        Assert.True(selected);
        await B2Harness.WaitUntilAsync(() => document.HealthFindings.Count > 0 && !document.IsCheckingHealth);
        Assert.Contains(document.HealthFindings, f => f.Code == GpoFindingCodes.VersionMismatch);
        Assert.True(document.CheckHealthCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACheckWithoutSysvol_AfterALoadWithIt_SaysWhySysvolWasNotRead()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.DriveMappings);
        Assert.Null(document.SysvolWarning);

        h.Sysvol.Unavailable = Core.GroupPolicy.SysvolUnavailableReason.Unreachable; // SMB to the DC fails after the load
        document.SelectedTab = GpoDocumentTab.Status;
        await B2Harness.WaitUntilAsync(() => document.Checks.Count > 0 && !document.IsCheckingHealth);

        var warning = h.Services.SysvolText(Core.GroupPolicy.SysvolUnavailableReason.Unreachable, h.Session.Info.Server);
        Assert.Equal([warning], document.HealthNotes);
        Assert.Contains(document.Checks, c => c.Text == "Wersje" && !c.IsOk && c.Detail == "Nie sprawdzono – SYSVOL jest niedostępny");
        Assert.Contains(document.Checks, c => c.Text == "Folder SYSVOL" && c.Detail.EndsWith("(sprawdzono częściowo – SYSVOL jest niedostępny)", StringComparison.Ordinal));
        Assert.Null(document.SysvolWarning); // the document's own load still had SYSVOL
    }

    [Fact]
    public async Task AMissingGptIni_OffersAlignVersions_WhichWritesItAgain()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var gpo = h.Sample.WorkstationPasswords;
        await using (var connection = await h.Sysvol.OpenForWriteAsync(h.Session, TestContext.Current.CancellationToken))
        {
            await connection.DeleteFileAsync(gpo, Core.GroupPolicy.GptIni.RelativePath, TestContext.Current.CancellationToken);
        }

        using var document = await h.GpoAsync(gpo, GpoDocumentTab.Status);

        Assert.Contains(document.HealthFindings, f => f.Code == GpoFindingCodes.SysvolFolderMissing && f.Finding.Arguments is ["GptIni"]);
        Assert.True(document.HasVersionMismatch);
        Assert.True(document.AlignVersionsCommand.CanExecute(null));

        await document.AlignVersionsCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && !document.IsCheckingHealth && !document.HasVersionMismatch);
        Assert.NotNull(Core.GroupPolicy.GptIni.ReadVersion(h.Sysvol.Get(gpo, Core.GroupPolicy.GptIni.RelativePath)!));
        Assert.DoesNotContain(document.HealthFindings, f => f.Code == GpoFindingCodes.SysvolFolderMissing);
    }

    [Fact]
    public async Task AGptIniThatCannotBeParsed_IsNotOfferedTheAlignment_ItsTransactionRefuses()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var gpo = h.Sample.WorkstationPasswords;
        h.Sysvol.PutText(gpo, Core.GroupPolicy.GptIni.RelativePath, "[General]\r\nVersion=zażółć\r\n");

        using var document = await h.GpoAsync(gpo, GpoDocumentTab.Status);

        Assert.Contains(document.HealthFindings, f => f.Code == GpoFindingCodes.UnreadableFile);
        Assert.False(document.HasVersionMismatch);
        Assert.False(document.AlignVersionsCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheDetailsCard_KnowsWhetherAFindingIsSelected()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.DriveMappings, GpoDocumentTab.Status);
        Assert.Null(document.SelectedHealthFinding); // nothing selected after a check
        Assert.False(document.HasSelectedHealthFinding);

        var changes = new List<string?>();
        document.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        document.SelectedHealthFinding = document.HealthFindings[0];

        Assert.True(document.HasSelectedHealthFinding);
        Assert.Contains(nameof(document.HasSelectedHealthFinding), changes);
    }
}
