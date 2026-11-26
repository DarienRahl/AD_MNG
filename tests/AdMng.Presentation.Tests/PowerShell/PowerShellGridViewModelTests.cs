using System.Globalization;
using AdMng.Core.PowerShell;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.PowerShell;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>The grid of Out-AdmGrid (track R, stage R1, decision D6): rows and columns, sort, filter, copy, open and export.</summary>
public sealed class PowerShellGridViewModelTests
{
    private const string Jan = "CN=Jan Kowalski,OU=Firma,DC=contoso,DC=test";
    private const string Lukasz = "CN=Łukasz Żółć,OU=Firma,DC=contoso,DC=test";
    private const string Ola = "CN=Ola Nowak,OU=IT,DC=contoso,DC=test";

    private static PowerShellGridTable Users(string? title = "Users") => new(
        ["DistinguishedName", "Name", "Count", "mail"],
        [
            [Jan, "Jan Kowalski", "10", "jan@contoso.test"],
            [Lukasz, "Łukasz Żółć", "9", null],
            [Ola, "Ola Nowak", "2", "=HYPERLINK(\"http://x\")"],
            ["CN=Zed,OU=IT,DC=contoso,DC=test", "Zed", null, "zed@contoso.test"],
            ["CN=Ala,OU=IT,DC=contoso,DC=test", "Ala", "100", "ala@contoso.test"],
        ],
        title,
        "DistinguishedName");

    private static PowerShellGridViewModel Create(B2Harness harness, PowerShellGridTable? table = null) =>
        new(harness.Services, harness.Engines.Csv, table ?? Users());

    private static ColumnViewModel Column(PowerShellGridViewModel grid, string header) => grid.Columns.Single(column => column.Header == header);

    private static string[] Names(PowerShellGridViewModel grid) => [.. grid.Rows.Select(row => row["c1"])];

