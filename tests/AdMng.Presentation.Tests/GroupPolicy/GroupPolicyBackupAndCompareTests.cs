using System.Globalization;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Backup;
using AdMng.Core.GroupPolicy.Comparison;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>Backup, restore, import (into a test-only folder) and the comparison of two GPOs (design note 5.5, 5.6).</summary>
public sealed class GroupPolicyBackupAndCompareTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task Backup_WritesGpmcLayoutToTheChosenFolder_AsAnActivity()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var folder = new TempFolder();
        h.Windows.Folder = folder.Path;
        h.Windows.BackupHandler = async window =>
        {
            Assert.Equal(GpoBackupMode.Backup, window.Mode);
            Assert.Contains("do potwierdzenia w pilotażu", window.CompatibilityNote, StringComparison.Ordinal);
            await window.BrowseCommand.ExecuteAsync(null);
            window.Comment = "Przed zmianą haseł – Łukasz";
            Assert.Contains("Backup-GPO", window.PowerShell, StringComparison.Ordinal);
            Assert.Contains(folder.Path, window.PowerShell, StringComparison.Ordinal);
            await window.RunCommand.ExecuteAsync(null);
            Assert.Null(window.Error);
            return true;
        };
        using var objects = await h.ObjectsAsync();
        objects.Selection = [.. objects.Rows.Where(r => r.Gpo.Id == h.Sample.WorkstationPasswords || r.Gpo.Id == h.Sample.DriveMappings)];

        await objects.BackupCommand.ExecuteAsync(null);

        var entries = GpmcBackupFormat.List(folder.Path);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("Przed zmianą haseł – Łukasz", e.Comment));
        Assert.True(File.Exists(Path.Combine(folder.Path, GpmcBackupFormat.ManifestFile)));
        var activity = Assert.Single(h.B2.Activities.Activities);
        Assert.Equal(2, activity.SucceededCount);
        Assert.Equal(folder.Path, h.B2.Settings.Current.GroupPolicy.LastBackupFolder);
        Assert.Empty(h.Sysvol.Writes);
    }

    [Fact]
    public async Task Backup_OfAGpoWithSettingsKeptInTheDirectory_SaysTheBackupLacksThem()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var folder = new TempFolder();
        h.Session.Seed("CN=Class Store,CN=Machine," + GpLinkValue.GpoDn(h.Sample.WorkstationPasswords, h.Session.Info.DefaultNamingContext),
            new Dictionary<string, object?> { ["objectClass"] = new[] { "top", "container" } });
        string? status = null;
        h.Windows.BackupHandler = async window =>
        {
            window.Folder = folder.Path;
            await window.RunCommand.ExecuteAsync(null);
            Assert.Null(window.Error);
            status = window.StatusText;
            return true;
        };
        var gpo = (await h.Planner.Reader.ReadPolicyAsync(h.Session, h.Sample.WorkstationPasswords, CancellationToken.None))!;

        Assert.True(await h.Actions.BackupAsync([gpo]));

        Assert.Contains("obiekty katalogu", status, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(GpmcBackupFormat.List(folder.Path)) is { } entry
            ? (await GpmcBackupFormat.ReadAsync(folder.Path, entry.BackupId, CancellationToken.None)).DirectoryObjectCount
            : 0);
    }

    [Fact]
    public async Task Restore_DeletedGpo_FromTheBackup_WithItsLinks()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var folder = new TempFolder();
        await BackupAsync(h, folder.Path, h.Sample.WorkstationRemoteDesktop);
        var original = h.Sysvol.Get(h.Sample.WorkstationRemoteDesktop, SecurityTemplate.RelativePath);

        // Deleted (without a backup prompt), then restored with its link.
        h.Windows.BackupHandler = async window =>
        {
            window.BackupFirst = false;
            await window.RunCommand.ExecuteAsync(null);
            return true;
        };
        h.B2.Dialogs.TypedConfirmAnswers.Enqueue(true);
        var gpo = (await h.Planner.Reader.ReadPolicyAsync(h.Session, h.Sample.WorkstationRemoteDesktop, CancellationToken.None))!;
        Assert.True(await h.Actions.DeleteAsync(gpo));
        Assert.False(h.Session.Exists(h.GpoDn(h.Sample.WorkstationRemoteDesktop)));

        h.Windows.BackupHandler = async window =>
        {
            Assert.Equal(GpoBackupMode.Restore, window.Mode);
            Assert.Equal(folder.Path, window.Folder);
            await B2Harness.WaitUntilAsync(() => window.SelectedBackup is not null && window.CanRestoreLinks);
            Assert.Equal("Przywróć też połączenia (1)", window.RestoreLinksText);
            window.RestoreLinks = true;
            await window.RunCommand.ExecuteAsync(null);
            return window.ChangedDirectory;
        };
        using var objects = await h.ObjectsAsync();
        await objects.RestoreCommand.ExecuteAsync(null);

        Assert.True(h.Session.Exists(h.GpoDn(h.Sample.WorkstationRemoteDesktop)));
        Assert.Equal(original, h.Sysvol.Get(h.Sample.WorkstationRemoteDesktop, SecurityTemplate.RelativePath));
        Assert.Contains(h.Sample.WorkstationRemoteDesktop.ToString("D"), h.Value(h.Company.ComputersOu, Core.ActiveDirectory.Attr.GpLink) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restore-GPO", h.Windows.Previews[^1].PowerShell, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_ReadsTheSelectedBackupOffTheCallingThread()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "A FIFO stands in for a hanging share (mkfifo).");
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var folder = new TempFolder();
        await BackupAsync(h, folder.Path, h.Sample.WorkstationPasswords);
        var entry = Assert.Single(GpmcBackupFormat.List(folder.Path));

        // Backup.xml becomes a FIFO: reading it blocks until a writer opens it, like a file on a hanging share.
        var xml = Path.Combine(folder.Path, SysvolPaths.FolderName(entry.BackupId), GpmcBackupFormat.BackupFile);
        File.Delete(xml);
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [xml]))
        {
            await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, mkfifo.ExitCode);
        }

        var ct = TestContext.Current.CancellationToken;
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Windows.BackupHandler = async window =>
        {
            shown.TrySetResult();
            await released.Task;

            // The window stays open until the read got the bytes (not XML) and reported it.
            await B2Harness.WaitUntilAsync(() => window.Error is not null);
            return false;
        };
        using var objects = await h.ObjectsAsync();
        var restore = Task.Run(() => objects.RestoreCommand.ExecuteAsync(null), ct);

        // The window opens (its first backup selected) while the read still waits for the FIFO.
        var opened = await Task.WhenAny(shown.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)) == shown.Task;

        // Opened read-write, the FIFO does not block us; the handle stays a writer until the read is over.
        await using (var writer = new FileStream(xml, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1))
        {
            await writer.WriteAsync("nie XML – Łódź"u8.ToArray(), ct);
            await writer.FlushAsync(ct);
            released.TrySetResult();
            await restore;
        }

        Assert.True(opened, "The window waited for the backup read.");
    }

    [Fact]
    public async Task Import_IntoAnotherGpo_CopiesTheSettings_AndRaisesItsVersion()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var folder = new TempFolder();
        await BackupAsync(h, folder.Path, h.Sample.WorkstationPasswords);
        h.Windows.BackupHandler = async window =>
        {
            Assert.Equal(GpoBackupMode.Import, window.Mode);
            Assert.Contains("Lokacja Warszawa – Drukarki", window.WindowTitle, StringComparison.Ordinal);
            await B2Harness.WaitUntilAsync(() => window.SelectedBackup is not null && window.RunCommand.CanExecute(null));
            Assert.False(window.CanRestoreLinks);
            await window.RunCommand.ExecuteAsync(null);
            return window.ChangedDirectory;
        };
        using var document = await h.GpoAsync(h.Sample.SitePrinters);

        Assert.True(await h.Actions.ImportAsync(document.Gpo!));

        Assert.Equal(h.Sysvol.Get(h.Sample.WorkstationPasswords, SecurityTemplate.RelativePath), h.Sysvol.Get(h.Sample.SitePrinters, SecurityTemplate.RelativePath));
        var printers = await h.Planner.Reader.ReadPolicyAsync(h.Session, h.Sample.SitePrinters, CancellationToken.None);
        Assert.Equal(new GpoVersion(2, 1), printers!.DirectoryVersion);
        Assert.Contains("Import-GPO", h.Windows.Previews[^1].PowerShell, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_FolderWithoutBackups_SaysSo()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var folder = new TempFolder();
        using var window = new GpoBackupViewModel(h.Services, GpoBackupMode.Restore, []);
        window.Folder = folder.Path;

        await window.LoadBackupsCommand.ExecuteAsync(null);

        Assert.Empty(window.Backups);
        Assert.Equal("Brak kopii zapasowych w tym folderze.", window.StatusText);
        Assert.False(window.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Compare_TwoGpos_ShowsDifferencesInEverySection_AndExportsCsvOnDemand()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        using var objects = await h.ObjectsAsync();
        objects.Selection = [.. objects.Rows.Where(r => r.Gpo.Id == h.Sample.WorkstationPasswords || r.Gpo.Id == h.Sample.WorkstationRemoteDesktop)];

        await objects.CompareCommand.ExecuteAsync(null);

        using var comparison = Assert.Single(h.Windows.Comparisons);
        Assert.Null(comparison.Error);
        Assert.True(comparison.OnlyDifferences);
        Assert.All(comparison.Rows, r => Assert.True(r.IsDifferent));
        Assert.Contains(comparison.Rows, r => r.Row.Section == GpoComparisonSection.Core && r.Name == "Filtr WMI" && (r.ValueA == "Tylko stacje robocze" || r.ValueB == "Tylko stacje robocze"));
        Assert.Contains(comparison.Rows, r => r.Row.Section == GpoComparisonSection.Permissions && r.Name == "CONTOSO\\GG_IT");
        Assert.Contains(comparison.Rows, r => r.Row.Section == GpoComparisonSection.Settings && r.Name.EndsWith("MinimumPasswordLength", StringComparison.Ordinal));
        Assert.Contains(comparison.Rows, r => r.Row.Section == GpoComparisonSection.Files);
        Assert.Contains(comparison.Rows, r => r.Row.Section == GpoComparisonSection.Links && (r.ValueA + r.ValueB).Contains("włączone: Nie", StringComparison.Ordinal));
        var all = comparison.Rows.Count;
        comparison.OnlyDifferences = false;
        Assert.True(comparison.Rows.Count > all);

        using var folder = new TempFolder();
        var csv = Path.Combine(folder.Path, "porównanie.csv");
        h.B2.Files.NextSavePath = csv;
        await comparison.ExportCommand.ExecuteAsync(null);
        var text = await File.ReadAllTextAsync(csv, TestContext.Current.CancellationToken);
        Assert.Contains("Stacje robocze – Zasady haseł", text, StringComparison.Ordinal);
        Assert.Contains("Tylko stacje robocze", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_MaskedSettings_AreNeverShown()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.Sysvol.PutText(h.Sample.SitePrinters, "Machine/Preferences/Groups/Groups.xml", FakeDirectoryBuilder.Groups(
            "<User clsid=\"{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}\" name=\"Administrator (wbudowany)\" image=\"2\" changed=\"2026-10-20 10:00:00\" uid=\"{11111111-2222-3333-4444-555555555555}\"><Properties action=\"U\" newName=\"\" fullName=\"\" description=\"\" cpassword=\"j1Uyj3Vx8TY9LtLZil2uAuZkFQA/4latT76ZwgdHdhw\" changeLogon=\"0\" noChange=\"0\" neverExpires=\"0\" acctDisabled=\"0\" userName=\"Administrator (wbudowany)\"/></User>"));
        var a = (await h.Planner.Reader.ReadPolicyAsync(h.Session, h.Sample.SitePrinters, CancellationToken.None))!;
        var b = (await h.Planner.Reader.ReadPolicyAsync(h.Session, h.Sample.DriveMappings, CancellationToken.None))!;
        using var comparison = new GpoComparisonViewModel(h.Services, a, b);

        await comparison.InitializeAsync();
        comparison.OnlyDifferences = false;

        Assert.DoesNotContain(comparison.Rows, r => r.ValueA.Contains("j1Uyj3Vx8TY9LtLZil2uAuZkFQA", StringComparison.Ordinal));
        Assert.Contains(comparison.Rows, r => r.ValueA == "(ustawione – ukryte)");
    }

    [Fact]
    public async Task Compare_WithoutSysvol_ComparesTheContainersOnly()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        h.Sysvol.Unavailable = SysvolUnavailableReason.AccessDenied;
        var a = (await h.Planner.Reader.ReadPolicyAsync(h.Session, h.Sample.WorkstationPasswords, CancellationToken.None))!;
        var b = (await h.Planner.Reader.ReadPolicyAsync(h.Session, h.Sample.ServerSecurity, CancellationToken.None))!;
        using var comparison = new GpoComparisonViewModel(h.Services, a, b);

        await comparison.InitializeAsync();

        Assert.Contains("nie porównano", comparison.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain(comparison.Rows, r => r.Row.Section is GpoComparisonSection.Settings or GpoComparisonSection.Files);
        Assert.Contains(comparison.Rows, r => r.Row.Section == GpoComparisonSection.Core);
    }

    private static async Task BackupAsync(GroupPolicyHarness h, string folder, Guid gpoId)
    {
        h.Windows.BackupHandler = async window =>
        {
            window.Folder = folder;
            await window.RunCommand.ExecuteAsync(null);
            Assert.Null(window.Error);
            return true;
        };
        var gpo = (await h.Planner.Reader.ReadPolicyAsync(h.Session, gpoId, CancellationToken.None))!;
        Assert.True(await h.Actions.BackupAsync([gpo]));
        h.Windows.BackupHandler = null;
    }
}
