using AdMng.Core.EventLogs;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.Localization;
using AdMng.Testing.EventLogs;

namespace AdMng.Presentation.Tests.EventLogs;

public class EventLogReaderTests
{
    private static async Task<(EventLogHarness Harness, EventLogReaderDocumentViewModel Reader)> OpenAsync(string culture = "pl-PL")
    {
        var harness = await EventLogHarness.CreateAsync(culture);
        harness.Connector.Add(EventLogSamples.LockoutScenario());
        var reader = harness.Services.CreateReader();
        await reader.OpenAsync();
        return (harness, reader);
    }

    [Fact]
    public async Task Open_ListsTheDomainControllers_PdcFirstAndMarked()
    {
        var (_, reader) = await OpenAsync();

        Assert.Equal([EventLogHarness.Dc01, EventLogHarness.Dc02], reader.Sources.Select(s => s.Host));
        Assert.True(reader.Sources[0].IsPdcEmulator);
        Assert.Equal("emulator PDC", reader.Sources[0].KindText);
        Assert.Equal("kontroler domeny", reader.Sources[1].KindText);
        Assert.All(reader.Sources, s => Assert.True(s.IsSelected));
        Assert.Equal("Dzienniki zdarzeń", reader.Title);
        Assert.True(reader.IsSupported);
        Assert.Equal("Security", reader.Channel);
        Assert.Equal(EventLogTimePreset.Last24Hours, reader.SelectedTimePreset.Value);
    }

    [Fact]
    public async Task Run_MergesTheRecordsOfEveryDc_NewestFirst_WithCoverageAndProgress()
    {
        var (harness, reader) = await OpenAsync();

        await reader.RunCommand.ExecuteAsync(null);

        Assert.Equal(7, reader.Rows.Count);
        Assert.Equal(reader.Rows.OrderByDescending(r => r.Record.TimeCreated).Select(r => r.Record.RecordId), reader.Rows.Select(r => r.Record.RecordId));
        Assert.Equal(["DC01", "DC02"], reader.Rows.Select(r => r.HostText.ToUpperInvariant()).Distinct().Order());
        Assert.All(reader.Hosts, h => Assert.Equal(EventLogHostState.Done, h.State));
        Assert.All(reader.Hosts, h => Assert.Contains("najstarsze", h.CoverageText, StringComparison.Ordinal));
        Assert.Equal("Zdarzeń: 7 z 2 źródeł", reader.StatusText);
        var query = harness.Connector.Reads[^1].Query;
        Assert.Equal("Security", query.Channel);
        Assert.Equal(harness.B2.Time.GetUtcNow().AddHours(-24), query.From);
        Assert.Equal(0, harness.Connector.OpenReaders);
        Assert.False(reader.IsLoading);
    }

    [Fact]
    public async Task Fields_MapToDataConditionsOfTheSecurityChannel_WithCaseAndAddressVariants()
    {
        var (harness, reader) = await OpenAsync();
        reader.EventIdsText = "4740, 4768-4771";
        reader.AccountText = "Jan.Kowalski";
        reader.IpText = "10.10.1.21";
        reader.WorkstationText = "waw-pc001";
        reader.WorkstationField = "Workstation";
        reader.LevelOptions.Single(l => l.Level == EventLogLevels.Information).IsSelected = true;
        reader.SelectedOutcome = reader.OutcomeOptions.Single(o => o.Value == EventAuditOutcome.Failure);

        Assert.True(reader.TryBuildQuery(out var query, out var error), error);
        Assert.Equal([4740, 4768, 4769, 4770, 4771], query!.EventIds);
        Assert.Equal([EventLogLevels.Information, EventLogLevels.LogAlways], query.Levels);
        Assert.Equal(EventAuditOutcome.Failure, query.Outcome);
        var account = Assert.Single(query.Data, d => d.Name == "TargetUserName");
        Assert.Equal(["Jan.Kowalski", "jan.kowalski", "JAN.KOWALSKI"], account.Values);
        Assert.Equal(["10.10.1.21", "::ffff:10.10.1.21"], Assert.Single(query.Data, d => d.Name == "IpAddress").Values);
        Assert.Equal(["waw-pc001", "WAW-PC001"], Assert.Single(query.Data, d => d.Name == "Workstation").Values);

        reader.Channel = "System";
        Assert.Equal(harness.B2.Localizer[L.EventLogs.FieldsOnlySecurity], reader.QueryError);
        Assert.False(string.IsNullOrEmpty(reader.FieldsNote));
    }