    [Fact]
    public async Task TheRowsAndColumns_AreTheTable_AndTheCellsAreBoundByKey()
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness);

        Assert.Equal(["DistinguishedName", "Name", "Count", "mail"], grid.Columns.Select(column => column.Header).ToArray());
        Assert.Equal(["c0", "c1", "c2", "c3"], grid.Columns.Select(column => column.Attribute).ToArray());
        Assert.Equal(["Jan Kowalski", "Łukasz Żółć", "Ola Nowak", "Zed", "Ala"], Names(grid));
        Assert.Equal("jan@contoso.test", grid.Rows[0]["c3"]);
        Assert.Equal(string.Empty, grid.Rows[1]["c3"]);
        Assert.Equal(string.Empty, grid.Rows[0]["c9"]);
        Assert.Equal(string.Empty, grid.Rows[0]["nonsense"]);
        Assert.Equal(Jan, grid.Rows[0].DistinguishedName);
        Assert.True(grid.CanOpenObjects);
        Assert.Equal(5, grid.TotalRows);
        Assert.Equal("Users", grid.Title);
        Assert.Equal("Users – contoso.test", grid.WindowTitle);
    }

    [Fact]
    public async Task ATableWithoutADistinguishedNameColumn_CannotBeOpened_AndTheTitleHasADefault()
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness, new PowerShellGridTable(["Value"], [["1"], ["2"]]));

        Assert.False(grid.CanOpenObjects);
        Assert.All(grid.Rows, row => Assert.Null(row.DistinguishedName));
        Assert.Equal("Results", grid.Title);
        grid.SetSelection(grid.Rows);
        Assert.False(grid.OpenCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("en-US", "5 rows · 4 columns")]
    [InlineData("pl-PL", "5 wierszy · 4 kolumny")]
    public async Task TheSummaryIsTheStatusLinesIdleText_InBothLanguages(string culture, string expected)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var grid = Create(harness);

        Assert.Equal(PageStatusKind.Idle, grid.Status.Current.Kind);
        Assert.Equal(expected, grid.Status.Current.Message);
    }

    [Fact]
    public async Task AClickOnAColumn_SortsNumbersAsNumbers_ThenTheOtherWay_AndRowsWithoutAValueGoLast()
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness);
        var count = Column(grid, "Count");

        await grid.SortCommand.ExecuteAsync(count);
        Assert.Equal(["Ola Nowak", "Łukasz Żółć", "Jan Kowalski", "Ala", "Zed"], Names(grid));
        Assert.Equal(SortDirection.Ascending, count.SortDirection);

        await grid.SortCommand.ExecuteAsync(count);
        Assert.Equal(["Ala", "Jan Kowalski", "Łukasz Żółć", "Ola Nowak", "Zed"], Names(grid));
        Assert.Equal(SortDirection.Descending, count.SortDirection);

        await grid.SortCommand.ExecuteAsync(Column(grid, "Name"));
        Assert.Equal(SortDirection.None, count.SortDirection);
        Assert.Equal(SortDirection.Ascending, Column(grid, "Name").SortDirection);
    }

    [Fact]
    public async Task Text_IsSortedInTheOrderOfTheLanguage()
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo("pl-PL"));
        var table = new PowerShellGridTable(["Name"], [["Zofia"], ["Łukasz"], ["Lena"], ["Maria"], ["Adam"]]);
        using var grid = Create(harness, table);

        await grid.SortCommand.ExecuteAsync(grid.Columns[0]);

        Assert.Equal(["Adam", "Lena", "Łukasz", "Maria", "Zofia"], grid.Rows.Select(row => row["c0"]).ToArray());
    }

    [Fact]
    public async Task TheFilter_IgnoresCaseAndDiacritics_SearchesEveryColumn_AndSaysHowManyAreShown()
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness);

        grid.FilterText = "zolc";
        await WaitForAsync(grid, () => grid.Rows.Count == 1);
        Assert.Equal(["Łukasz Żółć"], Names(grid));
        Assert.Equal("1 of 5 rows shown · 4 columns", grid.Status.Current.Message);

        grid.FilterText = "OU=IT";
        await WaitForAsync(grid, () => grid.Rows.Count == 3);
        Assert.Equal(["Ola Nowak", "Zed", "Ala"], Names(grid));

        grid.ClearFilterCommand.Execute(null);
        await WaitForAsync(grid, () => grid.Rows.Count == 5);
        Assert.Equal("5 rows · 4 columns", grid.Status.Current.Message);
    }

    [Fact]
    public async Task TheFilterAndTheSort_WorkTogether()
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness);

        await grid.SortCommand.ExecuteAsync(Column(grid, "Name"));
        grid.FilterText = "contoso";
        await WaitForAsync(grid, () => grid.Rows.Count == 5);

        Assert.Equal(["Ala", "Jan Kowalski", "Łukasz Żółć", "Ola Nowak", "Zed"], Names(grid));
    }

    [Fact]
    public async Task TheSelection_IsCopiedAsTabSeparatedText_WithoutAndWithTheColumnNames_AndAFormulaIsProtected()
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness);
        grid.SetSelection(new[] { grid.Rows[0], grid.Rows[2] });

        grid.CopyRowsCommand.Execute(null);
        var plain = harness.Clipboard.Text;
        grid.CopyRowsWithHeadersCommand.Execute(null);
        var withHeaders = harness.Clipboard.Text;

        Assert.Equal($"{Jan}\tJan Kowalski\t10\tjan@contoso.test{Environment.NewLine}{Ola}\tOla Nowak\t2\t'=HYPERLINK(\"http://x\")", plain);
        Assert.StartsWith($"DistinguishedName\tName\tCount\tmail{Environment.NewLine}{Jan}", withHeaders, StringComparison.Ordinal);
        Assert.Contains(harness.Notifications.Items, item => item.Message.Contains('2', StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopyingDistinguishedNames_CopiesOnePerLine()
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness);
        grid.SetSelection(new[] { grid.Rows[0], grid.Rows[1] });

        grid.CopyDnCommand.Execute(null);

        Assert.Equal($"{Jan}{Environment.NewLine}{Lukasz}", harness.Clipboard.Text);
    }

    [Fact]
    public async Task Opening_ShowsThePropertiesOfTheSelectedObjects_AtMostTenAndEachOnce()
    {
        var harness = await B2Harness.CreateAsync();
        var rows = Enumerable.Range(0, 14).Select(i => (IReadOnlyList<string?>)[$"CN=User{i},DC=contoso,DC=test", $"User{i}"]).ToList();
        rows.Add(["CN=User0,DC=contoso,DC=test", "again"]);
        using var grid = Create(harness, new PowerShellGridTable(["DistinguishedName", "Name"], rows, "Users", "DistinguishedName"));
        Assert.False(grid.OpenCommand.CanExecute(null));

        grid.SetSelection(grid.Rows);
        Assert.True(grid.OpenCommand.CanExecute(null));
        await grid.OpenCommand.ExecuteAsync(null);

        Assert.Equal(10, harness.Dialogs.PropertiesShown.Count);
        Assert.Equal(10, harness.Dialogs.PropertiesShown.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task WhenTheSessionOfTheWindowEnds_TheGridKeepsItsRows_ButNothingCanBeOpened_AndTheLineSaysSo()
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness);
        grid.SetSelection(new[] { grid.Rows[0] });
        Assert.True(grid.OpenCommand.CanExecute(null));

        await harness.Sessions.DisconnectAsync();

        Assert.True(grid.IsSessionEnded);
        Assert.False(grid.OpenCommand.CanExecute(null));
        Assert.Equal(5, grid.Rows.Count);
        Assert.Equal(PageStatusKind.Error, grid.Status.Current.Kind);
        Assert.Equal(grid.SessionEndedMessage, grid.Status.Current.Message);
    }

    [Fact]
    public async Task Export_WritesTheRowsAsShown_WithFormulasProtected_ToTheFileTheOperatorChose()
    {
        var harness = await B2Harness.CreateAsync(regional: CultureInfo.GetCultureInfo("pl-PL"));
        using var grid = Create(harness);
        using var folder = new TempFolder();
        harness.Files.NextSavePath = Path.Combine(folder.Path, "wyniki.csv");
        grid.FilterText = "OU=IT";
        await WaitForAsync(grid, () => grid.Rows.Count == 3);
        await grid.SortCommand.ExecuteAsync(Column(grid, "Name"));

        await grid.ExportCommand.ExecuteAsync(null);

        var lines = (await File.ReadAllLinesAsync(harness.Files.NextSavePath, TestContext.Current.CancellationToken));
        Assert.Equal(4, lines.Length);
        Assert.Equal("DistinguishedName;Name;Count;mail", lines[0]);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, (await File.ReadAllBytesAsync(harness.Files.NextSavePath, TestContext.Current.CancellationToken)).Take(3).ToArray());
        Assert.Contains("Ala", lines[1], StringComparison.Ordinal);
        Assert.Contains("'=HYPERLINK", lines[2], StringComparison.Ordinal);
        Assert.DoesNotContain("Jan", string.Concat(lines), StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Done, grid.Status.Current.Kind);
        Assert.Contains("wyniki.csv", grid.Status.Current.Message, StringComparison.Ordinal);
        Assert.StartsWith("Users", harness.Files.LastDefaultFileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_WritesNothing_WhenSaveAsIsCancelled_AndSaysSoWhenTheFileCannotBeWritten()
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness);
        harness.Files.NextSavePath = null;

        await grid.ExportCommand.ExecuteAsync(null);
        Assert.Equal(PageStatusKind.Idle, grid.Status.Current.Kind);

        using var folder = new TempFolder();
        harness.Files.NextSavePath = Path.Combine(folder.Path, "no-such-folder", "x.csv");
        await grid.ExportCommand.ExecuteAsync(null);

        Assert.Equal(PageStatusKind.Error, grid.Status.Current.Kind);
        Assert.DoesNotContain("Jan", grid.Status.Current.Log.ToString(), StringComparison.Ordinal);
    }

    private static PowerShellGridTable WithSecrets() => new(
        ["DistinguishedName", "Name", "ms-Mcs-AdmPwd", "msFVE-RecoveryPassword"],
        [["CN=LAPS01,OU=Komputery,DC=contoso,DC=test", "LAPS01", "Cleartext!Laps1", "111111-222222-333333-444444-555555-666666-777777-888888"]],
        "Computers",
        "DistinguishedName");

    /// <summary>
    /// Audit ps-commands-4: the grid copied the clear-text LAPS password and the BitLocker recovery password with the rest; the
    /// program's list leaves such columns out of a copy (the Attribute Editor's explicit reveal is the way to copy a secret).
    /// </summary>
    [Fact]
    public async Task ACopy_LeavesOutTheColumnsWithSecrets_AndSaysWhichOnes()
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness, WithSecrets());
        grid.SetSelection(grid.Rows);

        grid.CopyRowsWithHeadersCommand.Execute(null);

        Assert.Equal($"DistinguishedName\tName{Environment.NewLine}CN=LAPS01,OU=Komputery,DC=contoso,DC=test\tLAPS01", harness.Clipboard.Text);
        Assert.Contains(harness.Notifications.Items, item => item.Message.Contains("ms-Mcs-AdmPwd", StringComparison.Ordinal) && item.Message.Contains("msFVE-RecoveryPassword", StringComparison.Ordinal));
    }

    /// <summary>Audit ps-commands-4: the grid exported the secrets without the question the program's list export asks.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExport_AsksFirstAboutTheColumnsWithSecrets_AndLeavesThemOutUnlessTheAnswerIsYes(bool include)
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness, WithSecrets());
        using var folder = new TempFolder();
        harness.Files.NextSavePath = Path.Combine(folder.Path, "laps.csv");
        harness.Dialogs.ConfirmAnswers.Enqueue(include);

        await grid.ExportCommand.ExecuteAsync(null);

        var confirmation = Assert.Single(harness.Dialogs.Shown, shown => shown.StartsWith("confirm:", StringComparison.Ordinal));
        Assert.Contains("ms-Mcs-AdmPwd", confirmation, StringComparison.Ordinal);
        var text = await File.ReadAllTextAsync(harness.Files.NextSavePath, TestContext.Current.CancellationToken);
        Assert.Equal(include, text.Contains("Cleartext!Laps1", StringComparison.Ordinal));
        Assert.Equal(include, text.Contains("msFVE-RecoveryPassword", StringComparison.Ordinal));
        Assert.Contains("LAPS01", text, StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Done, grid.Status.Current.Kind);
    }

    [Fact]
    public async Task AnExportOfATableWithoutSecrets_AsksNothing()
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness);
        using var folder = new TempFolder();
        harness.Files.NextSavePath = Path.Combine(folder.Path, "users.csv");

        await grid.ExportCommand.ExecuteAsync(null);

        Assert.DoesNotContain(harness.Dialogs.Shown, shown => shown.StartsWith("confirm:", StringComparison.Ordinal));
        Assert.True(File.Exists(harness.Files.NextSavePath));
    }

    /// <summary>
    /// Audit TI-3: a stopped export ended with the lasting line "Stopping…" - the text of the stop in progress - and said neither that it
    /// was stopped nor that a part of the file may be left. It says so in its own words, and the part written is deleted.
    /// </summary>
    [Theory]
    [InlineData("en-US", "The export was stopped: users.csv was not written (the part already written was deleted).")]
    [InlineData("pl-PL", "Eksport zatrzymano: plik users.csv nie został zapisany (zapisaną już część usunięto).")]
    public async Task AStoppedExport_SaysSoInItsOwnWords_AndDeletesThePartOfTheFileItWrote(string culture, string expected)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var codec = new HeldCsvCodec(harness.Engines.Csv);
        using var grid = new PowerShellGridViewModel(harness.Services, codec, Users());
        using var folder = new TempFolder();
        harness.Files.NextSavePath = Path.Combine(folder.Path, "users.csv");

        var export = grid.ExportCommand.ExecuteAsync(null);
        await codec.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(harness.Files.NextSavePath));
        grid.Status.CancelCommand.Execute(null);
        codec.Release.SetResult();
        await export.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(harness.Files.NextSavePath));
        Assert.Equal(PageStatusKind.Attention, grid.Status.Current.Kind);
        Assert.Equal(expected, grid.Status.Current.Message);
    }

    /// <summary>A codec that writes the header, then waits until the test lets it go on with the rows (an export the test can stop midway).</summary>
    private sealed class HeldCsvCodec(AdMng.Core.Csv.ICsvCodec inner) : AdMng.Core.Csv.ICsvCodec
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AdMng.Core.Csv.CsvTable Read(TextReader reader, char? delimiter = null) => inner.Read(reader, delimiter);

        public void Write(TextWriter writer, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, char delimiter = ';', bool protectFormulas = true)
        {
            writer.Write(string.Join(delimiter, headers));
            writer.Flush();
            Entered.TrySetResult();
            Release.Task.Wait(TimeSpan.FromSeconds(10));
            inner.Write(writer, headers, rows, delimiter, protectFormulas);
        }
    }

    [Fact]
    public async Task NothingOfTheRowsReachesTheLogCopyOrToString()
    {
        var harness = await B2Harness.CreateAsync();
        using var grid = Create(harness);
        grid.SetSelection(grid.Rows);
        using var folder = new TempFolder();
        harness.Files.NextSavePath = Path.Combine(folder.Path, "a.csv");
        var recorder = new StatusRecorder(grid.Status);

        await grid.ExportCommand.ExecuteAsync(null);

        Assert.DoesNotContain("Jan", grid.Rows[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Kowalski", grid.Status.Current.Log.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(recorder.Seen);
    }

    [Fact]
    public async Task ALargeTableIsFilteredAndSortedWithoutBlocking_AndANewerFilterWins()
    {
        var harness = await B2Harness.CreateAsync();
        var rows = Enumerable.Range(0, 100_000).Select(i => (IReadOnlyList<string?>)[$"CN=U{i},DC=a,DC=b", i.ToString(CultureInfo.InvariantCulture)]).ToList();
        using var grid = Create(harness, new PowerShellGridTable(["DistinguishedName", "Id"], rows, "Big", "DistinguishedName"));

        grid.FilterText = "99";
        grid.FilterText = "99999";
        await WaitForAsync(grid, () => grid.Rows.Count == 1);
        grid.ClearFilterCommand.Execute(null);
        await WaitForAsync(grid, () => grid.Rows.Count == 100_000);
        await grid.SortCommand.ExecuteAsync(grid.Columns[1]);
        await grid.SortCommand.ExecuteAsync(grid.Columns[1]);

        Assert.Equal("99999", grid.Rows[0]["c1"]);
        Assert.Equal("0", grid.Rows[^1]["c1"]);
    }

    [Fact]
    public async Task ADisposedGrid_IgnoresALateFilter()
    {
        var harness = await B2Harness.CreateAsync();
        var grid = Create(harness);

        grid.FilterText = "zolc";
        grid.Dispose();
        await Task.Delay(1, TestContext.Current.CancellationToken);

        Assert.True(grid.IsDisposed);
    }

    private static async Task WaitForAsync(PowerShellGridViewModel grid, Func<bool> condition)
    {
        await B2Harness.WaitUntilAsync(condition);
        Assert.True(condition(), $"the grid shows {grid.Rows.Count} rows");
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "admng-grid-" + Guid.NewGuid().ToString("N"));
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
                // A temp folder of a test; the operating system cleans it up.
            }
        }
    }
}
