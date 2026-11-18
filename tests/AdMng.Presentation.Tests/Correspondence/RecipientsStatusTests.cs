using AdMng.Presentation.Feedback;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;
using static AdMng.Presentation.Tests.Correspondence.PageTests;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The status line of "1 · Odbiorcy" (track V, stage V2b-4, unit 5; docs/dev/v-interface-renewal.md "V2b-4 as built"): the
/// counts of the list are its summary; reading files and reading the directory are its operations, with Stop; what an import
/// or an action of the list came to is a result; the end of the session is its state. How rows are made is covered,
/// unchanged, by <see cref="RecipientsImportTests"/>, <see cref="RecipientsDirectoryTests"/> and <see cref="RecipientsListTests"/>.
/// </summary>
public sealed class RecipientsStatusTests
{
    private const string ContactsCsv =
        "Nazwa;E-mail;Miejscowość\r\n"
        + "Urząd Gminy Zgierz;urzad@gmina-zgierz.pl;Zgierz\r\n"
        + "Urząd Gminy Andrespol;sekretariat@andrespol.pl;Andrespol\r\n"
        + "Starostwo bez adresu;brak;Łódź\r\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCounts_AreTheSummaryOfThePage_AndFollowTheLanguage()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        Assert.Same(page.Status, window.Status.Front);
        Assert.Equal((PageStatusKind.Idle, "Odbiorcy: 0 • do wysłania: 0"), (window.Status.Current.Kind, window.Status.Current.Message));

        page.AddRows([new AdMng.Core.Correspondence.Import.ImportedContact("jan@kowalski.pl", "Jan", new Dictionary<string, string>())], "ręcznie");

