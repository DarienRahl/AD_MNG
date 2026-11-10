using System.Globalization;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using AdMng.Testing.BulkCreate;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// Step 1 <em>Dane</em> of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.6, 2.11 Presentation B3): pastes from
/// the clipboard (text, HTML only, both), appending and the repeated-paste question, <em>Wklej i zastąp</em>, the header
/// choice and line, the Excel row offset, files (CSV in cp1250 and UTF-16, xlsx, too large), typed rows and columns, the
/// masked password-like column, localized warnings and which changes keep the check step's state.
/// </summary>
public sealed class BulkSourceTests
{
    [Fact]
    public async Task Paste_ExcelText_FillsTheTableAndTheGrid()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var source = wizard.Source;
        var replaced = 0;
        source.TableReplaced += (_, _) => replaced++;

        await wizard.PasteAsync(ExcelSamples.PolishTsv);

        Assert.Null(source.Error);
        Assert.Equal(TableSource.ClipboardText, source.Table.Source);
        Assert.True(source.Table.HasHeader);
        Assert.Equal(5, source.Table.Rows.Count);
        Assert.Equal(ExcelSamples.PolishHeaders, source.Columns.Select(c => c.Name));
        Assert.Equal(5, source.Rows.Count);
        Assert.Equal("Łucja", source.Rows[0][1]);
        Assert.Equal("Ślęzak-Nowińska", source.Rows[4][2]);
        Assert.Equal("Wiersze: 5, kolumny: 7 – nagłówki rozpoznane", source.Summary);
        Assert.Equal(2, source.ExcelRowOffset);
        Assert.Equal([2, 3, 4, 5, 6], source.Rows.Select(r => r.ExcelRow));
        Assert.Equal(1, source.HeaderLine);
        Assert.Equal(1, replaced);
        Assert.True(wizard.Wizard.CanGoNext);
    }

    [Fact]
    public async Task Paste_HtmlOnly_ReadsTheHtmlTable()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();

        await wizard.PasteAsync(null, Testing.RichHtml.CfHtmlFixture.Build(ExcelSamples.ExcelHtml));

        var table = wizard.Source.Table;
        Assert.Equal(TableSource.ClipboardHtml, table.Source);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Żak", table.Cell(0, 1));
        Assert.Equal("grzegorz.bak@contoso.com", table.Cell(1, 2));
    }

    [Fact]
    public async Task Paste_TextAndHtml_RecoversTheFullNumber_AndSaysSo()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();

        await wizard.PasteAsync(ExcelSamples.ScientificTsv, ExcelSamples.ScientificCfHtml);

        Assert.Equal(ExcelSamples.ScientificFullValue, wizard.Source.Table.Cell(0, 2));
        Assert.Contains("Wiersz 2, kolumna Telefon komórkowy: pełną liczbę odczytano z formatu HTML schowka.", wizard.Source.Warnings);
    }

    [Fact]
    public async Task Paste_ScientificWithoutHtml_Warns()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();

        await wizard.PasteAsync(ExcelSamples.ScientificTsv);

        var warning = Assert.Single(wizard.Source.Warnings, w => w.Contains("zapisie naukowym", StringComparison.Ordinal));
        Assert.StartsWith("Wiersz 2, kolumna Telefon komórkowy:", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paste_EmptyClipboard_SaysSo()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        wizard.Harness.Clipboard.Formatted = null;

        await wizard.Source.PasteCommand.ExecuteAsync(null);

        Assert.Equal("Schowek nie zawiera tabeli. Skopiuj zakres komórek w Excelu i spróbuj ponownie.", wizard.Source.Error);
        Assert.False(wizard.Source.HasTable);
        Assert.False(wizard.Wizard.CanGoNext);
    }

    [Fact]
    public async Task SecondPaste_IsAppended_RowsKeepTheirNumbers_AndTheCheckStateStays()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        var source = wizard.Source;
        var replaced = 0;
        var changed = 0;
        source.TableReplaced += (_, _) => replaced++;
        source.Changed += (_, _) => changed++;

        await wizard.PasteAsync(ExcelSamples.Tsv(
            ["Telefon komórkowy", "Imię", "Nazwisko", "Dział", "E-mail"],
            ["+48 600 200 306", "Jędrzej", "Łęcki", "Kadry", "jedrzej.lecki@firma.pl"]));

        Assert.Equal(6, source.Table.Rows.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6], source.Table.Rows.Select(r => r.Number));
        Assert.Equal("Jędrzej", source.Table.Cell(5, 1));
        Assert.Equal(0, replaced);
        Assert.Equal(1, changed);
        Assert.Null(source.RepeatedPasteQuestion);
    }

    [Fact]
    public async Task RepeatedPaste_Asks_AndCancelAppendOrReplace()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var source = wizard.Source;
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);

        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        Assert.True(source.HasRepeatedPaste);
        Assert.Equal("Te wiersze są już wklejone (powtórzone: 5 z 5). Dołączyć je mimo to, zastąpić tabelę czy anulować?", source.RepeatedPasteQuestion);
        Assert.Equal(5, source.Table.Rows.Count);
        source.CancelRepeatedCommand.Execute(null);
        Assert.False(source.HasRepeatedPaste);
        Assert.Equal(5, source.Table.Rows.Count);

        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await source.AppendRepeatedCommand.ExecuteAsync(null);
        Assert.Equal(10, source.Table.Rows.Count);

        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        Assert.True(source.HasRepeatedPaste);
        await source.ReplaceWithRepeatedCommand.ExecuteAsync(null);
        Assert.Equal(5, source.Table.Rows.Count);
        Assert.False(source.HasRepeatedPaste);
    }

    [Fact]
    public async Task PasteAndReplace_ReplacesTheTable()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.PolishTsv);
        var replaced = 0;
        wizard.Source.TableReplaced += (_, _) => replaced++;
        wizard.Harness.Clipboard.Formatted = new ClipboardContent(null, null, null, ExcelSamples.EnglishTsv);

        await wizard.Source.ReplaceWithPasteCommand.ExecuteAsync(null);

        Assert.Equal(2, wizard.Source.Table.Rows.Count);
        Assert.Equal("First Name", wizard.Source.Columns[0].Name);
        Assert.Equal(1, replaced);
    }

    [Fact]
    public async Task HeaderToggle_MakesTheHeaderData_AndBack()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var source = wizard.Source;
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        var replaced = 0;
        source.TableReplaced += (_, _) => replaced++;

        source.HeaderMode = HeaderMode.NoHeader;
        await source.PendingWork;

        Assert.False(source.Table.HasHeader);
        Assert.Equal(6, source.Table.Rows.Count);
        Assert.Equal(["A", "B", "C", "D", "E"], source.Columns.Select(c => c.Name));
        Assert.Equal("Wiersze: 6, kolumny: 5 – bez wiersza nagłówków (kolumny oznaczone literami)", source.Summary);
        Assert.Equal(1, source.ExcelRowOffset);
        Assert.Null(source.HeaderLine);
        Assert.Equal(1, replaced);

        source.HeaderMode = HeaderMode.FirstRowIsHeader;
        await source.PendingWork;

        Assert.True(source.Table.HasHeader);
        Assert.Equal(5, source.Table.Rows.Count);
        Assert.Equal("Telefon komórkowy", source.Columns[0].Name);
        Assert.Equal("Wiersze: 5, kolumny: 5 – z wierszem nagłówków", source.Summary);
        Assert.Equal(2, replaced);
    }

    [Fact]
    public async Task TitleRow_IsSkipped_AndAnotherHeaderLineCanBeChosen()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var source = wizard.Source;

        await wizard.PasteAsync(ExcelSamples.TitleRowTsv);

        Assert.Equal(2, source.Table.Rows.Count);
        Assert.Equal("Imię", source.Columns[0].Name);
        Assert.Contains(source.Warnings, w => w.StartsWith("Pominięte wiersze nad nagłówkiem", StringComparison.Ordinal));
        Assert.NotNull(source.HeaderLine);
        var detected = source.HeaderLine!.Value;

        source.HeaderLine = detected + 1;
        await source.PendingWork;
        Assert.Equal("Łucja", source.Columns[0].Name);
        Assert.Single(source.Table.Rows);

        source.HeaderLine = 99;
        await source.PendingWork;
        Assert.StartsWith("Wiersz 99 nie należy do wklejonego zakresu", source.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExcelRowOffset_ChangesTheShownRows_AndTheWarnings()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var source = wizard.Source;
        await wizard.PasteAsync(ExcelSamples.ErrorValuesTsv);
        Assert.Contains(source.Warnings, w => w.StartsWith("Wiersz 2, kolumna E-mail: wartość błędu Excela", StringComparison.Ordinal));

        source.ExcelRowOffset = 10;

        Assert.Equal(10, source.Rows[0].ExcelRow);
        Assert.Contains(source.Warnings, w => w.StartsWith("Wiersz 10, kolumna E-mail: wartość błędu Excela", StringComparison.Ordinal));
        source.ExcelRowOffset = 0;
        Assert.Equal(1, source.ExcelRowOffset);
    }

    [Fact]
    public async Task CsvFile_InTheAnsiCodePage_IsReadIntoMemory()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        using var file = new TempFile(".csv", ExcelSamples.Cp1250Csv);
        wizard.Harness.Files.NextOpenPath = file.Path;

        await wizard.Source.LoadFileCommand.ExecuteAsync(null);

        var table = wizard.Source.Table;
        Assert.Null(wizard.Source.Error);
        Assert.Equal(TableSource.CsvFile, table.Source);
        Assert.Equal(["Imię", "Nazwisko", "E-mail", "Dział"], table.Columns.Select(c => c.Header));
        Assert.Equal("Żaneta", table.Cell(1, 0));
        Assert.Equal("Ćwiek", table.Cell(1, 1));
    }

    [Fact]
    public async Task UnicodeTextFile_AndXlsx_AreRead_AndAskBeforeReplacing()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        using var text = new TempFile(".txt", ExcelSamples.Utf16UnicodeText);
        using var xlsx = new TempFile(".xlsx", ExcelSamples.MinimalXlsx(
        [
            ["Imię", "Nazwisko", "Telefon komórkowy"],
            ["Bożena", "Kąkol", "+48 600 200 303"],
        ]));
        wizard.Harness.Files.NextOpenPath = text.Path;
        await wizard.Source.LoadFileCommand.ExecuteAsync(null);
        Assert.Equal("Mikołaj", wizard.Source.Table.Cell(1, 0));

        // A loaded table is replaced only after the question.
        wizard.Harness.Files.NextOpenPath = xlsx.Path;
        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(false);
        await wizard.Source.LoadFileCommand.ExecuteAsync(null);
        Assert.Equal("Mikołaj", wizard.Source.Table.Cell(1, 0));

        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await wizard.Source.LoadFileCommand.ExecuteAsync(null);
        Assert.Equal(TableSource.XlsxFile, wizard.Source.Table.Source);
        Assert.Equal("Kąkol", wizard.Source.Table.Cell(0, 1));
        Assert.Contains(wizard.Harness.Dialogs.Shown, s => s == "confirm:Plik zastąpi wczytane dane (wiersze: 2). Kontynuować?");
    }

    [Fact]
    public async Task Files_TooLargeOrNotASheet_AreRefused()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        using var large = new TempFile(".csv", []);
        using (var stream = File.OpenWrite(large.Path))
        {
            stream.SetLength(BulkSourceViewModel.MaxFileBytes + 1);
        }

        using var broken = new TempFile(".xlsx", [1, 2, 3, 4]);
        wizard.Harness.Files.NextOpenPath = large.Path;
        await wizard.Source.LoadFileCommand.ExecuteAsync(null);
        Assert.Equal("Plik jest większy niż 50 MB i nie został wczytany.", wizard.Source.Error);

        wizard.Harness.Files.NextOpenPath = broken.Path;
        await wizard.Source.LoadFileCommand.ExecuteAsync(null);
        Assert.Equal("Nie udało się odczytać pliku jako arkusza ani pliku CSV.", wizard.Source.Error);
        Assert.False(wizard.Source.HasTable);
    }

    [Fact]
    public async Task TypedRowsAndColumns_KeepTheCheckState()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var source = wizard.Source;
        var replaced = 0;
        var changed = 0;
        source.TableReplaced += (_, _) => replaced++;
        source.Changed += (_, _) => changed++;

        // An empty wizard starts a typed table with the usual columns.
        source.AddRowCommand.Execute(null);
        Assert.Equal(TableSource.Typed, source.Table.Source);
        Assert.Equal(["Imię", "Nazwisko", "E-mail", "Telefon komórkowy", "Dział"], source.Columns.Select(c => c.Name));
        Assert.Single(source.Rows);
        Assert.Equal(1, replaced);

        source.Rows[0][0] = "Łucja";
        source.Rows[0][1] = " Żak ";
        source.AddRowCommand.Execute(null);
        source.Rows[1][0] = "Grzegorz";
        source.NewColumnHeader = "Stanowisko";
        source.AddColumnCommand.Execute(null);

        Assert.Equal(1, replaced);
        Assert.Equal("Łucja", source.Table.Cell(0, 0));
        Assert.Equal("Żak", source.Table.Cell(0, 1));
        Assert.Equal("Stanowisko", source.Columns[5].Name);
        Assert.Equal(6, source.Table.Columns.Count);
        Assert.True(changed >= 5);

        source.RemoveRowsCommand.Execute(new List<object> { source.Rows[0] });
        Assert.Single(source.Table.Rows);
        Assert.Equal(2, source.Table.Rows[0].Number);
        Assert.Equal(3, source.Table.NextRowNumber);
        Assert.Equal(1, replaced);
    }

    [Fact]
    public async Task Clear_AsksAndDropsTheTable()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.PolishTsv);

        await wizard.Source.ClearCommand.ExecuteAsync(null);
        Assert.Equal(5, wizard.Source.Table.Rows.Count);

        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await wizard.Source.ClearCommand.ExecuteAsync(null);
        Assert.Same(InputTable.Empty, wizard.Source.Table);
        Assert.Empty(wizard.Source.Rows);
        Assert.Equal(string.Empty, wizard.Source.Summary);
    }

    [Fact]
    public async Task PasswordLikeColumn_IsMasked_ReadOnly_AndWarned()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var source = wizard.Source;

        await wizard.PasteAsync(ExcelSamples.SensitiveColumnsTsv);

        var column = source.Columns[2];
        Assert.True(column.IsMasked);
        Assert.True(column.IsReadOnly);
        Assert.False(source.Columns[3].IsMasked);
        Assert.Equal(BulkSourceViewModel.Mask, source.Rows[0][2]);
        Assert.DoesNotContain("Start!2026a", source.Rows[0].Cells);
        Assert.DoesNotContain("Start!2026a", source.Rows[0].ToString(), StringComparison.Ordinal);
        source.Rows[0][2] = "inne";
        Assert.Equal("Start!2026a", source.Table.Cell(0, 2));
        Assert.Contains("Kolumna Hasło startowe wygląda na hasła: nie zostanie użyta, a jej komórki są ukryte.", source.Warnings);
    }

    [Fact]
    public async Task Dispose_DropsTheTable()
    {
        var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.PolishTsv);
        var source = wizard.Source;

        wizard.Dispose();

        Assert.Same(InputTable.Empty, source.Table);
        Assert.Empty(source.Rows);
        Assert.Empty(source.Columns);
        Assert.DoesNotContain("Łucja", source.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ObjectKind.User)]
    [InlineData(ObjectKind.Contact)]
    [InlineData(ObjectKind.Group)]
    [InlineData(ObjectKind.Computer)]
    public async Task SingleObjectDialog_SeveralAtOnce_OpensTheWizardForTheSameKindAndParent(ObjectKind kind)
    {
        var harness = await B2Harness.CreateAsync(BulkWizardHarness.Polish, bulkCreateSchema: true);
        var dialog = harness.Factory.CreateNewObject(kind, harness.Company.EmployeesOu);
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;

        Assert.True(dialog.CanOpenBulkCreate);
        await dialog.OpenBulkCreateCommand.ExecuteAsync(null);

        Assert.False(closed);
        var request = Assert.Single(harness.Dialogs.BatchEditorRequests);
        Assert.Equal(new BulkCreateRequest(kind, harness.Company.EmployeesOu), request.Create);
        Assert.Empty(request.TargetDns);
        Assert.False(harness.Factory.CreateNewObject(ObjectKind.OrganizationalUnit, harness.Company.EmployeesOu).CanOpenBulkCreate);
    }

    [Fact]
    public async Task FiveThousandRows_ArePastedOffTheUiThread()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var started = DateTime.UtcNow;

        await wizard.PasteAsync(ExcelSamples.Rows(5000));

        Assert.Equal(5000, wizard.Source.Rows.Count);
        Assert.Equal(5000, wizard.Source.Table.Rows.Count);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        Assert.Equal("Wiersze: 5000, kolumny: 5 – nagłówki rozpoznane", wizard.Source.Summary);
    }
}

