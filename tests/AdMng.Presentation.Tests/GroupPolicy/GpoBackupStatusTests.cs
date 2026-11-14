using System.Diagnostics;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Backup;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// The status line of the window that backs up, restores and imports GPOs (track V, stage V2b-3;
/// docs/dev/v-interface-renewal.md "V2b-3 as built"): its operations with Stop - the read of the folder, the read of the
/// selected backup (a holder of its own, in front), the backup with the part done, the plan of a restore or an import up to
/// the change plan preview -, what they said when they ended, the count of backups as the summary, the note about a backup of
/// another domain, and the state that lasts. What the window writes and plans is covered, unchanged, by
/// <see cref="GroupPolicyBackupAndCompareTests"/>.
/// </summary>
public sealed class GpoBackupStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- The backup ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Backing up the GPOs…", "Backing up the GPOs: 1 of 2…", "Backing up the GPOs: 2 of 2…")]
    [InlineData("pl-PL", "Tworzenie kopii zapasowych GPO…", "Tworzenie kopii zapasowych GPO: 1 z 2…", "Tworzenie kopii zapasowych GPO: 2 z 2…")]
    public async Task TheBackup_IsTheOperationOfTheWindow_WithThePartDone_AndSaysNothingWhenItEndedWell(string culture, string start, string first, string second)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        using var window = await NewAsync(h, GpoBackupMode.Backup, folder.Path, h.Sample.WorkstationPasswords, h.Sample.DriveMappings);
        Assert.IsAssignableFrom<IHasPageStatus>(window);
        bool? closed = null;
        window.CloseRequested += (_, result) => closed = result;
        var seen = new StatusRecorder(window.Status);

        // The second GPO is read by a call the test holds: one backup is written, the line says how far it is.
        using var held = new HeldDirectoryCall(h.Session, operation => IsOf(operation, h.Sample.DriveMappings));
        var running = Task.Run(() => window.RunCommand.ExecuteAsync(null), Ct);
        await held.Reached.WaitAsync(Limit, Ct);

        Assert.True(window.IsBusy);
        Assert.True(window.Status.IsWorking);
        var working = window.Status.Current;
        Assert.Equal((PageStatusKind.Working, first), (working.Kind, working.Message));
        Assert.Equal(0.5, working.Progress?.Fraction);
        Assert.Equal("GpoBackup.Status.BackingUp (1, 2)", working.Log.ToString());
        Assert.Same(window.Status.CancelCommand, Assert.IsType<PageStatusAction>(working.Action).Command);
        Assert.False(window.CloseCommand.CanExecute(null));

        held.Release();
        await running.WaitAsync(Limit, Ct);

        // Ended well: the window closes, the toast of the main window says it - the line has nothing to add.
        Assert.True(closed);
        Assert.Equal(
            [(PageStatusKind.Working, start), (PageStatusKind.Working, first), (PageStatusKind.Working, second), (PageStatusKind.Idle, string.Empty)],
            seen.Plain);
        Assert.False(window.IsBusy);
        Assert.Null(window.Error);
        Assert.Null(window.Status.Lasting);
        Assert.Equal(2, GpmcBackupFormat.List(folder.Path).Count);
        Assert.Contains(h.B2.Notifications.Items, n => n.Severity == NotificationSeverity.Success);
    }

    [Theory]
    [InlineData("en-US", "Stopping…", "The backup was stopped: 1 of 2 GPOs were written to the folder. Press “Back Up” to try again.", "Back Up")]
    [InlineData("pl-PL", "Zatrzymywanie…", "Tworzenie kopii zapasowych zatrzymano: do folderu zapisano 1 z 2 obiektów GPO. Wybierz „Utwórz kopię”, aby spróbować ponownie.", "Utwórz kopię")]
    public async Task Stop_DuringTheBackup_ReturnsControl_TheLineSaysHowManyWereWritten_AndTheButtonOfTheWindowStartsItAgain(
        string culture, string stopping, string stoppedText, string runLabel)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        using var window = await NewAsync(h, GpoBackupMode.Backup, folder.Path, h.Sample.WorkstationPasswords, h.Sample.DriveMappings);
        bool? closed = null;
        window.CloseRequested += (_, result) => closed = result;
        using var held = new HeldDirectoryCall(h.Session, operation => IsOf(operation, h.Sample.DriveMappings));
        var running = Task.Run(() => window.RunCommand.ExecuteAsync(null), Ct);
        await held.Reached.WaitAsync(Limit, Ct);

        // Stop answers at once: "Stopping…" on the page, although the read that is on its way still has to return.
        window.Status.CancelCommand.Execute(null);

        Assert.True(window.Status.IsCancelling);
        Assert.Equal(stopping, window.Status.Current.Message);
        Assert.True(window.IsBusy);
        held.Release();
        await running.WaitAsync(Limit, Ct);

        // Stopped: what was written is in the folder, the window stays, and the button that backs up is the action.
        Assert.Null(closed);
        Assert.False(window.IsBusy);
        var state = window.Status.Current;
        Assert.Equal((PageStatusKind.Attention, stoppedText), (state.Kind, state.Message));
        Assert.Equal("GpoBackup.Status.BackupStopped (1, 2)", state.Log.ToString());
        Assert.True(state.CanClose);
        var again = Assert.IsType<PageStatusAction>(state.Action);
        Assert.Equal(runLabel, again.Label);
        Assert.Same(window.RunCommand, again.Command);
        Assert.Single(GpmcBackupFormat.List(folder.Path));
        Assert.Equal(h.B2.Localizer[L.Activity.Cancelled], window.StatusText);

        // Closed, the line says nothing; the action of the line backs up again and the window closes.
        window.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        held.Release();
        await window.RunCommand.ExecuteAsync(null);
        Assert.True(closed);
    }

    [Theory]
    [InlineData("en-US", "Could not back up 1 of 2 GPOs: ")]
    [InlineData("pl-PL", "Nie udało się utworzyć kopii zapasowych 1 z 2 obiektów GPO: ")]
    public async Task ABackupThatFailedForOneGpo_IsAnErrorThatCanBeClosed_WithTheReasonAndTheCode_AndTheWindowStays(string culture, string lead)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        using var window = await NewAsync(h, GpoBackupMode.Backup, folder.Path, h.Sample.WorkstationPasswords, h.Sample.DriveMappings);
        bool? closed = null;
        window.CloseRequested += (_, result) => closed = result;
        h.Session.BeforeOperation = operation =>
        {
            if (IsOf(operation, h.Sample.DriveMappings))
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "00002098: SecErr: DSID-03150F94");
            }
        };

        await window.RunCommand.ExecuteAsync(null);

        Assert.Null(closed);
        var failed = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(" (AccessDenied)", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DSID", failed.Message, StringComparison.Ordinal);
        Assert.Equal("GpoBackup.Status.BackupFailed (1, 2) [AccessDenied]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(failed.Action);
        Assert.NotNull(window.Error); // the sentence the window always had
        Assert.Single(GpmcBackupFormat.List(folder.Path));
        Assert.False(window.IsBusy);
        Assert.True(window.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhenSysvolCannotBeOpened_NothingIsBackedUp_AndTheLineSaysWhyWithRetry()
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        using var folder = new TempFolder();
        using var window = await NewAsync(h, GpoBackupMode.Backup, folder.Path, h.Sample.WorkstationPasswords);
        h.Sysvol.Unavailable = SysvolUnavailableReason.AccessDenied;
        var seen = new StatusRecorder(window.Status);

        await window.RunCommand.ExecuteAsync(null);

        Assert.Equal([PageStatusKind.Working, PageStatusKind.Attention], seen.Plain.Select(s => s.Kind).ToList());
        var state = window.Status.Current;
        Assert.StartsWith("The backup was not made: ", state.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Press Retry to try again.", state.Message, StringComparison.Ordinal);
        Assert.Equal("GroupPolicy.SysvolFailedShort", state.Log.ToString());
        var retry = Assert.IsType<PageStatusAction>(state.Action);
        Assert.Equal("Retry", retry.Label);
        Assert.Same(window.RunCommand, retry.Command);
        Assert.Empty(GpmcBackupFormat.List(folder.Path));
        Assert.False(window.IsBusy);

        // Retry, with a SYSVOL that answers: the backup is written and the window closes.
        h.Sysvol.Unavailable = null;
        bool? closed = null;
        window.CloseRequested += (_, result) => closed = result;
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);
        Assert.True(closed);
        Assert.Single(GpmcBackupFormat.List(folder.Path));
    }

    // ---- The folder ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Reading the backups in the folder…", "Backups in the folder: 2")]
    [InlineData("pl-PL", "Wczytywanie kopii zapasowych z folderu…", "Kopie zapasowe w folderze: 2")]
    public async Task TheReadOfTheFolder_IsTheOperationOfTheWindow_AndTheLineShowsTheCountAfterwards(string culture, string reading, string found)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        await BackUpAsync(h, folder.Path, h.Sample.WorkstationPasswords, h.Sample.DriveMappings);
        using var window = new GpoBackupViewModel(h.Services, GpoBackupMode.Restore, []) { Folder = folder.Path };
        var seen = new StatusRecorder(window.Status);

        await window.LoadBackupsCommand.ExecuteAsync(null);
        await WaitForDetailsAsync(window);

        Assert.Equal((PageStatusKind.Working, reading), seen.Plain[0]);
        Assert.Contains((PageStatusKind.Idle, found), seen.Plain);
        Assert.Equal((PageStatusKind.Idle, found), (window.Status.Current.Kind, window.Status.Current.Message));
        Assert.Equal(2, window.Backups.Count);
        Assert.Null(window.Status.Lasting);
        Assert.Null(window.Error);
        Assert.False(window.Status.IsWorking);
    }

    [Theory]
    [InlineData("en-US", "No backups in the folder")]
    [InlineData("pl-PL", "Brak kopii zapasowych w folderze")]
    public async Task AFolderWithoutBackups_SaysSo_AsTheSummary_AndTheSummaryFollowsTheLanguage(string culture, string none)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        using var window = new GpoBackupViewModel(h.Services, GpoBackupMode.Restore, []) { Folder = folder.Path };

        await window.LoadBackupsCommand.ExecuteAsync(null);

        Assert.Equal((PageStatusKind.Idle, none), (window.Status.Current.Kind, window.Status.Current.Message));
        Assert.Equal(h.B2.Localizer[L.GroupPolicy.NoBackups], window.StatusText); // the sentence the window always had

        h.B2.Localizer.SetCulture(culture == "en-US" ? Polish : CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        Assert.Equal(h.B2.Localizer[L.GpoBackupStatus.NoneFound], window.Status.Current.Message);
        Assert.NotEqual(none, window.Status.Current.Message);
    }

    [Fact]
    public async Task AWindowThatListsNothing_HasNoSummary_AndAnEmptyFolderBoxReadsNothing()
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        using var folder = new TempFolder();
        using var backup = await NewAsync(h, GpoBackupMode.Backup, folder.Path, h.Sample.WorkstationPasswords);
        using var restore = new GpoBackupViewModel(h.Services, GpoBackupMode.Restore, []);
        var seen = new StatusRecorder(restore.Status);

        await restore.LoadBackupsCommand.ExecuteAsync(null); // no folder chosen yet

        Assert.Same(PageStatus.Empty, backup.Status.Current);
        Assert.Same(PageStatus.Empty, restore.Status.Current);
        Assert.Empty(seen.Seen);
        Assert.Empty(restore.Backups);
    }

    [Fact]
    public async Task Stop_WhileTheFolderIsRead_ReturnsControl_AndTheListStaysAsItWas()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "A FIFO stands in for a folder on a share that hangs (mkfifo).");
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        using var folder = new TempFolder();
        using var window = new GpoBackupViewModel(h.Services, GpoBackupMode.Restore, []) { Folder = folder.Path };

        // manifest.xml is a FIFO: reading it blocks until a writer opens it, like a file on a share that does not answer.
        var manifest = Path.Combine(folder.Path, GpmcBackupFormat.ManifestFile);
        await MakeFifoAsync(manifest);
        var seen = new StatusRecorder(window.Status);
        var reading = Task.Run(() => window.LoadBackupsCommand.ExecuteAsync(null), Ct);
        await UntilAsync(() => window.Status.IsWorking);

        window.Status.CancelCommand.Execute(null);

        // Control comes back although the folder has not answered: the operator stopped it, the list is as it was.
        await reading.WaitAsync(Limit, Ct);
        Assert.False(window.Status.IsWorking);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Working, PageStatusKind.Attention], seen.Plain.Select(s => s.Kind).ToList());
        var state = window.Status.Current;
        Assert.Equal("Reading the backups was stopped: the list was not changed. Press “Retry” to read the folder again.", state.Message);
        Assert.Equal("GpoBackup.Status.ListStopped", state.Log.ToString());
        Assert.True(state.CanClose);
        Assert.Null(window.Status.Lasting);
        Assert.Empty(window.Backups);
        Assert.Equal("Retry", Assert.IsType<PageStatusAction>(state.Action).Label);

        // The folder answers at last (what the abandoned read finds is of no interest): Retry reads it again.
        await ReleaseFifoAsync(manifest);
        File.Delete(manifest);
        await ((IAsyncRelayCommand)state.Action!.Command).ExecuteAsync(null);
        Assert.Equal((PageStatusKind.Idle, "No backups in the folder"), (window.Status.Current.Kind, window.Status.Current.Message));
    }

    [Theory]
    [InlineData("en-US", "Could not list the backups: The folder could not be read or written. Press Retry to try again. (")]
    [InlineData("pl-PL", "Nie udało się wczytać listy kopii zapasowych: Nie można odczytać lub zapisać folderu. Wybierz Ponów, aby spróbować ponownie. (")]
    public async Task AFolderThatCouldNotBeRead_IsTheStateOfTheWindow_WithTheReasonTheCodeAndRetry(string culture, string text)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var window = new GpoBackupViewModel(h.Services, GpoBackupMode.Restore, []) { Folder = "\0" };

        await window.LoadBackupsCommand.ExecuteAsync(null);

        var failed = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith(text, failed.Message, StringComparison.Ordinal);
        Assert.Equal("GpoBackup.Status.ListFailed [ArgumentException]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.Same(failed, window.Status.Lasting);
        Assert.NotNull(window.Error);
        Assert.Empty(window.Backups);
        Assert.Same(window.LoadBackupsCommand, Assert.IsType<PageStatusAction>(failed.Action).Command);

        // Another folder: the state is over with the next read.
        using var folder = new TempFolder();
        window.Folder = folder.Path;
        await window.LoadBackupsCommand.ExecuteAsync(null);
        Assert.Null(window.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
    }

    // ---- The selected backup ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Reading the backup of “Stacje robocze – Zasady haseł”…")]
    [InlineData("pl-PL", "Wczytywanie kopii zapasowej „Stacje robocze – Zasady haseł”…")]
    public async Task TheReadOfTheSelectedBackup_HasAHolderOfItsOwn_InFrontOfTheWindow_AndSaysNothingWhenItEndedWell(string culture, string reading)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        await BackUpAsync(h, folder.Path, h.Sample.WorkstationPasswords);
        using var window = new GpoBackupViewModel(h.Services, GpoBackupMode.Restore, []) { Folder = folder.Path };
        Assert.NotSame(window.Status, window.DetailsStatus);
        Assert.Same(window.DetailsStatus, window.Status.Front);
        var details = new StatusRecorder(window.DetailsStatus);

        await window.InitializeAsync();
        await WaitForDetailsAsync(window);

        // Working, said by the holder of the selection - and said again by the window's line, whose page it is in front.
        Assert.Equal([(PageStatusKind.Working, reading), (PageStatusKind.Idle, string.Empty)], details.Plain);
        Assert.Null(window.DetailsStatus.Lasting);
        Assert.NotNull(window.BackupDetails);
        Assert.True(window.RunCommand.CanExecute(null));
        Assert.Null(window.OtherDomainText);
        Assert.False(window.HasOtherDomain);
    }

    [Theory]
    [InlineData("en-US", "Reading the backup was stopped: it cannot be used yet. Press “Retry” to read it again.", "Retry")]
    [InlineData("pl-PL", "Wczytywanie kopii zapasowej zatrzymano: nie można jeszcze z niej skorzystać. Wybierz „Ponów”, aby wczytać ją ponownie.", "Ponów")]
    public async Task Stop_WhileTheBackupIsRead_ReturnsControl_AndTheLineSaysItCannotBeUsedYet_UntilRetryReadsItAgain(string culture, string stopped, string retryLabel)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "A FIFO stands in for a file on a share that hangs (mkfifo).");
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        await BackUpAsync(h, folder.Path, h.Sample.WorkstationPasswords);
        var entry = Assert.Single(GpmcBackupFormat.List(folder.Path));
        var xml = Path.Combine(folder.Path, SysvolPaths.FolderName(entry.BackupId), GpmcBackupFormat.BackupFile);
        var original = await File.ReadAllBytesAsync(xml, Ct);
        File.Delete(xml);
        await MakeFifoAsync(xml);
        using var window = new GpoBackupViewModel(h.Services, GpoBackupMode.Restore, []) { Folder = folder.Path };
        var line = new StatusRecorder(window.Status);

        // The window opens (its first backup selected) while the read of the backup waits for the FIFO.
        var opening = Task.Run(window.InitializeAsync, Ct);
        await UntilAsync(() => window.DetailsStatus.IsWorking);
        window.DetailsStatus.CancelCommand.Execute(null);
        await opening.WaitAsync(Limit, Ct);
        await UntilAsync(() => !window.DetailsStatus.IsWorking);

        // Stopped: the backup cannot be used, the Run button waits, and the line of the window says so with Retry.
        var state = window.DetailsStatus.Current;
        Assert.Equal((PageStatusKind.Attention, stopped), (state.Kind, state.Message));
        Assert.Equal("GpoBackup.Status.BackupReadStopped", state.Log.ToString());
        Assert.False(state.CanClose);
        Assert.Same(state, window.DetailsStatus.Lasting);
        Assert.Equal((PageStatusKind.Attention, stopped), (window.Status.Current.Kind, window.Status.Current.Message));
        Assert.False(window.RunCommand.CanExecute(null));
        var retry = Assert.IsType<PageStatusAction>(state.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Contains(line.Plain, s => s.Kind == PageStatusKind.Attention);

        // The file answers: Retry reads the backup, and the state is over.
        await ReleaseFifoAsync(xml);
        File.Delete(xml);
        await File.WriteAllBytesAsync(xml, original, Ct);
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);
        Assert.Null(window.DetailsStatus.Lasting);
        Assert.True(window.RunCommand.CanExecute(null));
        Assert.NotNull(window.BackupDetails);
    }

    [Theory]
    [InlineData("en-US", "Could not read the backup of “Stacje robocze – Zasady haseł”: ", " Press Retry to try again. (GpoBackupException)")]
    [InlineData("pl-PL", "Nie udało się wczytać kopii zapasowej „Stacje robocze – Zasady haseł”: ", " Wybierz Ponów, aby spróbować ponownie. (GpoBackupException)")]
    public async Task ABackupThatCouldNotBeRead_IsTheStateOfThatSelection_WithTheReasonTheCodeAndRetry(string culture, string lead, string tail)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        await BackUpAsync(h, folder.Path, h.Sample.WorkstationPasswords);
        var entry = Assert.Single(GpmcBackupFormat.List(folder.Path));
        var xml = Path.Combine(folder.Path, SysvolPaths.FolderName(entry.BackupId), GpmcBackupFormat.BackupFile);
        var original = await File.ReadAllBytesAsync(xml, Ct);
        await File.WriteAllTextAsync(xml, "to nie jest XML – Łódź", Ct);
        using var window = new GpoBackupViewModel(h.Services, GpoBackupMode.Restore, []) { Folder = folder.Path };

        await window.InitializeAsync();
        await UntilAsync(() => window.DetailsStatus.Lasting is not null);

        // The reason is the sentence the window always had, the code the kind of the failure, and Retry reads it again.
        Assert.NotNull(window.Error);
        var failed = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.Contains(window.Error, failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("XML – Łódź", failed.Message, StringComparison.Ordinal);
        Assert.Equal("GpoBackup.Status.BackupReadFailed [GpoBackupException]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.Same(failed, window.DetailsStatus.Lasting);
        Assert.False(window.RunCommand.CanExecute(null));

        await File.WriteAllBytesAsync(xml, original, Ct);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);

        Assert.Null(window.DetailsStatus.Lasting);
        Assert.Null(window.Error);
        Assert.True(window.RunCommand.CanExecute(null));
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
    }

    [Theory]
    [InlineData("en-US", "The backup comes from the domain fabrikam.test: restore it there, or import its settings into a GPO of this domain.")]
    [InlineData("pl-PL", "Kopia pochodzi z domeny fabrikam.test: przywróć ją tam albo zaimportuj jej ustawienia do GPO tej domeny.")]
    public async Task ABackupOfAnotherDomain_IsANoteUnderTheList_ForAsLongAsItIsSelected_AndSaysNothingToTheLine(string culture, string note)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        await BackUpAsync(h, folder.Path, h.Sample.WorkstationPasswords);
        var manifest = Path.Combine(folder.Path, GpmcBackupFormat.ManifestFile);
        await File.WriteAllTextAsync(manifest, (await File.ReadAllTextAsync(manifest, Ct)).Replace("contoso.test", "fabrikam.test", StringComparison.OrdinalIgnoreCase), Ct);
        using var window = new GpoBackupViewModel(h.Services, GpoBackupMode.Restore, []) { Folder = folder.Path };

        await window.InitializeAsync();
        await WaitForDetailsAsync(window);

        Assert.True(Assert.Single(window.Backups).IsOtherDomain);
        Assert.Equal(note, window.OtherDomainText);
        Assert.True(window.HasOtherDomain);
        Assert.Null(window.Error);
        Assert.Null(window.DetailsStatus.Lasting);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);

        // Gone with the selection, and never there for an import (it is possible there).
        window.SelectedBackup = null;
        Assert.Null(window.OtherDomainText);
        using var import = new GpoBackupViewModel(h.Services, GpoBackupMode.Import, [(await h.Services.Reader.ReadPolicyAsync(h.Session, h.Sample.SitePrinters, Ct))!])
        {
            Folder = folder.Path,
        };
        await import.InitializeAsync();
        await WaitForDetailsAsync(import);
        Assert.Null(import.OtherDomainText);
    }

    // ---- The plan of a restore and of an import -----------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Restore “Stacje robocze – Zasady haseł” from a backup: preparing the changes…", "Stopping…",
        "Restore “Stacje robocze – Zasady haseł” from a backup – nothing was changed: Preparing the changes was stopped. Press Retry to try again.")]
    [InlineData("pl-PL", "Przywrócenie „Stacje robocze – Zasady haseł” z kopii zapasowej: przygotowywanie zmian…", "Zatrzymywanie…",
        "Przywrócenie „Stacje robocze – Zasady haseł” z kopii zapasowej – nic nie zmieniono: Przygotowywanie zmian zatrzymano. Wybierz Ponów, aby spróbować ponownie.")]
    public async Task ThePlanOfARestore_IsTheOperationOfTheWindow_StopHolds_AndRetryMakesThePlanAgainAndPreviewsIt(
        string culture, string preparing, string stopping, string stoppedText)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        await BackUpAsync(h, folder.Path, h.Sample.WorkstationPasswords);
        using var window = new GpoBackupViewModel(h.Services, GpoBackupMode.Restore, []) { Folder = folder.Path };
        await window.InitializeAsync();
        await WaitForDetailsAsync(window);
        Assert.True(window.RunCommand.CanExecute(null));
        var seen = new StatusRecorder(window.Status);

        // The plan reads the directory: the first call waits for the test, so "preparing" lasts.
        using var held = new HeldDirectoryCall(h.Session);
        var running = Task.Run(() => window.RunCommand.ExecuteAsync(null), Ct);
        await held.Reached.WaitAsync(Limit, Ct);
        var working = window.Status.Current;
        Assert.Equal((PageStatusKind.Working, preparing), (working.Kind, working.Message));
        Assert.Equal("GroupPolicy.Status.Preparing", working.Log.ToString());
        Assert.Same(window.Status.CancelCommand, Assert.IsType<PageStatusAction>(working.Action).Command);
        Assert.True(window.IsBusy);

        window.Status.CancelCommand.Execute(null);
        Assert.Equal(stopping, window.Status.Current.Message);
        held.Release();
        await running.WaitAsync(Limit, Ct);

        // Stopped: nothing was changed - no preview opened - and Retry makes the plan again.
        Assert.False(window.IsBusy);
        Assert.Empty(h.Windows.Previews);
        var state = window.Status.Current;
        Assert.Equal((PageStatusKind.Attention, stoppedText), (state.Kind, state.Message));
        Assert.Equal("GroupPolicy.Status.PreparingStopped", state.Log.ToString());
        Assert.Contains((PageStatusKind.Working, stopping), seen.Plain);
        var retry = Assert.IsType<PageStatusAction>(state.Action);
        Assert.Same(window.RunCommand, retry.Command);

        // The line is the window's again while the preview is open, and the window closes after the restore.
        bool? closed = null;
        window.CloseRequested += (_, result) => closed = result;
        h.Windows.BeforePreview = _ =>
        {
            Assert.False(window.Status.IsWorking);
            Assert.False(window.IsBusy);
            return Task.CompletedTask;
        };
        h.Windows.AfterPreview = _ => Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);

        Assert.Single(h.Windows.Previews);
        Assert.True(window.ChangedDirectory);
        Assert.True(closed);
    }

    [Theory]
    [InlineData("en-US", "Import settings into “Lokacja Warszawa – Drukarki” – nothing was changed: ", " Press Retry to try again. (ServerUnavailable)")]
    [InlineData("pl-PL", "Import ustawień do „Lokacja Warszawa – Drukarki” – nic nie zmieniono: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)")]
    public async Task APlanThatCouldNotBeMade_IsAnErrorWithRetry_NothingIsPreviewed_AndSysvolThatIsGoneIsAnAttention(string culture, string lead, string tail)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        await BackUpAsync(h, folder.Path, h.Sample.WorkstationPasswords);
        var target = (await h.Services.Reader.ReadPolicyAsync(h.Session, h.Sample.SitePrinters, Ct))!;
        using var window = new GpoBackupViewModel(h.Services, GpoBackupMode.Import, [target]) { Folder = folder.Path };
        await window.InitializeAsync();
        await WaitForDetailsAsync(window);
        h.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "serwer dc01.contoso.test nie odpowiada: DSID-0315");

        await window.RunCommand.ExecuteAsync(null);
        h.Session.BeforeOperation = null;

        var failed = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DSID", failed.Message, StringComparison.Ordinal);
        Assert.Equal("GroupPolicy.Status.NotChanged [ServerUnavailable]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Empty(h.Windows.Previews);
        Assert.False(window.IsBusy);
        Assert.NotNull(window.Error);

        // SYSVOL cannot be opened: an attention - the plan needs it - with the same Retry.
        h.Sysvol.Unavailable = SysvolUnavailableReason.AccessDenied;
        await ((IAsyncRelayCommand)failed.Action!.Command).ExecuteAsync(null);
        h.Sysvol.Unavailable = null;
        var gone = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, gone.Kind);
        Assert.StartsWith(lead, gone.Message, StringComparison.Ordinal);
        Assert.Equal("GroupPolicy.SysvolFailedShort", gone.Log.ToString());
        Assert.Empty(h.Windows.Previews);

        // Retry with everything answering: the plan is previewed and executed, and the window closes.
        bool? closed = null;
        window.CloseRequested += (_, result) => closed = result;
        await ((IAsyncRelayCommand)gone.Action!.Command).ExecuteAsync(null);
        Assert.Single(h.Windows.Previews);
        Assert.True(closed);
    }

    // ---- The state that lasts, the log ----------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US")]
    [InlineData("pl-PL")]
    public async Task WithoutASession_TheLineSaysSoFromTheStart_AndNothingIsBackedUp(string culture)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var gpo = (await h.Services.Reader.ReadPolicyAsync(h.Session, h.Sample.WorkstationPasswords, Ct))!;
        await h.B2.Sessions.DisconnectAsync();
        var before = h.Session.Operations.Count;
        using var folder = new TempFolder();
        using var window = new GpoBackupViewModel(h.Services, GpoBackupMode.Backup, [gpo]) { Folder = folder.Path };

        // Said before anything is pressed: an error that cannot be closed.
        var state = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.Equal(h.B2.Localizer[L.Errors.NotConnected], state.Message);
        Assert.Equal("Errors.NotConnected", state.Log.ToString());
        Assert.False(state.CanClose);
        var seen = new StatusRecorder(window.Status);

        await window.RunCommand.ExecuteAsync(null);

        Assert.Empty(seen.Seen);
        Assert.False(window.IsBusy);
        Assert.Equal(before, h.Session.Operations.Count);
        Assert.Empty(GpmcBackupFormat.List(folder.Path));
    }

    [Fact]
    public async Task TheEndOfTheSession_TakesTheLine_AlsoDuringTheBackup_AndIsSaidInTheLanguageOfTheMoment()
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        using var folder = new TempFolder();
        using var window = await NewAsync(h, GpoBackupMode.Backup, folder.Path, h.Sample.WorkstationPasswords, h.Sample.DriveMappings);
        bool? closed = null;
        window.CloseRequested += (_, result) => closed = result;
        using var held = new HeldDirectoryCall(h.Session, operation => IsOf(operation, h.Sample.DriveMappings));
        var running = Task.Run(() => window.RunCommand.ExecuteAsync(null), Ct);
        await held.Reached.WaitAsync(Limit, Ct);
        var seen = new StatusRecorder(window.Status);

        await h.B2.Sessions.DisconnectAsync();

        // The line goes straight from "working" to the state; the backup ends without a word of its own.
        Assert.True(window.IsSessionEnded);
        Assert.False(window.Status.IsWorking);
        Assert.Equal((PageStatusKind.Error, h.B2.Localizer[L.WindowSession.Ended]), (window.Status.Current.Kind, window.Status.Current.Message));
        Assert.False(window.Status.Current.CanClose);
        held.Release();
        await running.WaitAsync(Limit, Ct);
        Assert.Null(closed);
        Assert.False(window.IsBusy);
        Assert.Equal([(PageStatusKind.Error, h.B2.Localizer[L.WindowSession.Ended])], seen.Plain);
        Assert.Equal("WindowSession.Ended", window.Status.Current.Log.ToString());
        Assert.False(window.RunCommand.CanExecute(null));

        h.B2.Localizer.SetCulture(Polish);
        Assert.Equal(h.B2.Localizer[L.WindowSession.Ended], window.Status.Current.Message);
    }

    [Fact]
    public async Task TheLogCopies_CarryKeysCountsAndCodes_NeverAGpoNameAFolderADomainOrAServerText()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"), logger);
        using var folder = new TempFolder();
        using var backup = await NewAsync(h, GpoBackupMode.Backup, folder.Path, h.Sample.WorkstationPasswords, h.Sample.DriveMappings);
        h.Session.BeforeOperation = operation =>
        {
            if (IsOf(operation, h.Sample.DriveMappings))
            {
                throw new DirectoryException(DirectoryErrorKind.AccessDenied, "00002098: SecErr: DSID-03150F94 for CN=Policies");
            }
        };
        await backup.RunCommand.ExecuteAsync(null);
        h.Session.BeforeOperation = null;
        using var restore = new GpoBackupViewModel(h.Services, GpoBackupMode.Restore, []) { Folder = folder.Path };
        await restore.InitializeAsync();
        await WaitForDetailsAsync(restore);
        await restore.RunCommand.ExecuteAsync(null);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("GpoBackup.Status.BackingUp (0, 2)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GpoBackup.Status.BackupFailed (1, 2) [AccessDenied]", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GpoBackup.Status.ReadingFolder", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GpoBackup.Status.ReadingBackup", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.Preparing", StringComparison.Ordinal));
        Assert.DoesNotContain("Stacje", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Zasady haseł", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(folder.Path, logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DSID", logger.AllText, StringComparison.Ordinal);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>The window of a backup of <paramref name="gpos"/> into <paramref name="folder"/>.</summary>
    private static async Task<GpoBackupViewModel> NewAsync(GroupPolicyHarness h, GpoBackupMode mode, string folder, params Guid[] gpos)
    {
        var infos = new List<GpoInfo>();
        foreach (var id in gpos)
        {
            infos.Add((await h.Services.Reader.ReadPolicyAsync(h.Session, id, Ct))!);
        }

        return new GpoBackupViewModel(h.Services, mode, infos) { Folder = folder };
    }

    /// <summary>Writes the backups of the GPOs into the folder with a window of its own (the way the operator would).</summary>
    private static async Task BackUpAsync(GroupPolicyHarness h, string folder, params Guid[] gpos)
    {
        using var window = await NewAsync(h, GpoBackupMode.Backup, folder, gpos);
        await window.RunCommand.ExecuteAsync(null);
        Assert.Equal(gpos.Length, GpmcBackupFormat.List(folder).Count);
        h.B2.Notifications.Clear();
    }

    /// <summary>The call is the one of the GPO (its container or its objects).</summary>
    private static bool IsOf(FakeOperation operation, Guid gpo) =>
        operation.DistinguishedName.Contains(gpo.ToString("B"), StringComparison.OrdinalIgnoreCase);

    /// <summary>The selected backup was read: the Run button can be pressed, or the read failed.</summary>
    private static Task WaitForDetailsAsync(GpoBackupViewModel window) =>
        UntilAsync(() => !window.DetailsStatus.IsWorking && !window.Status.IsWorking && (window.SelectedBackup is null || window.BackupDetails is not null || window.DetailsStatus.Lasting is not null));

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not come true in time.");
            await Task.Delay(10, Ct);
        }
    }

    private static async Task MakeFifoAsync(string path)
    {
        using var mkfifo = Process.Start("mkfifo", [path]);
        await mkfifo.WaitForExitAsync(Ct);
        Assert.Equal(0, mkfifo.ExitCode);
    }

    /// <summary>Opened read-write the FIFO does not block: a read that waits for a writer gets its (invalid) bytes and ends.</summary>
    private static async Task ReleaseFifoAsync(string path)
    {
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1);
        await writer.WriteAsync("nie XML – Łódź"u8.ToArray(), Ct);
        await writer.FlushAsync(Ct);
        await Task.Delay(200, Ct);
    }
}
