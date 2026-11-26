using AdMng.Core.Ldap;
using AdMng.Core.PowerShell;
using AdMng.PowerShell.Commands;
using AdMng.Testing;

namespace AdMng.PowerShell.Tests.Reading;

/// <summary>Out-AdmGrid, Open-AdmObject and Export-AdmList: the three commands that call back into the window (R1 part 4).</summary>
public sealed class WindowCommandsTests
{
    private static bool SameDn(string? left, string? right) => DistinguishedName.AreEqual(left, right);

    private static List<string?> Cells(PowerShellGridTable table, string column)
    {
        var index = table.Columns.ToList().IndexOf(column);
        return [.. table.Rows.Select(row => row[index])];
    }

    /// <summary>A path that is valid on the system the test runs on (a drive letter is not a drive on Linux).</summary>
    private static string SomeFile(string name = "x.csv") => Path.Combine(Path.GetTempPath(), "admng-" + name);

    private static string SearchFilter(FakeDirectorySession directory) =>
        directory.Operations.Last(o => o.Kind == FakeOperationKind.Search).Search!.Filter;

    // ---- Out-AdmGrid -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Out_AdmGrid_hands_the_objects_to_the_window_as_plain_text_with_the_distinguished_name_first()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var result = await harness.RunAsync("Get-AdmUser -Department IT -Properties mail, department | Out-AdmGrid -Title 'IT staff'");