/// <summary>A connected batch editor in create mode over the bulk-create sample (pl-PL), with helpers for the three steps.</summary>
internal sealed class BulkWizardHarness : IDisposable
{
    public static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private BulkWizardHarness(B2Harness harness, BulkCreateSample sample, BatchEditorViewModel editor)
    {
        Harness = harness;
        Sample = sample;
        Editor = editor;
    }

    public B2Harness Harness { get; }

    public BulkCreateSample Sample { get; }

    public BatchEditorViewModel Editor { get; }

    public BulkCreateViewModel Wizard => Editor.BulkCreate!;

    public BulkSourceViewModel Source => Wizard.Source;

    public BulkColumnsViewModel Columns => Wizard.Columns;

    public BulkRulesViewModel Rules => Wizard.Rules;

    /// <param name="kind">The kind of the created objects.</param>
    /// <param name="harness">An existing harness (a second wizard in the same "application").</param>
    /// <param name="sample">The bulk-create sample of <paramref name="harness"/> (seeded once).</param>
    /// <param name="container">The target container (default: <em>Nowi pracownicy</em>).</param>
    public static async Task<BulkWizardHarness> CreateAsync(ObjectKind kind = ObjectKind.User, B2Harness? harness = null, BulkCreateSample? sample = null, string? container = null)
    {
        harness ??= await B2Harness.CreateAsync(Polish, bulkCreateSchema: true);
        sample ??= harness.Builder.AddBulkCreateSample(harness.Company);
        var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([]) { Create = new BulkCreateRequest(kind, container ?? sample.NewEmployeesOu) });
        await editor.InitializeAsync();
        return new BulkWizardHarness(harness, sample, editor);
    }

    /// <summary>Ctrl+V with <paramref name="text"/> (and an optional CF_HTML) on the clipboard.</summary>
    public async Task PasteAsync(string? text, byte[]? cfHtml = null)
    {
        Harness.Clipboard.Formatted = new ClipboardContent(cfHtml, null, null, text);
        await Source.PasteCommand.ExecuteAsync(null);
    }

    /// <summary><em>Dalej</em> to step 3 (the first free number and the samples awaited).</summary>
    public async Task GoToRulesAsync()
    {
        while (Wizard.Step < BulkCreateStep.Rules)
        {
            Assert.True(Wizard.CanGoNext, $"Dalej is not possible in {Wizard.Step}: {Columns.MappingError ?? Rules.Error}");
            await Wizard.NextStepCommand.ExecuteAsync(null);
        }

        await SettleAsync();
    }

    /// <summary>Waits for the counter preview and the samples of step 3.</summary>
    public async Task SettleAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            await Rules.PreviewWork;
            await Rules.SampleWork;
        }
    }

    public BulkColumnMappingViewModel Mapping(string name) => Columns.Mappings.Single(m => m.Name == name);

    public AttributeRuleViewModel Rule(string attribute) => Rules.Rules.Single(r => string.Equals(r.Attribute, attribute, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => Editor.Dispose();
}

/// <summary>A file in a test's own temporary directory (deleted with the directory).</summary>
internal sealed class TempFile : IDisposable
{
    private readonly string _directory;

    public TempFile(string extension, byte[] content)
    {
        _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "admng-bulk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "dane" + extension);
        File.WriteAllBytes(Path, content);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