    [Theory]
    [InlineData("4625, x")]
    [InlineData("70000")]
    [InlineData("4771-4768")]
    [InlineData("1-500")]
    public async Task InvalidEventIds_AreRejectedWithAHint(string ids)
    {
        var (_, reader) = await OpenAsync();

        reader.EventIdsText = ids;

        Assert.StartsWith("Wpisz identyfikatory", reader.QueryError, StringComparison.Ordinal);
        await reader.RunCommand.ExecuteAsync(null);
        Assert.Empty(reader.Rows);
    }

    [Fact]
    public async Task RawXPath_IsCheckedLive_AndReplacesTheBuilder()
    {
        var (harness, reader) = await OpenAsync();
        reader.IsRawMode = true;
        reader.RawXPath = "*[System[(EventID=4740)]";

        Assert.Equal("Nawiasy nie są sparowane.", reader.RawXPathError);
        Assert.False(reader.IsRawXPathValid);

        reader.RawXPath = "*[System/EventID=4740] | *";
        Assert.NotNull(reader.RawXPathError);

        reader.RawXPath = "*[System[(EventID=4740)]]";
        Assert.Null(reader.RawXPathError);
        Assert.True(reader.IsRawXPathValid);
        Assert.Null(reader.QueryError);
        await reader.RunCommand.ExecuteAsync(null);

        var query = harness.Connector.Reads[^1].Query;
        Assert.Equal("*[System[(EventID=4740)]]", query.RawXPath);
        Assert.Null(query.From);
        Assert.DoesNotContain(reader.Hosts, h => h.HasWarning);
    }

    [Fact]
    public async Task HostFailures_ShowTheErrorWithItsRemedy_AndTheOtherDcStillAnswers()
    {
        var (harness, reader) = await OpenAsync();
        harness.Connector.FailHost(EventLogHarness.Dc02, EventLogAccessError.RpcUnavailable, 1722);
        harness.Connector.FailChannel(EventLogHarness.Dc01, "Security", EventLogAccessError.AccessDenied);

        await reader.RunCommand.ExecuteAsync(null);

        var dc01 = reader.Hosts.Single(h => h.Host == EventLogHarness.Dc01);
        var dc02 = reader.Hosts.Single(h => h.Host == EventLogHarness.Dc02);
        Assert.Equal(EventLogHostState.Failed, dc01.State);
        Assert.Contains("Czytelnicy dziennika zdarzeń", dc01.ErrorText, StringComparison.Ordinal);
        Assert.Contains("Zdalne zarządzanie dziennikiem zdarzeń", dc02.ErrorText, StringComparison.Ordinal);
        Assert.Contains("(błąd 1722)", dc02.ErrorText, StringComparison.Ordinal);
        Assert.True(reader.HasHostProblems);
        Assert.Equal("Zdarzeń: 0 z 2 źródeł, nie odczytano: 2", reader.StatusText);
    }