        Assert.True(result.Errors.Length == 0, result.Errors);
        Assert.Empty(result.Objects);
        var table = Assert.Single(services.Grids);
        Assert.Equal("IT staff", table.Title);
        Assert.Equal("DistinguishedName", table.Columns[0]);
        Assert.Equal(0, table.DistinguishedNameColumn);
        Assert.Contains("sAMAccountName", table.Columns);
        Assert.Contains("mail", table.Columns);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["jan.kowalski", "lukasz.wisniewski"], Cells(table, "sAMAccountName").Order().ToList());
        Assert.All(table.Rows, row => Assert.True(DistinguishedName.TryParse(row[0]!, out _)));
        Assert.True(services.LastToken.CanBeCanceled, "the call into the window carries the pipeline's stop token");
    }

    [Fact]
    public async Task Out_AdmGrid_values_are_text_a_grid_can_sort_and_never_a_secret()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        await harness.RunAsync(
            "New-Object PSObject -Property @{ Name = 'a'; Count = 1234567; Ratio = 1.5; When = [datetime]'2026-03-04T05:06:07'; Flag = $true; Id = [guid]'11111111-2222-3333-4444-555555555555'; " +
            "List = @('x','y'); Empty = $null; Bytes = [byte[]](1,2,3); Secret = (ConvertTo-SecureString 'p' -AsPlainText -Force) } | Out-AdmGrid");

        var table = Assert.Single(services.Grids);
        string? Cell(string column) => table.Rows.Single()[table.Columns.ToList().IndexOf(column)];
        Assert.Equal("a", Cell("Name"));
        Assert.Equal("1234567", Cell("Count"));
        Assert.Equal("1.5", Cell("Ratio"));
        Assert.Equal("2026-03-04 05:06:07", Cell("When"));
        Assert.Equal("True", Cell("Flag"));
        Assert.Equal("11111111-2222-3333-4444-555555555555", Cell("Id"));
        Assert.Equal("x; y", Cell("List"));
        Assert.Null(Cell("Empty"));
        Assert.Equal("(binary, 3 bytes)", Cell("Bytes"));
        Assert.Equal("(secure string)", Cell("Secret"));
    }

    [Fact]
    public async Task Out_AdmGrid_of_plain_values_has_one_column_Value_and_the_columns_of_mixed_objects_are_their_union()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        await harness.RunAsync("1..3 | Out-AdmGrid");
        await harness.RunAsync("(New-Object PSObject -Property @{ A = 1 }), (New-Object PSObject -Property @{ B = 2; A = 3 }) | Out-AdmGrid");
        await harness.RunAsync("Out-AdmGrid -InputObject @('x', 'y')");

        Assert.Equal(["Value"], services.Grids[0].Columns);
        Assert.Equal(new List<string?> { "1", "2", "3" }, Cells(services.Grids[0], "Value"));
        Assert.Equal(["A", "B"], services.Grids[1].Columns.Order().ToArray());
        Assert.Equal(new List<string?> { "1", "3" }, Cells(services.Grids[1], "A"));
        Assert.Equal(new List<string?> { null, "2" }, Cells(services.Grids[1], "B"));
        Assert.Equal(new List<string?> { "x", "y" }, Cells(services.Grids[2], "Value"));
        Assert.Equal(-1, services.Grids[0].DistinguishedNameColumn);
    }

    /// <summary>
    /// Audit ps-commands-3: the text of a cell recursed into every list with no limit, so one array that contains itself - one line
    /// in the default ConstrainedLanguage mode - overflowed the stack, which no handler can catch: the whole program ended.
    /// </summary>
    [Fact]
    public async Task Out_AdmGrid_of_an_array_that_contains_itself_shows_a_cell_and_the_program_goes_on()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var result = await harness.RunAsync("$a = @(1, 2); $a[0] = $a; New-Object PSObject -Property @{ X = $a } | Out-AdmGrid");

        Assert.True(result.Errors.Length == 0, result.Errors);
        var cell = Assert.Single(Cells(Assert.Single(services.Grids), "X"));
        Assert.NotNull(cell);
        Assert.EndsWith("2", cell, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_of_a_cell_looks_into_lists_three_levels_deep_and_then_names_the_list()
    {
        var inner = new object[] { "c", new object[] { "d" } };
        var nested = new object[] { "a", new object[] { "b", inner } };
        var self = new object[2];
        self[0] = self;
        self[1] = "x";

        Assert.Equal(string.Join(AdMng.Core.Formatting.AttributeFormatter.ValueSeparator, "a", "b", "c", "System.Object[]"), Tables.CellText.Of(nested));
        Assert.EndsWith("x", Tables.CellText.Of(self), StringComparison.Ordinal);
    }

    /// <summary>Audit ps-commands-6: a null element of an array ended the command with "the value of argument obj is null".</summary>
    [Fact]
    public async Task Out_AdmGrid_of_an_array_with_a_null_element_has_an_empty_row_for_it()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var result = await harness.RunAsync("Out-AdmGrid -InputObject @('x', $null, 'y')");

        Assert.True(result.Errors.Length == 0, result.Errors);
        Assert.Equal(new List<string?> { "x", null, "y" }, Cells(Assert.Single(services.Grids), "Value"));
    }

    [Fact]
    public async Task Out_AdmGrid_with_nothing_to_show_opens_no_grid_and_says_so()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var result = await harness.RunAsync("Get-AdmUser -Name 'nobody-has-this-name' | Out-AdmGrid");

        Assert.Empty(services.Grids);
        Assert.Contains("no objects", result.Warnings, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.Errors.Length);
    }

    /// <summary>
    /// Owner answer 12 (audit W-notes-out-admgrid-cap): a loop of Out-AdmGrid opened a window for every call. The program refuses a grid
    /// while ten are open; the command says so in the window's language, as an error of the call that was refused, and the pipeline goes on.
    /// </summary>
    [Theory]
    [InlineData("en-US", "10 grid windows of Out-AdmGrid are already open, the most there may be at a time: no grid was opened. Close one of them and run the command again.")]
    [InlineData("pl-PL", "Otwartych jest już 10 okien siatki Out-AdmGrid – najwięcej, ile może być naraz: nie otwarto siatki. Zamknij któreś z nich i uruchom polecenie ponownie.")]
    public async Task Out_AdmGrid_refused_by_the_program_past_its_ten_windows_says_so_and_the_pipeline_goes_on(string culture, string expected)
    {
        var services = new RecordingWindowServices { GridLimit = 10 };
        await using var harness = await ReadHarness.CreateAsync(services: services, culture: System.Globalization.CultureInfo.GetCultureInfo(culture));

        var result = await harness.RunAsync(
            "foreach ($i in 1..12) { $i | Out-AdmGrid -ErrorVariable +refused -ErrorAction SilentlyContinue }; " +
            "$refused.Count; $refused[0].FullyQualifiedErrorId.Split(',')[0]; [string]$refused[0].CategoryInfo.Category; $refused[0].Exception.Message; 'after'");

        Assert.Equal(10, services.Grids.Count);
        Assert.Equal(["2", "AdmTooManyGrids", "LimitsExceeded", expected, "after"], result.Objects.Select(o => o.ToString()).ToArray());
    }

    [Fact]
    public async Task The_window_commands_say_so_when_the_window_has_no_services_or_no_window()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var grid = await harness.RunAsync("1 | Out-AdmGrid");
        var export = await harness.RunAsync($"1 | Export-AdmList -Path '{SomeFile()}'");
        var open = await harness.RunAsync("Open-AdmObject -Identity 'jan.kowalski'");
        await using var withoutWindow = await TestSessions.CreateAsync(withoutWindow: true);
        var noWindow = TestSessions.Text(await TestSessions.RunAsync(withoutWindow, "1 | Out-AdmGrid"), PowerShellStream.Error);

        Assert.Contains("program did not give it the means", grid.Errors, StringComparison.Ordinal);
        Assert.Contains("program did not give it the means", export.Errors, StringComparison.Ordinal);
        Assert.Contains("program did not give it the means", open.Errors, StringComparison.Ordinal);
        Assert.Contains("only in an AD Manager PowerShell window", noWindow, StringComparison.Ordinal);
    }

    // ---- Open-AdmObject ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Open_AdmObject_opens_the_object_by_every_kind_of_identity()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);
        var jan = harness.Company.JanKowalski;
        var guid = harness.Directory.GetValues(jan, "objectGUID").Single();
        var sid = harness.Directory.GetValues(jan, "objectSid").Single();

        foreach (var identity in new[] { "jan.kowalski", "jan.kowalski@contoso.test", jan, guid, $"<GUID={guid}>", sid })
        {
            services.Opened.Clear();
            var result = await harness.RunAsync($"Open-AdmObject -Identity '{identity.Replace("'", "''", StringComparison.Ordinal)}'");

            Assert.True(result.Errors.Length == 0, $"{identity}: {result.Errors}");
            Assert.True(SameDn(jan, Assert.Single(services.Opened)), identity);
        }
    }

    [Fact]
    public async Task Open_AdmObject_takes_the_distinguished_names_of_objects_from_the_pipeline()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var result = await harness.RunAsync("Get-AdmUser -Department IT | Open-AdmObject");

        Assert.True(result.Errors.Length == 0, result.Errors);
        Assert.Equal(2, services.Opened.Count);
        Assert.Contains(services.Opened, dn => SameDn(dn, harness.Company.JanKowalski));
    }

    [Fact]
    public async Task Open_AdmObject_of_a_missing_object_is_an_error_that_names_it_and_opens_nothing()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var result = await harness.RunAsync("Open-AdmObject -Identity 'nobody.here', 'CN=Gone,OU=Nowhere,DC=contoso,DC=test'");

        Assert.Empty(services.Opened);
        Assert.Contains("nobody.here", result.Errors, StringComparison.Ordinal);
        Assert.Contains("CN=Gone,OU=Nowhere,DC=contoso,DC=test", result.Errors, StringComparison.Ordinal);
        Assert.Contains("Cannot find", result.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_AdmObject_says_so_when_the_program_could_not_open_it()
    {
        var services = new RecordingWindowServices { OpenResult = false };
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var result = await harness.RunAsync("Open-AdmObject -Identity 'jan.kowalski'");

        Assert.Contains("could not open", result.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_AdmObject_opens_at_most_ten_windows_and_says_so_once()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var result = await harness.RunAsync("Get-AdmUser -Properties * | Select-Object -First 25 | Open-AdmObject");

        Assert.Equal(10, services.Opened.Count);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(result.Errors, "at most 10 windows"));
    }

    [Fact]
    public async Task Open_AdmObject_escapes_what_it_is_given_into_its_filter()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        await harness.RunAsync("Open-AdmObject -Identity 'a)(b*c\\'");

        Assert.Empty(services.Opened);
        Assert.Contains(@"a\29\28b\2ac\5c", SearchFilter(harness.Directory), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_AdmObject_reads_only_the_name_of_the_object_and_nothing_is_written()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);
        harness.Directory.ClearOperations();

        await harness.RunAsync("Open-AdmObject -Identity 'jan.kowalski'");

        Assert.All(harness.Directory.Operations, operation => Assert.Equal(FakeOperationKind.Search, operation.Kind));
        Assert.Equal(["name"], harness.Directory.Operations.Last().Search!.Attributes, StringComparer.OrdinalIgnoreCase);
    }

    // ---- Export-AdmList ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_AdmList_with_a_path_asks_the_window_to_write_exactly_that_file()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);
        var file = Path.Combine(Path.GetTempPath(), "admng-export-" + Guid.NewGuid().ToString("N") + ".csv");

        var result = await harness.RunAsync($"Get-AdmUser -Department IT | Export-AdmList -Path '{file}' -Delimiter ','");

        Assert.True(result.Errors.Length == 0, result.Errors);
        var export = Assert.Single(services.Exports);
        Assert.Equal(file, export.Path);
        Assert.Equal(',', export.Delimiter);
        Assert.Equal(2, export.Table.Rows.Count);
        Assert.Equal("DistinguishedName", export.Table.Columns[0]);
        var returned = result.Single();
        Assert.Equal(file, returned.Properties["Path"].Value);
        Assert.Equal(2, returned.Properties["Rows"].Value);
        Assert.False(File.Exists(file), "the command itself writes nothing; the window does");
    }

    [Fact]
    public async Task Export_AdmList_takes_the_path_literally_and_resolves_a_relative_one_against_the_location()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);
        var folder = Path.Combine(Path.GetTempPath(), "admng-[brackets]-" + Guid.NewGuid().ToString("N"));

        await harness.RunAsync($"1 | Export-AdmList -Path '{folder}'");
        await harness.RunAsync("1 | Export-AdmList -Path 'relative.csv'");

        Assert.Equal(folder, services.Exports[0].Path);
        Assert.True(Path.IsPathRooted(services.Exports[1].Path!));
        Assert.EndsWith("relative.csv", services.Exports[1].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_AdmList_without_a_path_asks_in_Save_As_and_writes_nothing_when_nobody_chose_a_file()
    {
        var services = new RecordingWindowServices { ChosenPath = null };
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var cancelled = await harness.RunAsync("1..3 | Export-AdmList");
        var chosenPath = SomeFile("chosen.csv");
        services.ChosenPath = chosenPath;
        var chosen = await harness.RunAsync("1..3 | Export-AdmList");

        Assert.Equal(2, services.Exports.Count);
        Assert.All(services.Exports, export => Assert.Null(export.Path));
        Assert.Empty(cancelled.Objects);
        Assert.Contains("nothing was written", cancelled.Warnings, StringComparison.Ordinal);
        Assert.Equal(chosenPath, chosen.Single().Properties["Path"].Value);
    }

    /// <summary>
    /// Audit ps-commands-10: Export-AdmList checked its path, -Force and -Delimiter only after it had read the whole pipeline - in a large
    /// domain 100,000 objects read and kept, then refused. It refuses before the command before it reads anything.
    /// </summary>
    [Theory]
    [InlineData("exists")]
    [InlineData("variable")]
    [InlineData("delimiter")]
    public async Task Export_AdmList_refuses_before_the_pipeline_is_read(string refusal)
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);
        var file = Path.Combine(Path.GetTempPath(), "admng-exists-" + Guid.NewGuid().ToString("N") + ".csv");
        await File.WriteAllTextAsync(file, "old", TestContext.Current.CancellationToken);
        try
        {
            var target = refusal switch
            {
                "exists" => $"-Path '{file}'",
                "variable" => "-Path 'Variable:\\x'",
                _ => $"-Path '{file}' -Force -Delimiter \"`n\"",
            };
            var searchesBefore = harness.Directory.Operations.Count(o => o.Kind == FakeOperationKind.Search);

            var result = await harness.RunAsync($"Get-AdmUser | Export-AdmList {target}");

            Assert.NotEqual(0, result.Errors.Length);
            Assert.Empty(services.Exports);
            Assert.Equal(searchesBefore, harness.Directory.Operations.Count(o => o.Kind == FakeOperationKind.Search));
            Assert.Equal("old", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Export_AdmList_does_not_replace_a_file_without_Force()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);
        var file = Path.Combine(Path.GetTempPath(), "admng-exists-" + Guid.NewGuid().ToString("N") + ".csv");
        await File.WriteAllTextAsync(file, "old", TestContext.Current.CancellationToken);
        try
        {
            var refused = await harness.RunAsync($"1 | Export-AdmList -Path '{file}'");
            var forced = await harness.RunAsync($"1 | Export-AdmList -Path '{file}' -Force");

            Assert.Contains("exists", refused.Errors, StringComparison.Ordinal);
            Assert.Single(services.Exports);
            Assert.Equal(file, services.Exports[0].Path);
            Assert.True(forced.Errors.Length == 0, forced.Errors);
            Assert.Equal("old", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("'\"'")]
    [InlineData("\"`n\"")]
    [InlineData("\"`0\"")]
    public async Task Export_AdmList_refuses_a_delimiter_that_is_not_one_visible_character(string delimiter)
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var result = await harness.RunAsync($"1 | Export-AdmList -Path '{SomeFile()}' -Delimiter {delimiter}");

        Assert.Empty(services.Exports);
        Assert.Contains("delimiter", result.Errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_AdmList_refuses_a_path_that_is_not_in_the_file_system()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var result = await harness.RunAsync("1 | Export-AdmList -Path 'Env:\\X'");

        Assert.Empty(services.Exports);
        Assert.Contains("not a path in the file system", result.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_AdmList_reports_a_file_the_window_could_not_write_as_an_error()
    {
        var services = new FailingExportServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var result = await harness.RunAsync($"1 | Export-AdmList -Path '{SomeFile()}'");

        Assert.Contains("denied", result.Errors, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Objects);
    }

    [Fact]
    public async Task Export_AdmList_is_a_command_for_data_and_has_no_write_to_the_directory()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);
        harness.Directory.ClearOperations();

        await harness.RunAsync($"Get-AdmUser -Department IT | Export-AdmList -Path '{SomeFile()}'");

        Assert.All(harness.Directory.Operations, operation => Assert.Equal(FakeOperationKind.Search, operation.Kind));
    }

    // ---- All three ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_three_commands_work_in_ConstrainedLanguage_which_is_the_default()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services);

        var result = await harness.RunAsync($"(Get-AdmVersion).LanguageMode; Get-AdmUser jan.kowalski | Out-AdmGrid; Open-AdmObject jan.kowalski; 1 | Export-AdmList '{SomeFile()}'");

        Assert.True(result.Errors.Length == 0, result.Errors);
        Assert.Equal("Constrained", result.Objects[0].ToString());
        Assert.Single(services.Grids);
        Assert.Single(services.Opened);
        Assert.Single(services.Exports);
    }

    [Fact]
    public async Task A_stop_cancels_the_call_into_the_window_and_ends_the_command()
    {
        var services = new RecordingWindowServices { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var harness = await ReadHarness.CreateAsync(services: services);
        using var cancel = new CancellationTokenSource();

        var stopper = Task.Run(
            async () =>
            {
                await services.Entered.Task;
                await cancel.CancelAsync();
            },
            TestContext.Current.CancellationToken);
        var items = new List<PowerShellOutputItem>();
        await foreach (var item in harness.Session.RunAsync("Get-AdmUser jan.kowalski | Out-AdmGrid", cancel.Token))
        {
            items.Add(item);
        }

        await stopper;
        Assert.Equal(PowerShellRunState.Stopped, TestSessions.End(items).State);
        Assert.Empty(services.Grids);
    }

    private sealed class FailingExportServices : IAdmWindowServices
    {
        public Task ShowGridAsync(PowerShellGridTable table, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> OpenObjectAsync(string distinguishedName, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<string?> ExportListAsync(PowerShellGridTable table, string? path, char? delimiter, CancellationToken cancellationToken) =>
            Task.FromException<string?>(new UnauthorizedAccessException("Access to the path is denied."));
    }
}
