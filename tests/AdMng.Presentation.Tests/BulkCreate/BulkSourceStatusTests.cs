using System.Globalization;
using System.Runtime.CompilerServices;
using AdMng.Core.Batch.Creation;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Presentation.Tests.Infrastructure;
using AdMng.Testing.BulkCreate;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// What step 1 <em>Dane</em> of the bulk creation wizard says in the status line of the batch editor (track V, stage V2b-4;
/// docs/dev/v-interface-renewal.md "V2b-4 as built"): its summary, reading a paste or a file with Stop (control comes back
/// at once, the table is not changed), why nothing was pasted or loaded, and that nothing of the operator's sheet - a cell,
/// a header, a file name - reaches a log copy. How a table is read and kept is covered, unchanged, by
/// <see cref="BulkSourceTests"/>.
/// </summary>
public sealed class BulkSourceStatusTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public async Task ThePaste_IsAnOperationOfTheStep_AndEndsAsTheSummaryOfTheTable()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var line = wizard.Editor.Status;
        Assert.Equal(PageStatusKind.Idle, line.Current.Kind);
        Assert.Equal(string.Empty, line.Current.Message);
        var seen = new StatusRecorder(line);

        await wizard.PasteAsync(ExcelSamples.PolishTsv);

        // Working - not stoppable by a button, but with Stop in the line - and then what the table is.
        Assert.Equal(
            [(PageStatusKind.Working, "Odczytywanie wklejonej tabeli…"), (PageStatusKind.Idle, "Wiersze: 5, kolumny: 7 – nagłówki rozpoznane")],
            seen.Plain);
        Assert.Equal(wizard.Source.Summary, line.Current.Message);
        Assert.False(wizard.Source.Status.IsWorking);
        Assert.Null(wizard.Source.Status.Lasting);
        Assert.False(line.CloseCommand.CanExecute(null));
    }

    [Fact]
    public async Task ThePaste_SaysItsWork_AndItsSummary_InEnglish()
    {
        var harness = await B2Harness.CreateAsync(English, bulkCreateSchema: true);
        using var wizard = await BulkWizardHarness.CreateAsync(harness: harness);
        var seen = new StatusRecorder(wizard.Editor.Status);

        await wizard.PasteAsync(ExcelSamples.EnglishTsv);

        Assert.Equal((PageStatusKind.Working, "Reading the pasted table…"), seen.Plain[0]);
        Assert.Equal(PageStatusKind.Idle, seen.Plain[^1].Kind);
        Assert.StartsWith("Rows: ", seen.Plain[^1].Message, StringComparison.Ordinal);
        Assert.Equal(wizard.Source.Summary, seen.Plain[^1].Message);
    }

    [Fact]
    public async Task ReadingAFile_NamesTheFileOnScreen_ButNeverInALogCopy()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        using var file = new TempFile(".txt", ExcelSamples.Utf16UnicodeText);
        wizard.Harness.Files.NextOpenPath = file.Path;
        var name = Path.GetFileName(file.Path);
        var seen = new StatusRecorder(wizard.Editor.Status);
        var logs = new List<string>();
        wizard.Editor.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                logs.Add(wizard.Editor.Status.Current.ToString());
            }
        };

        await wizard.Source.LoadFileCommand.ExecuteAsync(null);

        Assert.Equal((PageStatusKind.Working, $"Odczytywanie pliku „{name}”…"), seen.Plain[0]);
        Assert.Equal(PageStatusKind.Idle, seen.Plain[^1].Kind);
        Assert.Equal(wizard.Source.Summary, seen.Plain[^1].Message);
        Assert.Contains("Working: BulkCreate.Status.ReadingFile", logs);
        Assert.DoesNotContain(logs, log => log.Contains(name, StringComparison.OrdinalIgnoreCase) || log.Contains("Mikołaj", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_OfAPaste_GivesControlBackAtOnce_AndTheTableIsNotChanged()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var source = wizard.Source;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The parsing waits on a pool thread and cannot be interrupted - like a long parse of a big sheet.
        source.ReadGate = () => gate.Task;
        wizard.Harness.Clipboard.Formatted = new ClipboardContent(null, null, null, ExcelSamples.PolishTsv);
        var seen = new StatusRecorder(wizard.Editor.Status);
        try
        {
            var pasting = source.PasteCommand.ExecuteAsync(null);

            var working = wizard.Editor.Status.Current;
            Assert.Equal(PageStatusKind.Working, working.Kind);
            Assert.Equal("Odczytywanie wklejonej tabeli…", working.Message);
            Assert.Equal("BulkCreate.Status.ReadingPaste", working.Log.ToString());
            Assert.True(source.IsBusy);
            Assert.False(source.PasteCommand.CanExecute(null));
            var stop = Assert.IsType<PageStatusAction>(working.Action);
            Assert.Equal("Zatrzymaj", stop.Label);
            Assert.Same(source.Status.CancelCommand, stop.Command);

            // "Stopping…" at once, and the page does not wait for the thread that still parses.
            stop.Command.Execute(null);
            await pasting.WaitAsync(Limit, TestContext.Current.CancellationToken);
        }
        finally
        {
            gate.SetResult();
        }

        Assert.Equal(
            [
                (PageStatusKind.Working, "Odczytywanie wklejonej tabeli…"),
                (PageStatusKind.Working, "Zatrzymywanie…"),
                (PageStatusKind.Attention, "Odczytywanie wklejonej tabeli zatrzymano: tabeli nie zmieniono. Wybierz „Wklej”, aby spróbować ponownie."),
            ],
            seen.Plain);
        var stopped = wizard.Editor.Status.Current;
        Assert.Equal("BulkCreate.Status.PasteStopped", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.Equal("Wklej", stopped.Action!.Label);
        Assert.Same(source.PasteCommand, stopped.Action.Command);
        Assert.False(source.IsBusy);
        Assert.False(source.HasTable);
        Assert.Empty(source.Rows);
        Assert.Null(source.Error);
        Assert.True(source.PasteCommand.CanExecute(null));

        // The action of the line pastes again: the table of that paste, and the summary.
        source.ReadGate = null;
        await ((IAsyncRelayCommand)stopped.Action.Command).ExecuteAsync(null);
        Assert.Equal(5, source.Rows.Count);
        Assert.Equal(PageStatusKind.Idle, wizard.Editor.Status.Current.Kind);
        Assert.Equal(source.Summary, wizard.Editor.Status.Current.Message);
    }

    [Fact]
    public async Task AStoppedFileRead_ThatFailsAfterwards_IsNotReportedAsAnUnexpectedError()
    {
        // The operator stops a read when it hangs - a workbook on a share that went away. The thread still reads; when it
        // then fails, nobody waits for it. An exception nobody observed is the application's "unexpected error" dialog,
        // minutes later, about a file the operator gave up on.
        using var unobserved = new UnobservedTaskExceptions();
        var path = Path.Combine(Path.GetTempPath(), "admng-gone-" + Guid.NewGuid().ToString("N") + ".xlsx");

        await StopAReadOfAFileThatIsGoneAsync(path);

        Assert.False(await unobserved.CollectAsync(e => e is FileNotFoundException gone && gone.FileName == path, rounds: 8));
    }

    /// <summary>In a method of its own so that no local keeps the abandoned read alive when the test collects.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task StopAReadOfAFileThatIsGoneAsync(string path)
    {
        var harness = await B2Harness.CreateAsync(English, bulkCreateSchema: true);
        using var wizard = await BulkWizardHarness.CreateAsync(harness: harness);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wizard.Source.ReadGate = async () =>
        {
            await gate.Task.ConfigureAwait(false);
            read.SetResult();
        };
        harness.Files.NextOpenPath = path;

        var loading = wizard.Source.LoadFileCommand.ExecuteAsync(null);
        wizard.Editor.Status.Current.Action!.Command.Execute(null);
        await loading.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.Equal("BulkCreate.Status.FileStopped", wizard.Editor.Status.Current.Log.ToString());

        // Now the abandoned thread opens the file, which is not there.
        gate.SetResult();
        await read.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stop_OfAFile_NamesTheFile_AndOffersToLoadAFileAgain()
    {
        var harness = await B2Harness.CreateAsync(English, bulkCreateSchema: true);
        using var wizard = await BulkWizardHarness.CreateAsync(harness: harness);
        using var file = new TempFile(".txt", ExcelSamples.Utf16UnicodeText);
        var name = Path.GetFileName(file.Path);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wizard.Source.ReadGate = () => gate.Task;
        harness.Files.NextOpenPath = file.Path;
        var seen = new StatusRecorder(wizard.Editor.Status);
        try
        {
            var loading = wizard.Source.LoadFileCommand.ExecuteAsync(null);
            Assert.Equal($"Reading “{name}”…", wizard.Editor.Status.Current.Message);

            wizard.Editor.Status.Current.Action!.Command.Execute(null);
            await loading.WaitAsync(Limit, TestContext.Current.CancellationToken);
        }
        finally
        {
            gate.SetResult();
        }

        Assert.Equal(
            [
                (PageStatusKind.Working, $"Reading “{name}”…"),
                (PageStatusKind.Working, "Stopping…"),
                (PageStatusKind.Attention, $"Reading “{name}” was stopped: the table was not changed. Press “Load file…” to load a file again."),
            ],
            seen.Plain);
        var stopped = wizard.Editor.Status.Current;
        Assert.Equal("BulkCreate.Status.FileStopped", stopped.Log.ToString());
        Assert.Equal("Load file…", stopped.Action!.Label);
        Assert.Same(wizard.Source.LoadFileCommand, stopped.Action.Command);
        Assert.False(wizard.Source.HasTable);
        Assert.False(wizard.Source.IsBusy);
    }

    [Fact]
    public async Task AClipboardWithoutATable_IsWhatWasSkippedAndWhy_UntilItIsClosed()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        wizard.Harness.Clipboard.Formatted = null;

        await wizard.Source.PasteCommand.ExecuteAsync(null);

        var skipped = wizard.Editor.Status.Current;
        Assert.Equal(PageStatusKind.Attention, skipped.Kind);
        Assert.Equal("Nic nie wklejono: Schowek nie zawiera tabeli. Skopiuj zakres komórek w Excelu i spróbuj ponownie.", skipped.Message);
        Assert.Equal("BulkCreate.Status.NothingPasted", skipped.Log.ToString());
        Assert.True(skipped.CanClose);
        Assert.Null(skipped.Action);
        Assert.Equal("Schowek nie zawiera tabeli. Skopiuj zakres komórek w Excelu i spróbuj ponownie.", wizard.Source.Error);
        Assert.False(wizard.Source.IsBusy);

        // A result is closed by the operator, and the page shows its summary again - here: nothing yet.
        wizard.Editor.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, wizard.Editor.Status.Current.Kind);
        Assert.Equal(string.Empty, wizard.Editor.Status.Current.Message);
    }

    [Fact]
    public async Task AFileThatCannotBeUsed_SaysWhatWasNotLoaded_Why_AndWhatToDo_AndTheTableOfBeforeStays()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.PolishTsv);
        var summary = wizard.Source.Summary;
        using var large = new TempFile(".csv", []);
        using (var stream = File.OpenWrite(large.Path))
        {
            stream.SetLength(BulkSourceViewModel.MaxFileBytes + 1);
        }

        using var broken = new TempFile(".xlsx", [1, 2, 3, 4]);
        var missing = Path.Combine(Path.GetDirectoryName(large.Path)!, "nie-ma.csv");

        // Too large: an error, with the limit and the advice - the old table is still the table.
        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        wizard.Harness.Files.NextOpenPath = large.Path;
        await wizard.Source.LoadFileCommand.ExecuteAsync(null);
        var tooLarge = wizard.Editor.Status.Current;
        Assert.Equal(PageStatusKind.Error, tooLarge.Kind);
        Assert.Equal($"Nie wczytano pliku „{Path.GetFileName(large.Path)}”: Plik jest większy niż 50 MB. Wybierz inny plik.", tooLarge.Message);
        Assert.Equal("BulkCreate.Status.FileNotLoaded", tooLarge.Log.ToString());
        Assert.Equal("Plik jest większy niż 50 MB i nie został wczytany.", wizard.Source.Error);
        Assert.True(tooLarge.CanClose);
        Assert.Equal(5, wizard.Source.Rows.Count);

        // Not a sheet and not a CSV file.
        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        wizard.Harness.Files.NextOpenPath = broken.Path;
        await wizard.Source.LoadFileCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Error, wizard.Editor.Status.Current.Kind);
        Assert.Equal(
            $"Nie wczytano pliku „{Path.GetFileName(broken.Path)}”: Nie udało się odczytać pliku jako arkusza ani pliku CSV. Wybierz inny plik.",
            wizard.Editor.Status.Current.Message);

        // A file that is not there: the reason of the system and the kind of error as the code - never a path in a log copy.
        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        wizard.Harness.Files.NextOpenPath = missing;
        await wizard.Source.LoadFileCommand.ExecuteAsync(null);
        var notThere = wizard.Editor.Status.Current;
        Assert.Equal(PageStatusKind.Error, notThere.Kind);
        Assert.StartsWith("Nie wczytano pliku „nie-ma.csv”: ", notThere.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Wybierz inny plik. (FileNotFoundException)", notThere.Message, StringComparison.Ordinal);
        Assert.Equal("BulkCreate.Status.FileNotLoaded [FileNotFoundException]", notThere.Log.ToString());
        Assert.DoesNotContain("nie-ma", notThere.Log.ToString(), StringComparison.Ordinal);

        // Closed: the summary of the table that is still there.
        wizard.Editor.Status.CloseCommand.Execute(null);
        Assert.Equal((PageStatusKind.Idle, summary), (wizard.Editor.Status.Current.Kind, wizard.Editor.Status.Current.Message));
        Assert.Equal(5, wizard.Source.Rows.Count);
    }

    [Fact]
    public async Task ARebuildOfTheTable_IsAnOperationWithoutStop_AndAHeaderRowOutOfRangeIsWhatWasNotChanged()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var source = wizard.Source;
        await wizard.PasteAsync(ExcelSamples.TitleRowTsv);
        var detected = source.HeaderLine!.Value;
        var seen = new StatusRecorder(wizard.Editor.Status);

        source.HeaderLine = detected + 1;
        await source.PendingWork;

        // Quick, in memory, and nothing is left half done by a stop: no Stop in the line.
        Assert.Equal(PageStatusKind.Working, seen.Seen[0].Kind);
        Assert.Equal("Aktualizowanie tabeli…", seen.Seen[0].Message);
        Assert.Equal(PageStatusKind.Idle, seen.Plain[^1].Kind);
        Assert.Equal(source.Summary, seen.Plain[^1].Message);
        var rebuilding = new List<PageStatus>();
        wizard.Editor.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current) && wizard.Editor.Status.Current.IsWorking)
            {
                rebuilding.Add(wizard.Editor.Status.Current);
            }
        };

        source.HeaderLine = 99;
        await source.PendingWork;

        Assert.All(rebuilding, status => Assert.Null(status.Action));
        var refused = wizard.Editor.Status.Current;
        Assert.Equal(PageStatusKind.Attention, refused.Kind);
        Assert.StartsWith("Nie zmieniono wiersza nagłówków: Wiersz 99 nie należy do wklejonego zakresu", refused.Message, StringComparison.Ordinal);
        Assert.Equal("BulkCreate.Status.HeaderNotChanged", refused.Log.ToString());
        Assert.Equal($"Nie zmieniono wiersza nagłówków: {source.Error}", refused.Message);
    }

    [Fact]
    public async Task AColumnOverTheLimit_IsWhatWasNotAdded_AndTheNextActionClosesIt()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var source = wizard.Source;
        for (var i = 0; i < TableTextReader.MaxColumns; i++)
        {
            source.AddColumnCommand.Execute($"Kolumna {i + 1}");
        }

        Assert.Null(source.Error);
        Assert.Equal(PageStatusKind.Idle, wizard.Editor.Status.Current.Kind);

        source.AddColumnCommand.Execute("Jedna za dużo");

        var refused = wizard.Editor.Status.Current;
        Assert.Equal(PageStatusKind.Attention, refused.Kind);
        Assert.Equal($"Nie dodano kolumny: Tabela nie może mieć więcej kolumn (limit: {TableTextReader.MaxColumns}).", refused.Message);
        Assert.Equal("BulkCreate.Status.ColumnNotAdded", refused.Log.ToString());

        // Typing on goes on: what was said about the last action is over.
        source.AddRowCommand.Execute(null);
        Assert.Null(source.Error);
        Assert.Equal(PageStatusKind.Idle, wizard.Editor.Status.Current.Kind);
        Assert.Equal(source.Summary, wizard.Editor.Status.Current.Message);
    }

    [Fact]
    public async Task TheRepeatedPasteQuestion_IsANoteOfTheStep_NotAStatus()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        var summary = wizard.Source.Summary;

        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);

        // The question has three answers and stays next to the buttons of the step: the line has its summary.
        Assert.True(wizard.Source.HasRepeatedPaste);
        Assert.Equal((PageStatusKind.Idle, summary), (wizard.Editor.Status.Current.Kind, wizard.Editor.Status.Current.Message));
        Assert.False(wizard.Source.IsBusy);
    }

    [Fact]
    public async Task TheSummary_FollowsTheLanguage()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.PolishTsv);
        Assert.Equal("Wiersze: 5, kolumny: 7 – nagłówki rozpoznane", wizard.Editor.Status.Current.Message);

        wizard.Harness.Localizer.SetCulture(English);

        Assert.Equal("Rows: 5, columns: 7 – headers recognized", wizard.Source.Summary);
        Assert.Equal(wizard.Source.Summary, wizard.Editor.Status.Current.Message);
    }

    [Fact]
    public async Task ClosingTheWizard_EndsAReadThatRunsWithoutAMessage()
    {
        var wizard = await BulkWizardHarness.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wizard.Source.ReadGate = () => gate.Task;
        wizard.Harness.Clipboard.Formatted = new ClipboardContent(null, null, null, ExcelSamples.PolishTsv);
        var status = wizard.Source.Status;
        try
        {
            var pasting = wizard.Source.PasteCommand.ExecuteAsync(null);
            Assert.True(status.IsWorking);

            wizard.Dispose();

            Assert.False(status.IsWorking);
            Assert.Equal(PageStatusKind.Idle, status.Current.Kind);
            Assert.Null(wizard.Editor.Status.Front);
        }
        finally
        {
            gate.SetResult();
        }
    }
}