        Assert.Equal("Odbiorcy: 1 • do wysłania: 1", window.Status.Current.Message);
        Assert.Equal(page.CountsText, window.Status.Current.Message);
        scenario.Harness.Localizer.SetCulture(English);
        Assert.Equal("Recipients: 1 • to send: 1", window.Status.Current.Message);
    }

    [Fact]
    public async Task AFile_IsTheOperationOfThePage_ThenADoneWithTheCounts_AndNoNameInTheLogCopy()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var csv = File(scenario, "kontakty.csv", Cp1250.GetBytes(ContactsCsv));
        scenario.Dialogs.Files.Enqueue([csv]);
        var line = new StatusRecorder(window.Status);
        var logs = new List<string>();
        page.Status.PropertyChanged += (_, _) => logs.Add(page.Status.Current.Log.ToString());

        await page.ImportFilesCommand.ExecuteAsync(null);

        Assert.Equal(
        [
            (PageStatusKind.Working, "Wczytuję „kontakty.csv”…"),
            (PageStatusKind.Done, "Wczytano plik „kontakty.csv” (CSV): 2 nowe, 1 błędny adres"),
        ], line.Plain);
        Assert.Equal(["Correspondence.Recipients.Status.FileReading", "Correspondence.Recipients.ImportedFile (2, 0, 1)"], logs.Distinct());
        Assert.True(window.Status.Current.CanClose);
        Assert.Equal(page.StatusText, window.Status.Current.Message);

        // Closed: the counts are the summary again.
        window.Status.CloseCommand.Execute(null);
        Assert.Equal((PageStatusKind.Idle, "Odbiorcy: 2 • do wysłania: 2"), (window.Status.Current.Kind, window.Status.Current.Message));
    }

    [Fact]
    public async Task SeveralFiles_SayHowFarTheyAre_AndFilesThatCannotBeReadAreOneSentenceWithWhatWasRead()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var good = File(scenario, "kontakty.csv", Cp1250.GetBytes(ContactsCsv));
        var broken = File(scenario, "zepsuty.xlsx", System.Text.Encoding.ASCII.GetBytes("to nie jest arkusz"));
        scenario.Dialogs.Files.Enqueue([broken, good]);
        var line = new StatusRecorder(window.Status);

        await page.ImportFilesCommand.ExecuteAsync(null);

        Assert.Equal(PageStatusKind.Working, line.Seen[0].Kind);
        Assert.Equal("Wczytuję pliki: 0 z 2…", line.Seen[0].Message);
        Assert.Equal(0d, line.Seen[0].Fraction);
        Assert.Contains(line.Seen, s => s is { Kind: PageStatusKind.Working, Message: "Wczytuję pliki: 2 z 2…", Fraction: 1d });
        var result = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, result.Kind);
        Assert.StartsWith("Wczytano pliki (1): 2 nowe, 1 błędny adres Nie wczytano: Nie udało się wczytać „zepsuty.xlsx”: ", result.Message, StringComparison.Ordinal);
        Assert.Equal("Correspondence.Recipients.Status.ImportPartly (2, 1)", result.Log.ToString());

        // None of them could be read: an error, with the reasons and no counts.
        scenario.Dialogs.Files.Enqueue([broken]);
        await page.ImportFilesCommand.ExecuteAsync(null);
        var failed = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith("Nie wczytano żadnego pliku. Nie udało się wczytać „zepsuty.xlsx”: ", failed.Message, StringComparison.Ordinal);
        Assert.Equal("Correspondence.Recipients.Status.ImportFailed (1)", failed.Log.ToString());
    }

    [Fact]
    public async Task Stop_ReturnsControlAtOnce_TheFilesReadSoFarStay_AndWhatTheThreadReadsLaterIsDropped()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var csv = File(scenario, "kontakty.csv", Cp1250.GetBytes(ContactsCsv));
        var gate = new TaskCompletionSource();
        page.ReadGate = gate.Task;
        scenario.Dialogs.Files.Enqueue([csv]);
        var run = page.ImportFilesCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => page.Status.IsWorking);
        var line = new StatusRecorder(window.Status);

        // The thread that parses is held: Stop gives control back at once - the line says "Stopping…" and then it ended.
        Assert.Equal("Zatrzymaj", window.Status.Current.Action?.Label);
        window.Status.Current.Action!.Command.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.False(page.Status.IsWorking);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (line.Seen[0].Kind, line.Seen[0].Message));
        var stopped = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Wczytywanie zatrzymano po 0 z 1 plików: wiersze z już wczytanych plików są na liście.", stopped.Message);
        Assert.Equal("Correspondence.Recipients.Status.FilesStopped (0, 1)", stopped.Log.ToString());
        Assert.Empty(page.Items);

        // The thread runs to its end later; its result is dropped.
        gate.SetResult();
        await Task.Delay(100, Ct);
        Assert.Empty(page.Items);
        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
    }

    [Fact]
    public async Task TheDirectoryRead_IsAnOperation_WithStop_AndRetryReadsTheSameObjectsAgain()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        page.SelectedObjects = [scenario.Company.JanKowalski];
        using var held = new HeldDirectoryCall(scenario.Harness.Session);
        var run = page.AddFromSelectionCommand.ExecuteAsync(null);
        await held.Reached.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal((PageStatusKind.Working, "Odczytuję z katalogu: 1 obiekt…"), (window.Status.Current.Kind, window.Status.Current.Message));
        Assert.Equal("Correspondence.Recipients.Status.DirectoryReading (1)", window.Status.Current.Log.ToString());
        window.Status.Current.Action!.Command.Execute(null);
        Assert.Equal("Zatrzymywanie…", window.Status.Current.Message);
        held.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        var stopped = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Nic nie dodano z katalogu: Odczyt został zatrzymany.", stopped.Message);
        Assert.Equal("Correspondence.Recipients.Status.DirectoryStopped (1)", stopped.Log.ToString());
        Assert.Equal("Ponów", stopped.Action?.Label);
        Assert.Empty(page.Items);

        // Retry (the action of the line) reads the same objects: the answer is the rows and a done.
        stopped.Action!.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => page.Items.Count == 1);
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
        Assert.Equal("Z katalogu: 1 nowy", window.Status.Current.Message);
        Assert.Equal("Correspondence.Recipients.FromDirectory (1, 0, 0, 0, 0)", window.Status.Current.Log.ToString());
    }

    [Fact]
    public async Task TheSessionEnding_StopsTheRead_Quietly_AndTheStateOfThePageSaysWhy()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        page.SelectedObjects = [scenario.Company.JanKowalski];
        using var held = new HeldDirectoryCall(scenario.Harness.Session);
        var run = page.AddFromSelectionCommand.ExecuteAsync(null);
        await held.Reached.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var line = new StatusRecorder(window.Status);

        await scenario.Harness.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => !page.CanUseDirectory);
        held.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(PageStatusKind.Attention, window.Status.Current.Kind);
        Assert.False(window.Status.Current.CanClose);
        Assert.StartsWith("Sesja, w której otwarto to okno, została zakończona", window.Status.Current.Message, StringComparison.Ordinal);
        Assert.Same(window.Status.Current, page.Status.Lasting);
        Assert.Empty(page.Items);
        Assert.DoesNotContain(line.Seen, s => s.Kind is PageStatusKind.Idle or PageStatusKind.Error);
        Assert.Equal(PageStatusKind.Attention, line.Seen[^1].Kind);
    }

    [Fact]
    public async Task AQuickActionOfTheList_AnswersInTheLine_ButNeverEndsAReadThatRuns()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;

        // Nothing selected: the answer is an attention that says so.
        page.RemoveSelectedCommand.Execute(null);
        Assert.Equal((PageStatusKind.Attention, page.StatusText), (window.Status.Current.Kind, window.Status.Current.Message));
        Assert.Equal("Correspondence.Recipients.RemoveNone", window.Status.Current.Log.ToString());

        // A read runs: the same action changes the old text of the page, and the read stays what the line says.
        page.SelectedObjects = [scenario.Company.JanKowalski];
        using var held = new HeldDirectoryCall(scenario.Harness.Session);
        var run = page.AddFromSelectionCommand.ExecuteAsync(null);
        await held.Reached.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var toastsBefore = scenario.Harness.Notifications.Items.Count;
        page.ClearExclusionsCommand.Execute(null);
        Assert.Equal(PageStatusKind.Working, window.Status.Current.Kind);
        Assert.True(page.Status.IsWorking);

        // The answer is not dropped while the line is the read's: it is a toast, with the same sentence.
        var toast = Assert.Single(scenario.Harness.Notifications.Items.Skip(toastsBefore));
        Assert.Equal(page.StatusText, toast.Message);
        held.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task AFileImport_StartedWhileTheDirectoryIsRead_WaitsForItsTurn_AndBothAddTheirRows()
    {
        // The window was opened from a selection (a group with thousands of members) and the operator imports a file
        // meanwhile. The import used to take the line and cancel the read without a word: the members not read yet were
        // missing from the list, and the letter went out without them.
        using var scenario = await CorrespondenceShellScenario.CreateAsync(English);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        page.SelectedObjects = [scenario.Company.JanKowalski, scenario.Company.LukaszWisniewski];
        using var held = new HeldDirectoryCall(scenario.Harness.Session);
        var reading = page.AddFromSelectionCommand.ExecuteAsync(null);
        await held.Reached.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        scenario.Dialogs.Files.Enqueue([File(scenario, "kontakty.csv", Cp1250.GetBytes(ContactsCsv))]);
        Assert.True(page.ImportFilesCommand.CanExecute(null));
        var importing = page.ImportFilesCommand.ExecuteAsync(null);

        // The read still has the line and its token: nothing replaced it.
        await Task.Delay(100, Ct);
        Assert.False(importing.IsCompleted);
        Assert.Equal("Correspondence.Recipients.Status.DirectoryReading (2)", window.Status.Current.Log.ToString());
        Assert.Empty(page.Items);

        held.Release();
        await reading.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await importing.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(
            ["jan.kowalski@contoso.test", "lukasz.wisniewski@contoso.test", "sekretariat@andrespol.pl", "urzad@gmina-zgierz.pl"],
            page.Items.Where(r => r.IsValid).Select(r => r.Email).Order(StringComparer.Ordinal));
        Assert.Equal(PageStatusKind.Done, window.Status.Current.Kind);
    }

    [Fact]
    public async Task ADirectoryRead_StartedWhileFilesAreRead_WaitsForItsTurn_AndBothAddTheirRows()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(English);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var first = File(scenario, "a.csv", Cp1250.GetBytes(ContactsCsv));
        var second = File(scenario, "b.csv", Cp1250.GetBytes("Nazwa;E-mail;Miejscowość\r\nUrząd Miasta Pabianice;sekretariat@pabianice.pl;Pabianice\r\nUrząd Gminy Ksawerów;urzad@ksawerow.pl;Ksawerów\r\n"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.ReadGate = gate.Task;
        scenario.Dialogs.Files.Enqueue([first, second]);
        var importing = page.ImportFilesCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => page.Status.IsWorking);

        page.SelectedObjects = [scenario.Company.JanKowalski];
        var reading = page.AddFromSelectionCommand.ExecuteAsync(null);
        await Task.Delay(100, Ct);
        Assert.False(reading.IsCompleted);
        Assert.StartsWith("Correspondence.Recipients.Status.FilesReading", window.Status.Current.Log.ToString(), StringComparison.Ordinal);

        gate.SetResult();
        await importing.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await reading.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(
            ["jan.kowalski@contoso.test", "sekretariat@andrespol.pl", "sekretariat@pabianice.pl", "urzad@gmina-zgierz.pl", "urzad@ksawerow.pl"],
            page.Items.Where(r => r.IsValid).Select(r => r.Email).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Stop_OfTheReadThatRuns_LeavesTheOneThatWaitsForItsTurn()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(English);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        page.SelectedObjects = [scenario.Company.JanKowalski];
        using var held = new HeldDirectoryCall(scenario.Harness.Session);
        var reading = page.AddFromSelectionCommand.ExecuteAsync(null);
        await held.Reached.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        scenario.Dialogs.Files.Enqueue([File(scenario, "kontakty.csv", Cp1250.GetBytes(ContactsCsv))]);
        var importing = page.ImportFilesCommand.ExecuteAsync(null);

        // Stop is about what the line says: the read. The import that waited runs after it.
        window.Status.Current.Action!.Command.Execute(null);
        held.Release();
        await reading.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await importing.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(
            ["sekretariat@andrespol.pl", "urzad@gmina-zgierz.pl"],
            page.Items.Where(r => r.IsValid).Select(r => r.Email).Order(StringComparer.Ordinal));
    }
}
