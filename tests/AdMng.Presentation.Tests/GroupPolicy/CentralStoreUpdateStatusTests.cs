using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.Localization;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// The status line of the window that updates the ADMX templates of the central store (track V, stage V2b-3;
/// docs/dev/v-interface-renewal.md "V2b-3 as built"): loading a package is its operation, with Stop - the comparison with
/// the store is its last stage -, the summary says what the comparison found, a package that could not be loaded is a result
/// that can be closed, a comparison that was stopped or failed is the state of the window until Retry, and the update - the
/// backup, then the plan up to the preview - is an operation with Stop that says at the end what was written. How a package
/// is read, compared and written is covered, unchanged, by <see cref="CentralStoreUpdateTests"/>.
/// </summary>
public sealed class CentralStoreUpdateStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Loading a package ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Reading the templates…", "Comparing with the central store…", @"^To write: \d+, unchanged: \d+, skipped: \d+$")]
    [InlineData("pl-PL", "Wczytywanie szablonów…", "Porównywanie z magazynem centralnym…", @"^Do zapisania: \d+, bez zmian: \d+, pominięte: \d+$")]
    public async Task LoadingAFile_IsTheOperationOfTheWindow_TheComparisonIsItsLastStage_AndTheLineShowsWhatItFound(
        string culture, string reading, string comparing, string summary)
    {
        var h = await StoreAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path);
        using var update = New(h);
        Assert.IsAssignableFrom<IHasPageStatus>(update);
        Assert.Same(PageStatus.Empty, update.Status.Current);
        var seen = new StatusRecorder(update.Status);

        // The store is listed twice - which languages it has, then for the comparison; the test holds both calls.
        var first = h.Sysvol.HoldNext("ListDefinitions");
        var loading = update.OpenFileCommand.ExecuteAsync(null);
        await first.Reached.WaitAsync(Limit, Ct);
        var second = h.Sysvol.HoldNext("ListDefinitions");

        var working = update.Status.Current;
        Assert.Equal((PageStatusKind.Working, reading), (working.Kind, working.Message));
        Assert.Equal("CentralStore.Loading", working.Log.ToString());
        Assert.Same(update.Status.CancelCommand, Assert.IsType<PageStatusAction>(working.Action).Command);
        Assert.True(update.IsBusy);
        Assert.False(update.OpenFileCommand.CanExecute(null));

        first.Release();
        await second.Reached.WaitAsync(Limit, Ct);

        // The comparison is a stage of the same message: it is not said (or logged) as a new one.
        var stage = update.Status.Current;
        Assert.Equal((PageStatusKind.Working, comparing), (stage.Kind, stage.Message));
        Assert.Equal("CentralStore.Loading", stage.Log.ToString());
        Assert.True(update.IsComparing);

        second.Release();
        await loading.WaitAsync(Limit, Ct);

        // What the comparison found is the summary; nothing else was said.
        Assert.Equal([(PageStatusKind.Working, reading), (PageStatusKind.Working, comparing), (PageStatusKind.Idle, update.Status.Idle.Message)], seen.Plain);
        Assert.Matches(summary, update.Status.Current.Message);
        Assert.Null(update.Status.Lasting);
        Assert.False(update.IsBusy);
        Assert.False(update.Status.IsWorking);
    }

    [Theory]
    [InlineData("en-US", "Downloading the templates of Microsoft Edge…", "Stopping…", "Loading the templates was stopped: the package shown was not changed.", "Retry")]
    [InlineData("pl-PL", "Pobieranie szablonów Microsoft Edge…", "Zatrzymywanie…", "Wczytywanie szablonów zatrzymano: pokazanej paczki nie zmieniono.", "Ponów")]
    public async Task Stop_WhileAPackageIsDownloaded_ReturnsControl_AndTheLineOffersTheDownloadAgain(
        string culture, string downloading, string stopping, string stoppedText, string retryLabel)
    {
        var h = await StoreAsync(CultureInfo.GetCultureInfo(culture));
        var handler = new ScriptedHandler(Zip()) { Hold = true };
        using var update = New(h, new PolicyTemplatePackageLoader(handler: () => handler));
        var seen = new StatusRecorder(update.Status);

        var loading = update.DownloadEdgeCommand.ExecuteAsync(null);
        await handler.Reached.WaitAsync(Limit, Ct);
        Assert.Equal((PageStatusKind.Working, downloading), (update.Status.Current.Kind, update.Status.Current.Message));
        Assert.Equal("CentralStore.Downloading", update.Status.Current.Log.ToString());

        // Stop answers at once; the download that is on its way ends with it.
        update.Status.CancelCommand.Execute(null);
        Assert.Equal(stopping, update.Status.Current.Message);
        await loading.WaitAsync(Limit, Ct);

        // A result that can be closed - the package shown before (none) stays - and the command that downloads again.
        Assert.False(update.IsBusy);
        Assert.Null(update.Package);
        Assert.Equal([(PageStatusKind.Working, downloading), (PageStatusKind.Working, stopping), (PageStatusKind.Attention, stoppedText)], seen.Plain);
        var state = update.Status.Current;
        Assert.Equal("CentralStore.Status.LoadStopped", state.Log.ToString());
        Assert.True(state.CanClose);
        Assert.Null(update.Status.Lasting);
        var retry = Assert.IsType<PageStatusAction>(state.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.Same(update.DownloadEdgeCommand, retry.Command);

        // Retry, with a vendor that answers: the package is there and the line says what the comparison found.
        handler.Hold = false;
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);

        Assert.NotNull(update.Package);
        Assert.Equal(PageStatusKind.Idle, update.Status.Current.Kind);
        Assert.Equal(update.Status.Idle.Message, update.Status.Current.Message);
    }

    [Theory]
    [InlineData("en-US", "Could not download the templates of Microsoft Edge: ", " Press Retry to try again.", "Retry")]
    [InlineData("pl-PL", "Nie udało się pobrać szablonów Microsoft Edge: ", " Wybierz Ponów, aby spróbować ponownie.", "Ponów")]
    public async Task ADownloadThatFailed_IsAResultThatCanBeClosed_WithTheReasonAndRetry_NothingOfTheAddressIsInIt(
        string culture, string lead, string tail, string retryLabel)
    {
        var h = await StoreAsync(CultureInfo.GetCultureInfo(culture));
        var handler = new ScriptedHandler(Zip()) { Status = HttpStatusCode.ServiceUnavailable };
        using var update = New(h, new PolicyTemplatePackageLoader(handler: () => handler));
        var seen = new StatusRecorder(update.Status);

        await update.DownloadEdgeCommand.ExecuteAsync(null);

        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], seen.Plain.Select(s => s.Kind).ToList());
        var failed = update.Status.Current;
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.Contains(h.B2.Localizer[L.CentralStore.ErrorDownload], failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("microsoft.com", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("CentralStore.Status.DownloadFailed", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(update.Status.Lasting);
        Assert.Equal(retryLabel, Assert.IsType<PageStatusAction>(failed.Action).Label);
        Assert.False(update.IsBusy);

        // Closed, the line is empty - there is no comparison to say anything about.
        update.Status.CloseCommand.Execute(null);
        Assert.Same(PageStatus.Empty, update.Status.Current);
    }

    [Theory]
    [InlineData("en-US", "Could not read the templates: The file or folder does not exist. Choose another package.")]
    [InlineData("pl-PL", "Nie udało się wczytać szablonów: Plik lub folder nie istnieje. Wybierz inną paczkę.")]
    public async Task APackageThatCannotBeRead_IsAResultThatCanBeClosed_AndTheOperatorChoosesAnotherOne(string culture, string text)
    {
        var h = await StoreAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = Path.Combine(folder.Path, "nie ma takiego pliku.zip");
        using var update = New(h);

        await update.OpenFileCommand.ExecuteAsync(null);

        var failed = update.Status.Current;
        Assert.Equal((PageStatusKind.Error, text), (failed.Kind, failed.Message));
        Assert.Equal("CentralStore.Status.LoadFailed", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(failed.Action); // a file the operator picks: there is nothing to repeat
        Assert.Null(update.Package);

        // Another package: the error is gone, the comparison is shown.
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path);
        await update.OpenFileCommand.ExecuteAsync(null);
        Assert.Equal((PageStatusKind.Idle, update.Status.Idle.Message), (update.Status.Current.Kind, update.Status.Current.Message));
    }

    // ---- The comparison -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Could not compare the templates with the central store: ", " Press Retry to try again.", "Retry")]
    [InlineData("pl-PL", "Nie udało się porównać szablonów z magazynem centralnym: ", " Wybierz Ponów, aby spróbować ponownie.", "Ponów")]
    public async Task AComparisonThatFailed_IsTheStateOfTheWindow_WithTheReasonAndRetry_AndTheNextOneEndsIt(string culture, string lead, string tail, string retryLabel)
    {
        var h = await StoreAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path);
        using var update = New(h);
        h.Sysvol.Unavailable = SysvolUnavailableReason.Unreachable;
        var seen = new StatusRecorder(update.Status);

        await update.OpenFileCommand.ExecuteAsync(null);

        // The package is loaded, there are no files to show: the state of the window, with Retry, until it is compared again.
        Assert.NotNull(update.Package);
        Assert.Empty(update.Rows);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Working, PageStatusKind.Error], seen.Plain.Select(s => s.Kind).ToList()); // reading, comparing
        var failed = update.Status.Current;
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.Contains(h.Services.SysvolText(SysvolUnavailableReason.Unreachable, h.Session.Info.Server), failed.Message, StringComparison.Ordinal);
        Assert.Equal("CentralStore.Status.CompareFailed", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.Same(failed, update.Status.Lasting);
        Assert.False(update.IsBusy);
        Assert.False(update.IsComparing);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);

        // Retry, with a SYSVOL that answers: the files and the summary are there, the state is over.
        h.Sysvol.Unavailable = null;
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);

        Assert.NotEmpty(update.Rows);
        Assert.Null(update.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, update.Status.Idle.Message), (update.Status.Current.Kind, update.Status.Current.Message));
    }

    [Theory]
    [InlineData("en-US", "Stopping…", "Comparing with the central store was stopped: the files are not shown. Press “Retry” to compare again.")]
    [InlineData("pl-PL", "Zatrzymywanie…", "Porównywanie z magazynem centralnym zatrzymano: pliki nie są pokazane. Wybierz „Ponów”, aby porównać ponownie.")]
    public async Task Stop_WhileTheStoreIsCompared_IsTheStateOfTheWindow_UntilRetryComparesAgain(string culture, string stopping, string stoppedText)
    {
        var h = await StoreAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path);
        using var update = New(h);
        await update.OpenFileCommand.ExecuteAsync(null);
        var summary = update.Status.Current.Message;
        var seen = new StatusRecorder(update.Status);

        // A language is switched: the comparison is an operation of its own, and it waits for the test.
        var hold = h.Sysvol.HoldNext("ListDefinitions");
        var choice = update.Cultures.Single(c => c.Culture == "pl");
        choice.IsSelected = !choice.IsSelected;
        await hold.Reached.WaitAsync(Limit, Ct);
        Assert.Equal(PageStatusKind.Working, update.Status.Current.Kind);
        Assert.Empty(update.Rows); // what the previous options showed is gone at once
        update.Status.CancelCommand.Execute(null);
        Assert.Equal(stopping, update.Status.Current.Message);
        hold.Release();
        await UntilAsync(() => !update.IsComparing);

        Assert.Empty(update.Rows);
        var state = update.Status.Current;
        Assert.Equal((PageStatusKind.Attention, stoppedText), (state.Kind, state.Message));
        Assert.Equal("CentralStore.Status.CompareStopped", state.Log.ToString());
        Assert.False(state.CanClose);
        Assert.Same(state, update.Status.Lasting);
        Assert.Contains((PageStatusKind.Working, stopping), seen.Plain);
        Assert.False(update.UpdateCommand.CanExecute(null));

        await ((IAsyncRelayCommand)state.Action!.Command).ExecuteAsync(null);

        Assert.NotEmpty(update.Rows);
        Assert.Null(update.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, update.Status.Current.Kind);
        Assert.NotEqual(string.Empty, summary);
    }

    /// <summary>
    /// A load takes the line from a comparison that runs on its own (an option was changed), and the files of that comparison
    /// were cleared when it began. When the load brings no other package - it failed, or the operator stopped it -, the
    /// package shown has no files: under the load's message the line says so, with Retry, also when the comparison that was
    /// taken off the line ends only afterwards (audit GP-4: the old package stayed with no files, no summary and nothing that
    /// compared again). A load that brings a package compares it, and nothing of the interrupted comparison is said - that
    /// case is a guard of the fix: it held before it too.
    /// </summary>
    [Theory]
    [InlineData("failed")]
    [InlineData("stopped")]
    [InlineData("loaded")]
    public async Task ALoadThatTakesTheLineFromAComparison_LeavesTheComparisonToRepeat_UnlessItBringsAPackage(string how)
    {
        var h = await StoreAsync(English);
        using var folder = new TempFolder();
        var archive = await ArchiveAsync(folder.Path);
        h.B2.Files.NextOpenPath = archive;
        var handler = new ScriptedHandler(Zip()) { Hold = true };
        using var update = New(h, new PolicyTemplatePackageLoader(handler: () => handler));
        await update.OpenFileCommand.ExecuteAsync(null);
        var shown = update.Package;
        Assert.NotEmpty(update.Rows);

        // The comparison of its own waits for a share that does not answer (and does not hear its Stop meanwhile).
        var hold = h.Sysvol.HoldNext("ListDefinitions");
        var comparing = update.CompareAsync();
        await hold.Reached.WaitAsync(Limit, Ct);
        Assert.True(update.IsComparing);
        Assert.Empty(update.Rows);

        // Another package is loaded meanwhile.
        switch (how)
        {
            case "failed":
                h.B2.Files.NextOpenPath = Path.Combine(folder.Path, "nie ma takiego pliku.zip");
                await update.OpenFileCommand.ExecuteAsync(null);
                break;
            case "stopped":
                var loading = update.DownloadEdgeCommand.ExecuteAsync(null);
                await handler.Reached.WaitAsync(Limit, Ct);
                update.Status.CancelCommand.Execute(null);
                await loading.WaitAsync(Limit, Ct);
                break;
            default:
                h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path, newer: true);
                await update.OpenFileCommand.ExecuteAsync(null);
                break;
        }

        var said = update.Status.Current;

        // The comparison that was taken off the line ends late: it shows nothing, and the load's message stays.
        hold.Release();
        await comparing.WaitAsync(Limit, Ct);
        Assert.Same(said, update.Status.Current);
        Assert.False(update.IsComparing);
        if (how == "loaded")
        {
            Assert.NotSame(shown, update.Package);
            Assert.NotEmpty(update.Rows);
            Assert.Null(update.Status.Lasting);
            Assert.Equal((PageStatusKind.Idle, update.Status.Idle.Message), (said.Kind, said.Message));
            return;
        }

        Assert.Equal(
            how == "failed" ? (PageStatusKind.Error, "CentralStore.Status.LoadFailed") : (PageStatusKind.Attention, "CentralStore.Status.LoadStopped"),
            (said.Kind, said.Log.ToString()));
        Assert.Same(shown, update.Package);
        Assert.Empty(update.Rows);
        Assert.False(update.UpdateCommand.CanExecute(null));

        // Closed, the line says why the package shown has no files, and Retry compares it again.
        update.Status.CloseCommand.Execute(null);
        var state = update.Status.Current;
        Assert.Equal(
            (PageStatusKind.Attention, "Comparing with the central store was interrupted by loading another package: the files are not shown. Press “Retry” to compare again."),
            (state.Kind, state.Message));
        Assert.Equal("CentralStore.Status.CompareInterrupted", state.Log.ToString());
        Assert.False(state.CanClose);
        Assert.Same(state, update.Status.Lasting);
        var retry = Assert.IsType<PageStatusAction>(state.Action);
        Assert.Equal("Retry", retry.Label);
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);
        Assert.NotEmpty(update.Rows);
        Assert.Null(update.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, update.Status.Idle.Message), (update.Status.Current.Kind, update.Status.Current.Message));
    }

    [Theory]
    [InlineData("en-US", "Comparing with the central store was interrupted by loading another package: the files are not shown. Press “Retry” to compare again.")]
    [InlineData("pl-PL", "Porównywanie z magazynem centralnym przerwano, aby wczytać inną paczkę: pliki nie są pokazane. Wybierz „Ponów”, aby porównać ponownie.")]
    public async Task AComparisonInterruptedByALoad_IsSaidInBothLanguages(string culture, string text)
    {
        var h = await StoreAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path);
        using var update = New(h);
        await update.OpenFileCommand.ExecuteAsync(null);
        var hold = h.Sysvol.HoldNext("ListDefinitions");
        var comparing = update.CompareAsync();
        await hold.Reached.WaitAsync(Limit, Ct);

        h.B2.Files.NextOpenPath = Path.Combine(folder.Path, "nie ma takiego pliku.zip");
        await update.OpenFileCommand.ExecuteAsync(null);
        hold.Release();
        await comparing.WaitAsync(Limit, Ct);
        update.Status.CloseCommand.Execute(null);

        Assert.Equal((PageStatusKind.Attention, text), (update.Status.Current.Kind, update.Status.Current.Message));
    }

    // ---- The update ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The comparison after an update is the update's last stage: the sources and the options wait for it as they do during
    /// the comparison of a load - a language switched or a package loaded meanwhile took it off the line, and the line never
    /// said what the update wrote -, and a Stop of it, or a failure (the store cannot be listed), still says what was written;
    /// that the files are not shown is the state under it (the auditor's note to GP-4).
    /// </summary>
    [Theory]
    [InlineData("stopped")]
    [InlineData("failed")]
    public async Task TheComparisonAfterAnUpdate_KeepsTheSourcesAndOptionsWaiting_AndAStopOrAFailureOfItStillSaysWhatWasWritten(string how)
    {
        var h = await StoreAsync(English);
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path, newer: true);
        using var update = New(h, backup: (path, _, _, _, _) => Task.FromResult(Path.Combine(path, "PolicyDefinitions-kopia")));
        update.BackupFolder = folder.Path;
        await update.OpenFileCommand.ExecuteAsync(null);
        FakeSysvolHold? hold = null;
        h.Windows.AfterPreview = _ =>
        {
            hold = h.Sysvol.HoldNext("ListDefinitions");
            if (how == "failed")
            {
                h.Sysvol.Fail("ListDefinitions", SysvolWriteError.Failed);
            }
        };

        var updating = update.UpdateCommand.ExecuteAsync(null);
        await UntilAsync(() => hold is not null);
        await hold!.Reached.WaitAsync(Limit, Ct);

        // The store was written; its new comparison runs - the window may be closed, but nothing can replace the comparison.
        Assert.True(update.ChangedStore);
        Assert.Equal((PageStatusKind.Working, "Comparing with the central store…"), (update.Status.Current.Kind, update.Status.Current.Message));
        Assert.False(update.IsUpdating);
        Assert.True(update.CloseCommand.CanExecute(null));
        Assert.True(update.IsBusy);
        Assert.False(update.OpenFileCommand.CanExecute(null));
        Assert.False(update.DownloadEdgeCommand.CanExecute(null));
        Assert.False(update.UpdateCommand.CanExecute(null));

        if (how == "stopped")
        {
            update.Status.CancelCommand.Execute(null);
        }

        hold.Release();
        await updating.WaitAsync(Limit, Ct);

        // What the update wrote is said all the same; under it, that the files are not shown, with Retry.
        Assert.False(update.IsBusy);
        Assert.True(update.OpenFileCommand.CanExecute(null));
        var result = update.Status.Current;
        Assert.Equal(PageStatusKind.Done, result.Kind);
        Assert.StartsWith("Central store updated: ", result.Message, StringComparison.Ordinal);
        Assert.Contains("PolicyDefinitions-kopia", result.Message, StringComparison.Ordinal);
        Assert.True(result.CanClose);
        update.Status.CloseCommand.Execute(null);
        var state = update.Status.Current;
        Assert.Equal(
            how == "stopped" ? (PageStatusKind.Attention, "CentralStore.Status.CompareStopped") : (PageStatusKind.Error, "CentralStore.Status.CompareFailed"),
            (state.Kind, state.Log.Key));
        Assert.Same(state, update.Status.Lasting);
        Assert.Equal("Retry", Assert.IsType<PageStatusAction>(state.Action).Label);
        Assert.Empty(update.Rows);
    }

    [Theory]
    [InlineData("en-US", "Preparing the update of the central store…", "Backing up the central store…", "Comparing with the central store…", "Central store updated: ")]
    [InlineData("pl-PL", "Przygotowywanie aktualizacji magazynu centralnego…", "Tworzenie kopii zapasowej magazynu centralnego…", "Porównywanie z magazynem centralnym…", "Magazyn centralny zaktualizowany: ")]
    public async Task TheUpdate_IsTheOperationOfTheWindow_HandsTheLineBackForThePreview_AndSaysWhatWasWrittenAfterTheNewComparison(
        string culture, string preparing, string backingUp, string comparing, string done)
    {
        var h = await StoreAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path, newer: true);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var update = New(h, backup: async (path, server, files, time, token) =>
        {
            reached.TrySetResult();
            await gate.Task.WaitAsync(token);
            return Path.Combine(path, "PolicyDefinitions-kopia");
        });
        update.BackupFolder = folder.Path;
        await update.OpenFileCommand.ExecuteAsync(null);
        Assert.True(update.UpdateCommand.CanExecute(null));
        var seen = new StatusRecorder(update.Status);
        var duringPreview = new List<(bool Working, bool Updating, PageStatusKind Kind)>();
        h.Windows.BeforePreview = _ =>
        {
            duringPreview.Add((update.Status.IsWorking, update.IsUpdating, update.Status.Current.Kind));
            return Task.CompletedTask;
        };

        var updating = update.UpdateCommand.ExecuteAsync(null);
        await reached.Task.WaitAsync(Limit, Ct);

        // Working: the backup, with Stop; the window cannot be closed meanwhile.
        var working = update.Status.Current;
        Assert.Equal((PageStatusKind.Working, backingUp), (working.Kind, working.Message));
        Assert.Equal("CentralStore.Updating", working.Log.ToString()); // the stage is not a new message
        Assert.Same(update.Status.CancelCommand, Assert.IsType<PageStatusAction>(working.Action).Command);
        Assert.True(update.IsUpdating);
        Assert.False(update.CloseCommand.CanExecute(null));
        gate.SetResult();
        await updating.WaitAsync(Limit, Ct);

        // The preview opened with the line handed back (summary, not working); the update then ended with the new comparison.
        Assert.Equal([(false, true, PageStatusKind.Idle)], duringPreview);
        Assert.Equal((PageStatusKind.Working, preparing), seen.Plain[0]);
        Assert.Contains((PageStatusKind.Working, backingUp), seen.Plain);
        Assert.Contains((PageStatusKind.Working, comparing), seen.Plain);
        var result = update.Status.Current;
        Assert.Equal(PageStatusKind.Done, result.Kind);
        Assert.StartsWith(done, result.Message, StringComparison.Ordinal);
        Assert.Contains("PolicyDefinitions-kopia", result.Message, StringComparison.Ordinal); // where the backup is
        Assert.StartsWith("CentralStore.Updated (", result.Log.ToString(), StringComparison.Ordinal);
        Assert.True(result.CanClose);
        Assert.True(update.ChangedStore);
        Assert.False(update.IsUpdating);
        Assert.False(update.IsBusy);
        Assert.All(update.Rows, r => Assert.Equal(CentralStoreFileStatus.Unchanged, r.Status));
        Assert.Single(h.Windows.Previews);

        // Closed, the line says what the new comparison found.
        update.Status.CloseCommand.Execute(null);
        Assert.Equal((PageStatusKind.Idle, update.Status.Idle.Message), (update.Status.Current.Kind, update.Status.Current.Message));
    }

    [Theory]
    [InlineData("en-US", "Stopping…", "The update was stopped: the central store was not changed. Press “Update…” to try again.", "Update…")]
    [InlineData("pl-PL", "Zatrzymywanie…", "Aktualizację zatrzymano: magazynu centralnego nie zmieniono. Wybierz „Zaktualizuj…”, aby spróbować ponownie.", "Zaktualizuj…")]
    public async Task Stop_DuringTheUpdate_ReturnsControl_NothingIsPreviewed_AndTheLineOffersTheUpdateAgain(string culture, string stopping, string stoppedText, string updateLabel)
    {
        var h = await StoreAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path, newer: true);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var update = New(h, backup: async (path, server, files, time, token) =>
        {
            reached.TrySetResult();
            await gate.Task.WaitAsync(token);
            return path;
        });
        update.BackupFolder = folder.Path;
        await update.OpenFileCommand.ExecuteAsync(null);
        var updating = update.UpdateCommand.ExecuteAsync(null);
        await reached.Task.WaitAsync(Limit, Ct);

        update.Status.CancelCommand.Execute(null);

        Assert.Equal(stopping, update.Status.Current.Message);
        await updating.WaitAsync(Limit, Ct);
        Assert.Empty(h.Windows.Previews);
        Assert.False(update.IsUpdating);
        Assert.False(update.ChangedStore);
        Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx), h.Sysvol.GetPolicyDefinition("contoso.admx"));
        var state = update.Status.Current;
        Assert.Equal((PageStatusKind.Attention, stoppedText), (state.Kind, state.Message));
        Assert.Equal("CentralStore.Status.UpdateStopped", state.Log.ToString());
        Assert.True(state.CanClose);
        var again = Assert.IsType<PageStatusAction>(state.Action);
        Assert.Equal(updateLabel, again.Label);
        Assert.Same(update.UpdateCommand, again.Command);

        // The action of the line updates: the store is changed this time.
        gate.SetResult();
        await ((IAsyncRelayCommand)again.Command).ExecuteAsync(again.Parameter);
        Assert.True(update.ChangedStore);
        Assert.Single(h.Windows.Previews);
    }

    [Theory]
    [InlineData("en-US", "The central store was not updated: The backup could not be written to the chosen folder; nothing was changed. (IOException)")]
    [InlineData("pl-PL", "Magazyn centralny nie został zaktualizowany: Nie udało się zapisać kopii zapasowej w wybranym folderze; niczego nie zmieniono. (IOException)")]
    public async Task ABackupThatCouldNotBeWritten_IsAnErrorThatCanBeClosed_AndNothingIsPlanned(string culture, string text)
    {
        var h = await StoreAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path, newer: true);
        using var update = New(h, backup: (_, _, _, _, _) => throw new IOException("the share does not answer"));
        update.BackupFolder = folder.Path;
        await update.OpenFileCommand.ExecuteAsync(null);

        await update.UpdateCommand.ExecuteAsync(null);

        var failed = update.Status.Current;
        Assert.Equal((PageStatusKind.Error, text), (failed.Kind, failed.Message));
        Assert.Equal("CentralStore.Status.NotUpdated [IOException]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Empty(h.Windows.Previews);
        Assert.False(update.IsUpdating);
        Assert.False(update.IsBusy);
        Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx), h.Sysvol.GetPolicyDefinition("contoso.admx"));
    }

    [Fact]
    public async Task APreviewThatWasClosedWithoutExecuting_LeavesTheBackup_AndTheLineSaysWhereItIs()
    {
        var h = await StoreAsync(English);
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path, newer: true);
        using var update = New(h, backup: (path, _, _, _, _) => Task.FromResult(Path.Combine(path, "PolicyDefinitions-kopia")));
        update.BackupFolder = folder.Path;
        await update.OpenFileCommand.ExecuteAsync(null);
        h.Windows.ExecutePreviews = false;

        await update.UpdateCommand.ExecuteAsync(null);

        Assert.False(update.ChangedStore);
        var result = update.Status.Current;
        Assert.Equal((PageStatusKind.Done, "Backup written to " + Path.Combine(folder.Path, "PolicyDefinitions-kopia") + "."), (result.Kind, result.Message));
        Assert.Equal("CentralStore.BackupWritten", result.Log.ToString());
        Assert.True(result.CanClose);
        Assert.False(update.IsUpdating);
        Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx), h.Sysvol.GetPolicyDefinition("contoso.admx"));
    }

    // ---- What the operations write ------------------------------------------------------------------------------

    /// <summary>
    /// The update writes - the backup of the store, then the plan up to the preview: it declares so, and closing the window
    /// while it runs asks first; "do not close" keeps the window and the update (audit TI-2: the older test passed through
    /// "the window is updating" alone, so the declaration could be dropped unnoticed).
    /// </summary>
    [Fact]
    public async Task TheUpdate_IsAWrite_AndClosingTheWindowWhileItRunsAsksFirst()
    {
        var h = await StoreAsync(English);
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path, newer: true);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var update = New(h, backup: async (path, _, _, _, token) =>
        {
            reached.TrySetResult();
            await gate.Task.WaitAsync(token);
            return path;
        });
        update.BackupFolder = folder.Path;
        await update.OpenFileCommand.ExecuteAsync(null);
        var updating = update.UpdateCommand.ExecuteAsync(null);
        await reached.Task.WaitAsync(Limit, Ct);

        var operation = Assert.IsType<OperationScope>(update.Status.Operation);
        Assert.True(operation.IsWrite);
        Assert.Equal(OperationWrites.Other, operation.Writes);
        Assert.True(operation.CanCancel);
        h.B2.Dialogs.CloseQuestions.Clear();
        Assert.False(await update.CanCloseAsync());
        Assert.Single(h.B2.Dialogs.CloseQuestions);
        Assert.Same(operation, update.Status.Operation);
        Assert.False(update.Status.IsCancelling);
        Assert.True(update.IsUpdating);

        // Kept: the update goes on - the plan is previewed and executed.
        gate.SetResult();
        await updating.WaitAsync(Limit, Ct);
        Assert.True(update.ChangedStore);
        Assert.Single(h.Windows.Previews);
    }

    /// <summary>A load - here a download - reads: it declares no write, and the window closes without a question.</summary>
    [Fact]
    public async Task ALoad_IsARead_AndTheWindowClosesWithoutAQuestion()
    {
        var h = await StoreAsync(English);
        var handler = new ScriptedHandler(Zip()) { Hold = true };
        using var update = New(h, new PolicyTemplatePackageLoader(handler: () => handler));
        var loading = update.DownloadEdgeCommand.ExecuteAsync(null);
        await handler.Reached.WaitAsync(Limit, Ct);

        var operation = Assert.IsType<OperationScope>(update.Status.Operation);
        Assert.False(operation.IsWrite);
        h.B2.Dialogs.CloseQuestions.Clear();
        Assert.True(await update.CanCloseAsync());
        Assert.Empty(h.B2.Dialogs.CloseQuestions);

        update.Status.CancelCommand.Execute(null);
        await loading.WaitAsync(Limit, Ct);
    }

    // ---- The state that lasts, the log ----------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US")]
    [InlineData("pl-PL")]
    public async Task WithoutASession_TheLineSaysSoFromTheStart_AndNothingIsCompared(string culture)
    {
        var h = await StoreAsync(CultureInfo.GetCultureInfo(culture));
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path);
        await h.B2.Sessions.DisconnectAsync();
        var before = h.Sysvol.Calls.Count;
        using var update = New(h);

        // Said before anything is pressed: an error that cannot be closed.
        var state = update.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.Equal(h.B2.Localizer[L.Errors.NotConnected], state.Message);
        Assert.Equal("Errors.NotConnected", state.Log.ToString());
        Assert.False(state.CanClose);

        // A package can still be read, but there is no store to compare it with, and the line keeps saying so.
        await update.OpenFileCommand.ExecuteAsync(null);

        Assert.NotNull(update.Package);
        Assert.Empty(update.Rows);
        Assert.Equal(PageStatusKind.Error, update.Status.Current.Kind);
        Assert.Equal(h.B2.Localizer[L.Errors.NotConnected], update.Status.Current.Message);
        Assert.Equal(before, h.Sysvol.Calls.Count);
        Assert.False(update.UpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheEndOfTheSession_TakesTheLine_AlsoDuringTheDownload_AndIsSaidInTheLanguageOfTheMoment()
    {
        var h = await StoreAsync(English);
        var handler = new ScriptedHandler(Zip()) { Hold = true };
        using var update = New(h, new PolicyTemplatePackageLoader(handler: () => handler));
        var loading = update.DownloadEdgeCommand.ExecuteAsync(null);
        await handler.Reached.WaitAsync(Limit, Ct);
        Assert.True(update.Status.IsWorking);
        var seen = new StatusRecorder(update.Status);

        await h.B2.Sessions.DisconnectAsync();

        // The line goes straight from "working" to the state; the download ends without a word of its own.
        Assert.True(update.IsSessionEnded);
        Assert.False(update.Status.IsWorking);
        Assert.Equal((PageStatusKind.Error, h.B2.Localizer[L.WindowSession.Ended]), (update.Status.Current.Kind, update.Status.Current.Message));
        Assert.False(update.Status.Current.CanClose);
        await loading.WaitAsync(Limit, Ct);
        Assert.Equal([(PageStatusKind.Error, h.B2.Localizer[L.WindowSession.Ended])], seen.Plain);
        Assert.False(update.IsBusy);
        Assert.Equal("WindowSession.Ended", update.Status.Current.Log.ToString());

        h.B2.Localizer.SetCulture(Polish);
        Assert.Equal(h.B2.Localizer[L.WindowSession.Ended], update.Status.Current.Message);
    }

    [Fact]
    public async Task TheSummaryFollowsTheLanguage_AndTheLogCopiesCarryKeysCountsAndCodes_NeverAnAddressAFileOrAPackage()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var h = await StoreAsync(English, logger);
        using var folder = new TempFolder();
        h.B2.Files.NextOpenPath = await ArchiveAsync(folder.Path, newer: true);
        var failing = new ScriptedHandler(Zip()) { Status = HttpStatusCode.ServiceUnavailable };
        using var update = New(h, new PolicyTemplatePackageLoader(handler: () => failing), (path, _, _, _, _) => Task.FromResult(path));
        update.BackupFolder = folder.Path;

        await update.DownloadEdgeCommand.ExecuteAsync(null); // fails: the vendor does not answer
        await update.OpenFileCommand.ExecuteAsync(null);
        var english = update.Status.Current.Message;
        await update.UpdateCommand.ExecuteAsync(null);
        h.B2.Localizer.SetCulture(Polish);

        Assert.StartsWith("Central store updated: ", update.Status.Current.Message, StringComparison.Ordinal); // a result stays as it was said
        update.Status.CloseCommand.Execute(null);
        Assert.Matches(@"^Do zapisania: \d+, bez zmian: \d+, pominięte: \d+$", update.Status.Current.Message);
        Assert.NotEqual(english, update.Status.Current.Message);
        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("CentralStore.Downloading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("CentralStore.Status.DownloadFailed", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("CentralStore.Loading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("CentralStore.Updated (", StringComparison.Ordinal));
        Assert.DoesNotContain("microsoft", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("edge", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(folder.Path, logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("policy_templates", logger.AllText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>A harness whose central store has the sample templates without the Polish files.</summary>
    private static async Task<GroupPolicyHarness> StoreAsync(CultureInfo culture, ListLogger<PageStatusHolder>? logger = null)
    {
        var h = logger is null ? await GroupPolicyHarness.CreateAsync(culture) : await GroupPolicyHarness.CreateAsync(culture, logger);
        foreach (var (path, content) in SampleAdministrativeTemplates.Files(polish: false))
        {
            h.Sysvol.PutPolicyDefinition(path, content);
        }

        return h;
    }

    private static CentralStoreUpdateViewModel New(
        GroupPolicyHarness h,
        PolicyTemplatePackageLoader? loader = null,
        Func<string, string, IReadOnlyDictionary<string, byte[]>, DateTimeOffset, CancellationToken, Task<string>>? backup = null) =>
        backup is null
            ? new(h.Services, h.Definitions, loader ?? new PolicyTemplatePackageLoader())
            : new(h.Services, h.Definitions, loader ?? new PolicyTemplatePackageLoader()) { WriteBackup = backup };

    /// <summary>Writes an archive of the sample templates (newer: a higher revision of contoso.admx) and returns its path.</summary>
    private static async Task<string> ArchiveAsync(string folder, bool newer = false)
    {
        var admx = newer
            ? SampleAdministrativeTemplates.ContosoAdmx.Replace("revision=\"1.2\"", "revision=\"1.3\"", StringComparison.Ordinal)
            : SampleAdministrativeTemplates.ContosoAdmx;
        var path = Path.Combine(folder, "policy_templates.zip");
        await File.WriteAllBytesAsync(path, Zip(admx), Ct);
        return path;
    }

    private static byte[] Zip(string contosoAdmx = SampleAdministrativeTemplates.ContosoAdmx)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(SampleAdministrativeTemplates.Utf8(text));
            }

            Add("windows/admx/contoso.admx", contosoAdmx);
            Add("windows/admx/en-US/contoso.adml", SampleAdministrativeTemplates.ContosoAdmlEn);
            Add("windows/admx/pl/contoso.adml", SampleAdministrativeTemplates.ContosoAdmlPl);
        }

        return stream.ToArray();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not come true in time.");
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>
    /// The vendor of a download: it answers the request of the release list and the file of Microsoft Edge with the
    /// archive, with the status of <see cref="Status"/>, or - while <see cref="Hold"/> - not at all until the request is
    /// cancelled.
    /// </summary>
    private sealed class ScriptedHandler(byte[] zip) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Hold { get; set; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        /// <summary>Completes when the first request is held.</summary>
        public Task Reached => _reached.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Hold)
            {
                _reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status) { RequestMessage = request };
            }

            var hash = Convert.ToHexString(SHA256.HashData(zip));
            var response = request.RequestUri!.Host switch
            {
                "edgeupdates.microsoft.com" => Json($$"""
                    [ { "Product": "Policy", "Releases": [ { "ProductVersion": "131.0.2903.48", "Artifacts": [
                      { "ArtifactName": "zip", "Location": "https://msedge.sf.dl.delivery.mp.microsoft.com/files/MicrosoftEdgePolicyTemplates.zip",
                        "Hash": "{{hash}}", "HashAlgorithm": "SHA256", "SizeInBytes": {{zip.Length}} } ] } ] } ]
                    """),
                "msedge.sf.dl.delivery.mp.microsoft.com" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            response.RequestMessage ??= request;
            return response;
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }
}
