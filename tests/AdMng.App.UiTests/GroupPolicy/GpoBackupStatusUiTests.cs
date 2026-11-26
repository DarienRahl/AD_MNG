using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Backup;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using Microsoft.Extensions.DependencyInjection;
using static AdMng.App.UiTests.GroupPolicy.GroupPolicyStatusUiSupport;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The status line of the window that backs up, restores and imports GPOs (track V, stage V2b-3;
/// docs/dev/v-interface-renewal.md "V2b-3 as built"), through the real window over the fake directory: one line at the
/// top, the main button as an action button that is also the Cancel of the backup it started, the count of backups as the
/// summary, a backup that was stopped with its action, a backup that could not be read with Retry, and a backup of another
/// domain as a note under the list. A slow domain controller is a read the test holds and releases, a damaged backup a file
/// the test writes; every state that is asserted lasts until the test changes it. What the window writes and plans is
/// covered by <see cref="GroupPolicyWindowTests"/>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GpoBackupStatusUiTests
{
    [Fact]
    public Task TheWindow_HasOneStatusLine_AtTheTop_AndTheBackupsOfTheFolderAreItsSummary() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        using var folder = new TempFolder();
        await BackUpAsync(context, folder.Path, sample.WorkstationPasswords);

        var (window, backup) = Open(context, GpoBackupMode.Restore, folder.Path);
        await UiWait.InitializedAsync(window.Initialization);
        var line = window.PageStatus;
        await UiWait.UntilAsync(() => backup.BackupDetails is not null && !backup.Status.IsWorking, "the folder and the backup were read");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // One status line, named PageStatus, over the status of the window, at the top - above the title and the fields -
        // of the fixed height, and it says how many backups the folder holds, where the footer said it before.
        Assert.Same(line, Assert.Single(UiWait.Descendants<StatusLine>(window)));
        Assert.True(line.IsVisible);
        Assert.IsAssignableFrom<IHasPageStatus>(backup);
        Assert.Same(backup.Status.Current, line.Status);
        Assert.Same(backup.Status.CloseCommand, line.CloseCommand);
        Assert.True(TopOf(line, window) < TopOf(window.FolderBox, window), "the status line is not above the folder");
        Assert.True(TopOf(window.FolderBox, window) < TopOf(window.BackupList, window), "the folder is not above the backups");
        Assert.Equal((double)Application.Current.FindResource("Ad.Size.StatusLine"), line.ActualHeight);
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal("Backups in the folder: 1", line.Message);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);

        // The main button is an action button, the default button, in the accent of the dialogs; Close is the cancel button.
        Assert.Equal(ActionButtonState.Idle, window.RunButton.State);
        Assert.Same(backup.RunCommand, window.RunButton.Command);
        Assert.Same(backup.Status.CancelCommand, window.RunButton.CancelCommand);
        Assert.True(window.RunButton.IsDefault);
        Assert.Same(Application.Current.FindResource("Ad.AccentDialogActionButton"), window.RunButton.Style);
        Assert.Equal("Restore…", Assert.IsType<string>(window.RunButton.Content));
        Assert.True(window.RunButton.IsEnabled);
        Assert.True(window.CloseButton.IsCancel);
        Assert.False(window.OtherDomainNote.IsVisible);
        AssertNoOldFeedback(window);
        context.AssertHealthy();
    });

    [Fact]
    public Task TheBackup_SaysBackingUpWithThePartDone_TheMainButtonIsItsCancel_AndAStoppedBackupIsAttentionWithTheActionThatStartsItAgain() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        using var folder = new TempFolder();
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var gpos = new[] { sample.WorkstationPasswords, sample.DriveMappings };
        var infos = new List<GpoInfo>();
        foreach (var id in gpos)
        {
            infos.Add((await gp.Reader.ReadPolicyAsync(context.Directory.Session, id, CancellationToken.None))!);
        }

        // The second GPO is read by a call the test holds (on a pool thread): one backup is written, "backing up" lasts.
        var second = sample.DriveMappings.ToString("B");
        using var held = new HeldRead(context.Directory.Session, operation => operation.DistinguishedName.Contains(second, StringComparison.OrdinalIgnoreCase));
        var backup = new GpoBackupViewModel(gp, GpoBackupMode.Backup, infos) { Folder = folder.Path };
        var window = (GpoBackupWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(backup);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var line = window.PageStatus;
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");
        var ring = ControlHost.Part<ProgressRing>(line, "PART_Ring");
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Message);
        var contentTop = TopOf(window.FolderBox, window);

        ControlHost.Press(window.RunButton);
        await UiWait.UntilAsync(() => held.IsReached && line.Kind == PageStatusKind.Working, "the backup is running");

        // Working: how far the backup is, with Stop in the line - and the button that started it says "Cancel". The line
        // was there before: nothing moved. The window cannot be closed meanwhile.
        Assert.Equal("Working", line.Title);
        Assert.Equal("Backing up the GPOs: 1 of 2…", line.Message);
        Assert.True(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Stop", Assert.IsType<string>(action.Content));
        Assert.Same(backup.Status.CancelCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.Equal(ActionButtonState.Running, window.RunButton.State);
        Assert.True(window.RunButton.IsEnabled);
        Assert.False(window.CloseButton.IsEnabled);
        AssertNoOldFeedback(window);
        window.UpdateLayout();
        Assert.Equal(contentTop, TopOf(window.FolderBox, window));

        // Cancel, pressed on the button that started the backup: "Stopping…" at once, in the button and in the line.
        ControlHost.Press(window.RunButton);
        await UiWait.UntilAsync(() => window.RunButton.State == ActionButtonState.Cancelling && backup.Status.IsCancelling && line.Message == "Stopping…", "the backup is stopping");
        Assert.Equal(PageStatusKind.Working, line.Kind);
        held.Release();
        await UiWait.UntilAsync(() => !backup.IsBusy && line.Kind == PageStatusKind.Attention, "the backup was stopped");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        // Stopped: one backup is in the folder, the window stays, and the button that backs up is the action of the line.
        Assert.Equal("Attention", line.Title);
        Assert.Equal("The backup was stopped: 1 of 2 GPOs were written to the folder. Press “Back Up” to try again.", line.Message);
        Assert.Equal(AppIcons.StatusWarning, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.False(ring.IsActive);
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Back Up", Assert.IsType<string>(action.Content));
        Assert.Same(backup.RunCommand, action.Command);
        Assert.Equal(Visibility.Visible, close.Visibility);
        Assert.Equal(ActionButtonState.Idle, window.RunButton.State);
        Assert.True(window.CloseButton.IsEnabled);
        Assert.Single(GpmcBackupFormat.List(folder.Path));
        Assert.True(window.IsVisible);
        window.UpdateLayout();
        Assert.Equal(contentTop + GrowthOf(line), TopOf(window.FolderBox, window), 0.5);

        // The action of the line backs up again, the window closes - and what it said is gone with it.
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => window.Result == true, "the window closed after the backup");
        Assert.False(window.IsVisible);
        Assert.Equal(3, GpmcBackupFormat.List(folder.Path).Count); // the stopped run's one and the two of the second run
        context.AssertHealthy();
    });

    [Fact]
    public Task ABackupThatCouldNotBeRead_IsAnErrorWithRetry_ABackupOfAnotherDomainIsANoteUnderTheList_AndTheEndOfTheSessionLasts() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        using var folder = new TempFolder();
        await BackUpAsync(context, folder.Path, sample.WorkstationPasswords);
        var entry = Assert.Single(GpmcBackupFormat.List(folder.Path));
        var xml = Path.Combine(folder.Path, SysvolPaths.FolderName(entry.BackupId), GpmcBackupFormat.BackupFile);
        var original = await File.ReadAllBytesAsync(xml);
        await File.WriteAllTextAsync(xml, "to nie jest XML – Łódź");

        var (window, backup) = Open(context, GpoBackupMode.Restore, folder.Path);
        await UiWait.InitializedAsync(window.Initialization);
        var line = window.PageStatus;
        await UiWait.UntilAsync(() => line.Kind == PageStatusKind.Error, "the backup could not be read");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var action = ControlHost.Part<Button>(line, "PART_ActionButton");
        var close = ControlHost.Part<Button>(line, "PART_CloseButton");

        // The error of that selection: what was tried, the reason, what to do, the code - with Retry, and it cannot be closed.
        Assert.Equal("Error", line.Title);
        Assert.Equal(
            $"Could not read the backup of “Stacje robocze – Zasady haseł”: {context.Localizer()[L.GroupPolicy.BackupInvalid]} Press Retry to try again. (GpoBackupException)",
            line.Message);
        Assert.Equal(AppIcons.StatusError, line.IconKey);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(line));
        Assert.Equal(Visibility.Visible, action.Visibility);
        Assert.Equal("Retry", Assert.IsType<string>(action.Content));
        Assert.Same(backup.RetryBackupCommand, action.Command);
        Assert.Equal(Visibility.Collapsed, close.Visibility);
        Assert.False(window.RunButton.Command.CanExecute(null));
        Assert.Single(window.BackupList.Items);
        AssertNoOldFeedback(window);

        // The file is whole again: Retry reads the backup - the line goes back to the count of backups.
        await File.WriteAllBytesAsync(xml, original);
        await UiWait.UntilAsync(() => action.IsEnabled, "the action of the line can be pressed");
        ControlHost.Press(action);
        await UiWait.UntilAsync(() => backup.BackupDetails is not null && line.Kind == PageStatusKind.Idle, "the backup was read");
        Assert.Equal("Backups in the folder: 1", line.Message);
        Assert.True(window.RunButton.Command.CanExecute(null));

        // The same backup, said to come from another domain: a note under the list - glyph and words - and no status.
        window.Close();
        var manifest = Path.Combine(folder.Path, GpmcBackupFormat.ManifestFile);
        File.SetAttributes(manifest, FileAttributes.Normal); // GPMC's manifest.xml is written read-only (Windows refuses to overwrite it)
        await File.WriteAllTextAsync(manifest, (await File.ReadAllTextAsync(manifest)).Replace("contoso.test", "fabrikam.test", StringComparison.OrdinalIgnoreCase));
        var (foreign, foreignBackup) = Open(context, GpoBackupMode.Restore, folder.Path);
        await UiWait.InitializedAsync(foreign.Initialization);
        await UiWait.UntilAsync(() => foreignBackup.BackupDetails is not null && !foreignBackup.Status.IsWorking, "the foreign backup was read");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(foreign.OtherDomainNote.IsVisible);
        Assert.Same(Application.Current.FindResource("Ad.Note.Attention"), foreign.OtherDomainNote.Style);
        Assert.Equal("The backup comes from the domain fabrikam.test: restore it there, or import its settings into a GPO of this domain.", foreign.OtherDomainTextBlock.Text);
        Assert.True(TopOf(foreign.BackupList, foreign) < TopOf(foreign.OtherDomainNote, foreign), "the note is not under the list");
        Assert.True(TopOf(foreign.OtherDomainNote, foreign) < TopOf(foreign.BackupDetailsText, foreign), "the note is not above the details");
        Assert.Equal(PageStatusKind.Idle, foreign.PageStatus.Kind);

        // The session of the window ends: an error that cannot be closed takes the line; the window can still be closed.
        await context.Services.GetRequiredService<ISessionManager>().DisconnectAsync();
        await UiWait.UntilAsync(() => foreignBackup.IsSessionEnded && foreign.PageStatus.Kind == PageStatusKind.Error, "the end of the session in the line");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(context.Localizer()[L.WindowSession.Ended], foreign.PageStatus.Message);
        Assert.False(foreign.PageStatus.HasAction);
        Assert.False(foreign.PageStatus.CanClose);
        Assert.True(foreign.CloseButton.IsEnabled);
        context.AssertHealthy();
    });

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The window in a mode that lists backups, over <paramref name="folder"/>, shown off-screen (its folder is read at once).</summary>
    private static (GpoBackupWindow Window, GpoBackupViewModel Backup) Open(UiTestContext context, GpoBackupMode mode, string folder)
    {
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var backup = new GpoBackupViewModel(gp, mode, []) { Folder = folder };
        var window = (GpoBackupWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(backup);
        UiTestSupport.ShowOffScreen(window);
        return (window, backup);
    }

    /// <summary>Writes the backups of the GPOs into the folder (a window's view model of its own, as the operator would).</summary>
    private static async Task BackUpAsync(UiTestContext context, string folder, params Guid[] gpos)
    {
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var infos = new List<GpoInfo>();
        foreach (var id in gpos)
        {
            infos.Add((await gp.Reader.ReadPolicyAsync(context.Directory.Session, id, CancellationToken.None))!);
        }

        using var backup = new GpoBackupViewModel(gp, GpoBackupMode.Backup, infos) { Folder = folder };
        await backup.RunCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !backup.IsBusy && GpmcBackupFormat.List(folder).Count == gpos.Length, "the backups were written");
    }

    /// <summary>The old places of feedback are gone from the window: no progress bar of its own. The banners of before cannot come back - XamlSourceTests guards every view.</summary>
    private static void AssertNoOldFeedback(GpoBackupWindow window) => GroupPolicyStatusUiSupport.AssertNoOldFeedback(window);

    /// <summary>A folder that goes with the test.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "admng-v2b3-backup-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
