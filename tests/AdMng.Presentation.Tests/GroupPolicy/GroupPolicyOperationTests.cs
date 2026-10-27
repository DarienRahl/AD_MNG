using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Tests.Dialogs;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// Every GPO operation from the documents through the preview (executed by the scripted window, as the operator would) to
/// the fake directory and the fake SYSVOL, and the refreshed documents (design note 5.3, 10).
/// </summary>
public sealed class GroupPolicyOperationTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task CreateAndLink_FromTheScopeDocument_CreatesTheGpoItsFolderAndTheLink()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var scope = await h.ScopeAsync(h.Company.ComputersOu);
        h.Windows.NewGpoName = "Stacje robocze – Zapora „Łódź”";

        await scope.CreateAndLinkCommand.ExecuteAsync(null);

        var dialog = Assert.Single(h.Windows.NameDialogs);
        Assert.Equal(NewGpoMode.New, dialog.Mode);
        Assert.Equal("Połączone z: Komputery", dialog.LinkTargetText);
        var preview = Assert.Single(h.Windows.Previews);
        Assert.True(preview.Plan.Sequential);
        Assert.Contains("New-GPO -Name 'Stacje robocze – Zapora „Łódź”'", preview.PowerShell, StringComparison.Ordinal);
        Assert.Contains("New-GPLink", preview.PowerShell, StringComparison.Ordinal);
        Assert.Equal(0, preview.Result!.FailedCount);

        var created = (await h.Planner.Reader.ReadPoliciesAsync(h.Session, CancellationToken.None)).Single(g => g.DisplayName == "Stacje robocze – Zapora „Łódź”");
        Assert.True(h.Sysvol.FolderExists(created.Id));
        Assert.Equal("[General]\r\nVersion=0\r\n", h.Sysvol.GetText(created.Id, "GPT.INI"));
        Assert.NotNull(h.Sysvol.FolderSecurity(created.Id));
        await B2Harness.WaitUntilAsync(() => scope.Links.Count == 3);
        Assert.Equal("Stacje robocze – Zapora „Łódź”", scope.Links[^1].GpoName);
        Assert.Contains(h.Sysvol.Writes, w => w.Server == h.Session.Info.Server);
    }

    [Fact]
    public async Task Copy_WithPermissions_CopiesFilesAndExplicitAces()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var objects = await h.ObjectsAsync();
        objects.Selection = [objects.Rows.Single(r => r.Gpo.Id == h.Sample.WorkstationPasswords)];
        h.Windows.CopyPermissions = true;

        await objects.CopyCommand.ExecuteAsync(null);

        Assert.Equal("Kopia Stacje robocze – Zasady haseł", h.Windows.NameDialogs.Single().Name);
        var preview = Assert.Single(h.Windows.Previews);
        Assert.Contains("Copy-GPO", preview.PowerShell, StringComparison.Ordinal);
        Assert.Contains("-CopyAcl", preview.PowerShell, StringComparison.Ordinal);
        var copy = (await h.Planner.Reader.ReadPoliciesAsync(h.Session, CancellationToken.None)).Single(g => g.DisplayName == "Kopia Stacje robocze – Zasady haseł");
        Assert.Equal(h.Sysvol.Get(h.Sample.WorkstationPasswords, SecurityTemplate.RelativePath), h.Sysvol.Get(copy.Id, SecurityTemplate.RelativePath));
        Assert.Contains(GpoPermissionLevels.Read(copy.SecurityDescriptor), p => p.Sid == h.B2.Builder.SidOf(h.Company.LukaszWisniewski) && p.Level == GpoPermissionLevel.Edit);
        Assert.Equal(new GpoVersion(0, 1), copy.DirectoryVersion);
    }

    [Fact]
    public async Task Rename_ChangesTheDisplayName_AndTheTreeAndDocumentFollow()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.SitePrinters);
        h.Windows.NewGpoName = "Lokacja Warszawa – Drukarki piętro 2";

        await document.RenameCommand.ExecuteAsync(null);

        Assert.Equal("Lokacja Warszawa – Drukarki piętro 2", h.Value(h.GpoDn(h.Sample.SitePrinters), Attr.DisplayName));
        Assert.Contains("Rename-GPO", h.Windows.Previews.Single().PowerShell, StringComparison.Ordinal);
        await B2Harness.WaitUntilAsync(() => document.Title == "Lokacja Warszawa – Drukarki piętro 2");
    }

    [Fact]
    public async Task Delete_OffersABackup_AsksToTypeTheName_AndRemovesLinksContainerAndFolder()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var folder = new TempFolder();
        h.Windows.BackupHandler = async window =>
        {
            Assert.Equal(GpoBackupMode.BeforeDelete, window.Mode);
            Assert.True(window.BackupFirst);
            window.Folder = folder.Path;
            await window.RunCommand.ExecuteAsync(null);
            return window.Error is null;
        };
        h.B2.Dialogs.TypedConfirmAnswers.Enqueue(true);
        using var objects = await h.ObjectsAsync();
        objects.Selection = [objects.Rows.Single(r => r.Gpo.Id == h.Sample.WorkstationRemoteDesktop)];

        await objects.DeleteCommand.ExecuteAsync(null);

        var typed = Assert.Single(h.B2.Dialogs.TypedConfirmations);
        Assert.Equal("Stacje robocze – Pulpit zdalny", typed.Expected);
        Assert.Contains(h.B2.Builder.DomainDnsName, typed.Message, StringComparison.Ordinal);
        Assert.Contains("Remove-GPO", h.Windows.Previews.Single().PowerShell, StringComparison.Ordinal);
        Assert.False(h.Session.Exists(h.GpoDn(h.Sample.WorkstationRemoteDesktop)));
        Assert.False(h.Sysvol.FolderExists(h.Sample.WorkstationRemoteDesktop));
        Assert.DoesNotContain(h.Sample.WorkstationRemoteDesktop.ToString("D"), h.Value(h.Company.ComputersOu, Attr.GpLink) ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // The backup was written to the chosen folder only, and can be listed.
        var backup = Assert.Single(GpmcBackupFormatList(folder.Path));
        Assert.Equal(h.Sample.WorkstationRemoteDesktop, backup.GpoId);
        Assert.Equal(folder.Path, h.B2.Settings.Current.GroupPolicy.LastBackupFolder);
    }

    [Fact]
    public async Task Delete_TypedConfirmationRefused_WritesNothing()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.Windows.BackupHandler = async window =>
        {
            window.BackupFirst = false;
            await window.RunCommand.ExecuteAsync(null);
            return true;
        };
        h.B2.Dialogs.TypedConfirmAnswers.Enqueue(false);
        using var document = await h.GpoAsync(h.Sample.SitePrinters);

        await document.DeleteCommand.ExecuteAsync(null);

        Assert.True(h.Session.Exists(h.GpoDn(h.Sample.SitePrinters)));
        Assert.True(h.Sysvol.FolderExists(h.Sample.SitePrinters));
        Assert.False(h.B2.Settings.Current.GroupPolicy.BackupBeforeDelete);
    }

    [Fact]
    public async Task Delete_DefaultGpo_IsRefused()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var objects = await h.ObjectsAsync();
        objects.Selection = [objects.Rows.Single(r => r.Gpo.Id == GpoInfo.DefaultDomainPolicyId)];

        Assert.False(objects.DeleteCommand.CanExecute(null));
        Assert.False(await h.Actions.DeleteAsync(objects.Selection[0].Gpo));
        Assert.Contains(h.B2.Dialogs.Errors, e => e.Message.Contains("Default Domain Policy", StringComparison.Ordinal));
        Assert.Empty(h.Windows.Previews);
    }

    [Fact]
    public async Task LinkExisting_MoveEnforceDisableUnlink_ChangeGpLink()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var scope = await h.ScopeAsync(h.Company.ComputersOu);
        h.Windows.PickGpo = "Serwery – Zabezpieczenia";

        await scope.LinkExistingCommand.ExecuteAsync(null);
        var picker = Assert.Single(h.Windows.Pickers);
        Assert.DoesNotContain(picker.Items, i => i.Gpo.Id == h.Sample.WorkstationPasswords);
        await B2Harness.WaitUntilAsync(() => scope.Links.Count == 3);
        Assert.Equal("Serwery – Zabezpieczenia", scope.Links[2].GpoName);

        scope.SelectedLink = scope.Links[2];
        await scope.MoveUpCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => scope.Links[1].GpoName == "Serwery – Zabezpieczenia");
        Assert.Contains("Set-GPLink", h.Windows.Previews[^1].PowerShell, StringComparison.Ordinal);
        Assert.Contains("-Order 2", h.Windows.Previews[^1].PowerShell, StringComparison.Ordinal);

        scope.SelectedLink = scope.Links[1];
        await scope.ToggleEnforcedCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => scope.Links[1].Enforced);
        scope.SelectedLink = scope.Links[1];
        await scope.ToggleEnabledCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => !scope.Links[1].Enabled);

        var gpLink = GpLinkValue.Parse(h.Value(h.Company.ComputersOu, Attr.GpLink), h.DomainDn);
        var link = gpLink.Find(h.Sample.ServerSecurity)!;
        Assert.True(link.Enforced);
        Assert.False(link.Enabled);
        Assert.Equal(2, link.Order);

        scope.SelectedLink = scope.Links[1];
        await scope.UnlinkCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => scope.Links.Count == 2);
        Assert.Contains("Remove-GPLink", h.Windows.Previews[^1].PowerShell, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlockInheritance_TogglesGpOptions()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var scope = await h.ScopeAsync(h.Company.ComputersOu);

        await scope.ToggleBlockInheritanceCommand.ExecuteAsync(null);
        Assert.Equal("1", h.Value(h.Company.ComputersOu, GroupPolicyAttributes.GpOptions));
        Assert.Contains("Set-GPInheritance", h.Windows.Previews[^1].PowerShell, StringComparison.Ordinal);
        await B2Harness.WaitUntilAsync(() => scope.BlocksInheritance);
        Assert.Equal(["Serwery – Zabezpieczenia", "Stacje robocze – Zasady haseł"], scope.Inheritance.Select(i => i.GpoName));

        await scope.ToggleBlockInheritanceCommand.ExecuteAsync(null);
        Assert.Null(h.Value(h.Company.ComputersOu, GroupPolicyAttributes.GpOptions));
    }

    [Fact]
    public async Task StatusAndWmiFilter_AreAppliedFromTheGpoDocument()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.WorkstationRemoteDesktop);
        Assert.False(document.ApplyStatusCommand.CanExecute(null));

        document.SelectedStatus = document.StatusOptions.Single(o => o.Value == GpoStatus.UserSettingsDisabled);
        await document.ApplyStatusCommand.ExecuteAsync(null);
        Assert.Equal("1", h.Value(h.GpoDn(h.Sample.WorkstationRemoteDesktop), GroupPolicyAttributes.Flags));
        Assert.Contains("GpoStatus", h.Windows.Previews[^1].PowerShell, StringComparison.Ordinal);

        await B2Harness.WaitUntilAsync(() => document.WmiFilters.Count == 2 && !document.IsLoading);
        document.SelectedWmiFilter = document.WmiFilters.Single(o => o.Value is not null);
        await document.ApplyWmiFilterCommand.ExecuteAsync(null);
        Assert.Equal(h.B2.Builder.WmiFilterLinkValue(h.Sample.WmiFilterId), h.Value(h.GpoDn(h.Sample.WorkstationRemoteDesktop), GroupPolicyAttributes.WqlFilter));
    }

    [Fact]
    public async Task SecurityFiltering_AddAndRemove_SyncTheFolderAcl_AndWarnAboutMs16072()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.WorkstationPasswords);
        h.B2.Dialogs.PickedObjects.Enqueue([h.Company.GroupAccounting]);

        await document.AddSecurityFilterCommand.ExecuteAsync(null);

        var preview = h.Windows.Previews[^1];
        Assert.Contains(preview.Plan.Objects.SelectMany(o => o.Changes), c => c.Action == ChangeAction.ModifySecurity);
        Assert.Contains(preview.Plan.Objects.SelectMany(o => o.Changes), c => c.Action == ChangeAction.SyncGpoFolderSecurity);
        Assert.Contains("Set-GPPermission", preview.PowerShell, StringComparison.Ordinal);
        Assert.Contains("GpoApply", preview.PowerShell, StringComparison.Ordinal);
        await B2Harness.WaitUntilAsync(() => document.SecurityFilter.Count == 2);
        Assert.True(GpoFolderAcl.IsConsistent(h.Sysvol.FolderSecurity(h.Sample.WorkstationPasswords), (await h.Planner.Reader.ReadPolicyAsync(h.Session, h.Sample.WorkstationPasswords, CancellationToken.None))!.SecurityDescriptor));

        // Removing Authenticated Users keeps their Read (MS16-072); the other filter remains.
        document.SelectedFilterTrustee = document.SecurityFilter.Single(r => r.Sid == "S-1-5-11");
        await document.RemoveSecurityFilterCommand.ExecuteAsync(null);
        var gpo = await h.Planner.Reader.ReadPolicyAsync(h.Session, h.Sample.WorkstationPasswords, CancellationToken.None);
        Assert.Contains(GpoPermissionLevels.Read(gpo!.SecurityDescriptor), p => p.Sid == "S-1-5-11" && p.Level == GpoPermissionLevel.Read);
    }

    [Fact]
    public async Task Delegation_AddChangeRemove()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.ServerSecurity, GpoDocumentTab.Delegation);
        h.B2.Dialogs.PickedObjects.Enqueue([h.Company.MalgorzataWojcik]);
        document.SelectedLevel = document.LevelOptions.Single(o => o.Value == GpoPermissionLevel.Read);

        await document.AddDelegationCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => document.Delegation.Any(r => r.Name.EndsWith("malgorzata.wojcik", StringComparison.Ordinal)));
        var row = document.Delegation.Single(r => r.Name.EndsWith("malgorzata.wojcik", StringComparison.Ordinal));
        Assert.Equal(GpoPermissionLevel.Read, row.Level);
        Assert.Contains("GpoRead", h.Windows.Previews[^1].PowerShell, StringComparison.Ordinal);

        document.SelectedTrustee = row;
        document.SelectedLevel = document.LevelOptions.Single(o => o.Value == GpoPermissionLevel.EditDeleteModifySecurity);
        await document.ChangeLevelCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => document.Delegation.Single(r => r.Sid == row.Sid).Level == GpoPermissionLevel.EditDeleteModifySecurity);

        document.SelectedTrustee = document.Delegation.Single(r => r.Sid == row.Sid);
        await document.RemoveDelegationCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => document.Delegation.All(r => r.Sid != row.Sid));
    }

    [Fact]
    public async Task AlignVersions_RaisesBothHalves_AboveTheHigherOfAdAndGptIni()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.GpoAsync(h.Sample.DriveMappings, GpoDocumentTab.Status);

        await document.AlignVersionsCommand.ExecuteAsync(null);

        var gpo = await h.Planner.Reader.ReadPolicyAsync(h.Session, h.Sample.DriveMappings, CancellationToken.None);
        Assert.Equal(new GpoVersion(3, 1), gpo!.DirectoryVersion);
        Assert.Contains("Version=" + new GpoVersion(3, 1).ToInt32().ToString(CultureInfo.InvariantCulture), h.Sysvol.GetText(h.Sample.DriveMappings, "GPT.INI"), StringComparison.Ordinal);
        Assert.Contains(h.Windows.Previews[^1].Rows, r => r.Contains("komputer 1, użytkownik 3", StringComparison.Ordinal));
        await B2Harness.WaitUntilAsync(() => !document.HasVersionMismatch);
    }

    [Fact]
    public async Task Undo_InThePreview_RevertsALinkChange()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var before = h.Value(h.Company.ComputersOu, Attr.GpLink);
        h.Windows.UndoAfterExecute = true;

        Assert.True(await h.Actions.SetLinkOptionsAsync(h.Company.ComputersOu, h.Sample.WorkstationPasswords, "Stacje robocze – Zasady haseł", true, null));

        Assert.Equal(before, h.Value(h.Company.ComputersOu, Attr.GpLink));
    }

    [Fact]
    public async Task PreviewClosedWithoutExecuting_WritesNothing()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.Windows.ExecutePreviews = false;
        var before = h.Value(h.Company.ComputersOu, Attr.GpLink);

        Assert.False(await h.Actions.UnlinkAsync(h.Company.ComputersOu, h.Sample.WorkstationPasswords, "Stacje robocze – Zasady haseł"));

        Assert.Equal(before, h.Value(h.Company.ComputersOu, Attr.GpLink));
        Assert.Single(h.Windows.Previews);
    }

    [Fact]
    public async Task WmiFilters_CreateEditDelete()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var document = await h.WmiFiltersAsync();
        h.Windows.WmiEditorHandler = editor =>
        {
            editor.Name = "Tylko serwery – Łódź";
            editor.Description = "Serwery (ProductType = 3)";
            editor.Queries[0].Query = "SELECT * FROM Win32_OperatingSystem WHERE ProductType = 3";
            editor.OkCommand.Execute(null);
            return true;
        };

        await document.NewCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => document.Rows.Count == 2);
        var created = document.Rows.Single(r => r.Name == "Tylko serwery – Łódź");
        Assert.Contains("ProductType = 3", created.QueriesText, StringComparison.Ordinal);

        h.Windows.WmiEditorHandler = editor =>
        {
            Assert.Equal("Tylko serwery – Łódź", editor.Name);
            editor.AddQueryCommand.Execute(null);
            editor.Queries[1].Query = "SELECT * FROM Win32_ComputerSystem WHERE DomainRole > 1";
            editor.OkCommand.Execute(null);
            return true;
        };
        document.SelectedRow = created;
        await document.EditCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => document.Rows.Single(r => r.Name == "Tylko serwery – Łódź").QueriesText.Contains("DomainRole", StringComparison.Ordinal));

        document.SelectedRow = document.Rows.Single(r => r.Name == "Tylko serwery – Łódź");
        await document.DeleteCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => document.Rows.Count == 1);
    }

    [Fact]
    public async Task WmiEditor_ValidatesNameAndQueries()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var editor = new WmiFilterEditorViewModel(h.Ui, null);

        Assert.Equal("Wpisz nazwę.", editor.ValidationError);
        editor.Name = "Stacje";
        Assert.Equal("Każde zapytanie wymaga przestrzeni nazw i zapytania WQL.", editor.ValidationError);
        editor.Queries[0].Query = "SELECT * FROM Win32_OperatingSystem";
        Assert.Null(editor.ValidationError);
        Assert.False(editor.RemoveQueryCommand.CanExecute(null));
        editor.OkCommand.Execute(null);
        Assert.Equal(@"root\CIMv2", editor.Result!.Queries.Single().Namespace);
    }

    [Fact]
    public async Task NewGpoDialog_ValidatesTheName()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var dialog = new NewGpoViewModel(h.Ui, NewGpoMode.New, "  ");

        Assert.False(dialog.OkCommand.CanExecute(null));
        dialog.Name = new string('ż', GroupPolicyPlanner.MaxNameLength + 1);
        Assert.Contains("255", dialog.NameError, StringComparison.Ordinal);
        dialog.Name = "  Kadry – drukarki  ";
        dialog.OkCommand.Execute(null);
        Assert.Equal("Kadry – drukarki", dialog.Result);
    }

    private static IReadOnlyList<Core.GroupPolicy.Backup.GpoBackupEntry> GpmcBackupFormatList(string folder) =>
        Core.GroupPolicy.Backup.GpmcBackupFormat.List(folder);
}

/// <summary>A test-only temporary folder (deleted afterwards): the folder "the operator chooses" for backups.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "admng-k2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