    [Fact]
    public async Task Coverage_WarnsWhenTheLogStartsAfterTheWindow()
    {
        var (harness, reader) = await OpenAsync();
        var now = harness.B2.Time.GetUtcNow();
        harness.Connector.SetCoverage(EventLogHarness.Dc01, "Security", new EventLogCoverage
        {
            Host = EventLogHarness.Dc01,
            Channel = "Security",
            Oldest = now.AddHours(-6),
            RecordCount = 120_000,
            FileSizeBytes = 134_217_728,
            MaximumSizeBytes = 134_217_728,
            Mode = EventLogMode.Circular,
            IsFull = true,
        });

        await reader.RunCommand.ExecuteAsync(null);

        var dc01 = reader.Hosts.Single(h => h.Host == EventLogHarness.Dc01);
        Assert.StartsWith("DC01 sięga tylko 6 godz. wstecz", dc01.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("128 MB z 128 MB", dc01.CoverageText, StringComparison.Ordinal);
        Assert.Contains("nadpisywanie w razie potrzeby", dc01.CoverageText, StringComparison.Ordinal);
        Assert.Contains("zapełniony", dc01.CoverageText, StringComparison.Ordinal);
        Assert.StartsWith("DC02 sięga tylko", reader.Hosts.Single(h => h.Host == EventLogHarness.Dc02).Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancel_StopsEverySource_AndKeepsTheWindowUsable()
    {
        var (harness, reader) = await OpenAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connector.BeforeRead = async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        var run = reader.RunCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(reader.IsLoading);
        Assert.True(reader.CancelCommand.CanExecute(null));
        reader.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(reader.IsLoading);
        Assert.StartsWith("Przerwano", reader.StatusText, StringComparison.Ordinal);
        Assert.Contains(reader.Hosts, h => h.State == EventLogHostState.Cancelled);
        await EventLogHarness.WaitAsync(() => harness.Connector.OpenReaders == 0);
        Assert.True(reader.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task MaxRecords_KeepsTheNewest_AndSaysHowManyWereDropped()
    {
        var (_, reader) = await OpenAsync();
        reader.MaxRecords = 3;

        await reader.RunCommand.ExecuteAsync(null);

        Assert.Equal(3, reader.Rows.Count);
        Assert.Equal([5_003L, 9_004L, 9_003L], reader.Rows.Select(r => r.Record.RecordId));
        Assert.Equal("Pokazano najnowsze 3 z 7 zdarzeń – reszta przekracza limit pamięci. Zawęź zapytanie.", reader.DroppedNote);
    }

    [Fact]
    public async Task Search_FiltersTheLoadedRecordsInMemory()
    {
        var (harness, reader) = await OpenAsync();
        await reader.RunCommand.ExecuteAsync(null);
        var reads = harness.Connector.Reads.Count;
        var focused = 0;
        reader.SearchFocusRequested += (_, _) => focused++;

        reader.FocusSearch();
        reader.SearchText = "zofia";
        Assert.Equal(1, focused);
        Assert.Equal(EventLogSamples.ZofiaZak, Assert.Single(reader.Rows).Record.Get("TargetUserName"));

        reader.SearchText = "nie ma takiego tekstu";
        Assert.Empty(reader.Rows);
        Assert.Equal("Żadne wczytane zdarzenie nie zawiera tego tekstu.", reader.ListNote);
        reader.SearchText = string.Empty;
        Assert.Equal(7, reader.Rows.Count);
        Assert.Equal(reads, harness.Connector.Reads.Count);
    }

    [Fact]
    public async Task Details_ExplainCodes_ResolveObjects_AndShowTheMessageOnDemand()
    {
        var (harness, reader) = await OpenAsync();
        await reader.RunCommand.ExecuteAsync(null);

        reader.SelectedRow = reader.Rows.First(r => r.Record.EventId == 4771);
        var status = reader.Fields.Single(f => f.Name == "Status");
        Assert.Equal("wstępne uwierzytelnianie nieudane (złe hasło)", status.Explanation);
        Assert.Contains(reader.Fields, f => f.IsSystem && f.Value == "4771");
        var sid = reader.Fields.Single(f => f.Name == "TargetSid");
        await EventLogHarness.WaitAsync(() => sid.HasObject);
        Assert.Equal(harness.Company.JanKowalski, sid.ObjectDn);
        Assert.Equal("Jan Kowalski", sid.ObjectName);

        await reader.OpenObjectCommand.ExecuteAsync(sid);
        Assert.Equal([harness.Company.JanKowalski], harness.B2.Dialogs.PropertiesShown);

        await reader.ShowMessageCommand.ExecuteAsync(null);
        Assert.Equal(FakeEventLogConnector.DefaultMessage(reader.SelectedRow!.Record), reader.MessageText);

        harness.Connector.MessageFormatter = _ => null;
        await reader.ShowMessageCommand.ExecuteAsync(null);
        Assert.Equal(harness.B2.Localizer[L.EventLogs.MessageUnavailable], reader.MessageText);

        reader.CopyDetailsCommand.Execute(null);
        Assert.Contains("Status: 0x18 (wstępne uwierzytelnianie nieudane (złe hasło))", harness.B2.Clipboard.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sources_ComputersAreTypedPickedOrPassedIn_AndFilesAreRead()
    {
        var (harness, reader) = await OpenAsync();
        reader.ComputerText = "waw-pc001";
        reader.AddComputerCommand.Execute(null);
        Assert.Contains("nie jest pełną nazwą DNS", reader.SourceError, StringComparison.Ordinal);

        reader.ComputerText = "waw-pc001.contoso.test";
        reader.AddComputerCommand.Execute(null);
        Assert.Null(reader.SourceError);
        Assert.Contains(reader.Sources, s => s is { Host: "waw-pc001.contoso.test", Kind: EventLogSourceKind.Computer });

        harness.B2.Dialogs.PickedObjects.Enqueue([harness.Company.Laptop]);
        await reader.PickComputersCommand.ExecuteAsync(null);
        Assert.Contains(reader.Sources, s => s.Host == "krk-lt001.contoso.test");

        var path = Path.Combine(Path.GetTempPath(), "admng-test-" + Guid.NewGuid().ToString("N") + ".evtx");
        harness.Connector.AddFile(path, [EventLogSamples.AccountLockedOut("WAW-PC001.contoso.test", 1, EventLogSamples.Morning, "jan.kowalski", "WAW-PC001")]);
        harness.B2.Files.NextOpenPath = path;
        await reader.OpenFilesCommand.ExecuteAsync(null);
        var file = Assert.Single(reader.Sources, s => s.IsFiles);
        Assert.Equal("plik .evtx", file.KindText);

        foreach (var source in reader.Sources)
        {
            source.IsSelected = source.IsFiles;
        }

        await reader.RunCommand.ExecuteAsync(null);
        Assert.Equal(4740, Assert.Single(reader.Rows).EventId);
        Assert.Equal(Path.GetFileName(path), Assert.Single(reader.Hosts).Host);
    }

    [Fact]
    public async Task ComputerRequest_SelectsOnlyThatComputer()
    {
        var (harness, reader) = await OpenAsync();

        await reader.OpenAsync(new EventLogDocumentRequest(EventLogDocumentKind.Reader) { ComputerDn = harness.Company.Computer1 });

        var selected = Assert.Single(reader.Sources, s => s.IsSelected);
        Assert.Equal("waw-pc001.contoso.test", selected.Host);
        Assert.Equal(3, reader.Sources.Count);
    }

    [Fact]
    public async Task SavedQueries_KeepTheBuilderAsTyped_AndNeverValuesReadFromALog()
    {
        var (harness, reader) = await OpenAsync();
        await reader.RunCommand.ExecuteAsync(null);
        reader.EventIdsText = "4740, 4771";
        reader.AccountText = "jan.kowalski";
        reader.Sources[1].IsSelected = false;
        reader.SavedQueryName = "Blokady Jana";

        await reader.SaveQueryCommand.ExecuteAsync(null);

        var saved = Assert.Single(harness.B2.Settings.Current.EventLogs.SavedQueries);
        Assert.Equal(("Blokady Jana", "4740, 4771", "jan.kowalski"), (saved.Name, saved.EventIds, saved.Account));
        Assert.Equal([EventLogHarness.Dc01], saved.Hosts);
        var json = Presentation.Settings.JsonSettingsStore.Serialize(harness.B2.Settings.Current);
        Assert.DoesNotContain("10.10.1.21", json, StringComparison.Ordinal);
        Assert.DoesNotContain("WAW-PC001", json, StringComparison.OrdinalIgnoreCase);

        var other = harness.Services.CreateReader();
        await other.OpenAsync();
        Assert.Equal(["Blokady Jana"], other.SavedQueries);
        other.SelectedSavedQuery = "Blokady Jana";
        other.LoadQueryCommand.Execute(null);
        Assert.Equal(("4740, 4771", "jan.kowalski"), (other.EventIdsText, other.AccountText));
        Assert.Equal([EventLogHarness.Dc01], other.Sources.Where(s => s.IsSelected).Select(s => s.Host));

        harness.B2.Dialogs.ConfirmAnswers.Enqueue(true);
        await other.DeleteQueryCommand.ExecuteAsync(null);
        Assert.Empty(harness.B2.Settings.Current.EventLogs.SavedQueries);
        Assert.Empty(other.SavedQueries);
    }

    [Fact]
    public async Task Export_WritesEscapedFiles_WithThePersonalDataNote()
    {
        var (harness, reader) = await OpenAsync();
        harness.Connector.Add(EventLogSamples.Record(EventLogHarness.Dc01, "Security", "Microsoft-Windows-Security-Auditing", 4625, 77, EventLogSamples.Morning.AddHours(1),
            EventLogLevels.LogAlways, [("TargetUserName", "=cmd|' /C calc'!A0"), ("WorkstationName", "<script>alert(1)</script>")], EventLogSamples.AuditFailureKeywords));
        await reader.RunCommand.ExecuteAsync(null);
        var csv = Path.Combine(Path.GetTempPath(), "admng-test-" + Guid.NewGuid().ToString("N") + ".csv");
        var html = Path.ChangeExtension(csv, ".html");
        try
        {
            harness.B2.Files.NextSavePath = csv;
            await reader.ExportCsvCommand.ExecuteAsync(null);
            var csvText = await File.ReadAllTextAsync(csv, TestContext.Current.CancellationToken);
            Assert.StartsWith(harness.B2.Localizer[L.EventLogs.PersonalDataNote], csvText.TrimStart('﻿').Trim('"'), StringComparison.Ordinal);
            Assert.Contains("'=cmd", csvText, StringComparison.Ordinal);
            Assert.Contains("jan.kowalski", csvText, StringComparison.Ordinal);

            harness.B2.Files.NextSavePath = html;
            await reader.ExportHtmlCommand.ExecuteAsync(null);
            var htmlText = await File.ReadAllTextAsync(html, TestContext.Current.CancellationToken);
            Assert.Contains(EventLogExport.E(harness.B2.Localizer[L.EventLogs.PersonalDataNote]), htmlText, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", htmlText, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", htmlText, StringComparison.Ordinal);
            Assert.Contains("default-src 'none'", htmlText, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(csv);
            File.Delete(html);
        }
    }

    [Fact]
    public async Task NotSupported_ShowsTheBanner_AndReadsNothing()
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.IsSupported = false;
        var reader = harness.Services.CreateReader();
        await reader.OpenAsync();

        Assert.True(reader.IsNotSupported);
        Assert.Equal(harness.B2.Localizer[L.EventLogs.NotSupported], reader.NotSupportedText);
        Assert.False(reader.RunCommand.CanExecute(null));
        await reader.RefreshAsync();
        Assert.Empty(harness.Connector.Reads);
        Assert.Empty(reader.Rows);
    }
}
