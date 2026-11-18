using System.Globalization;
using System.Text.RegularExpressions;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Testing.EventLogs;

namespace AdMng.Presentation.Tests.EventLogs;

/// <summary>
/// What the three event log documents say in their status line (track V, stage V2b-4; docs/dev/v-interface-renewal.md "V2b-4
/// as built"), pl-PL unless a test says otherwise, over the sample company with two DCs: the same words in the three documents
/// for the summary, the run with its stage and how far it is, Stop, what a run leaves (sources that were not read, a stop, a
/// failure) and the states that last (not connected, the window cannot read event logs, what a run found it cannot show) - and
/// nothing of a log in a log copy. A slow event log is a read the test holds. What the documents read and find is covered,
/// unchanged, by the other tests of this folder.
/// </summary>
public sealed partial class EventLogStatusTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static async Task<(EventLogHarness Harness, EventLogReaderDocumentViewModel Reader)> OpenReaderAsync(string culture = "pl-PL")
    {
        var harness = await EventLogHarness.CreateAsync(culture);
        harness.Connector.Add(EventLogSamples.LockoutScenario());
        var reader = harness.Services.CreateReader();
        await reader.OpenAsync();
        return (harness, reader);
    }

    /// <summary>The first read of an event log waits until the run is stopped.</summary>
    private static TaskCompletionSource HoldTheReads(EventLogHarness harness)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connector.BeforeRead = async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        return started;
    }

    private static List<string> LogsOf(PageStatusHolder status)
    {
        var logs = new List<string>();
        status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                logs.Add(status.Current.ToString());
            }
        };
        return logs;
    }

    [GeneratedRegex(@"^Odczytywanie dzienników zdarzeń( – źródła: \d z 2, zdarzenia: \d+)?…$")]
    private static partial Regex PolishReading();

    // ---- The reader --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheReader_SaysWhatToDoFirst_ThenItsRunWithTheSourcesAndEvents_AndEndsAsTheSummary()
    {
        var (harness, reader) = await OpenReaderAsync();
        Assert.IsAssignableFrom<IHasPageStatus>(reader);
        Assert.Equal((PageStatusKind.Idle, "Wybierz źródła i zapytanie, a potem kliknij Wyszukaj."), (reader.Status.Current.Kind, reader.Status.Current.Message));
        var seen = new StatusRecorder(reader.Status);
        var logs = LogsOf(reader.Status);

        await reader.RunCommand.ExecuteAsync(null);

        // The run, from the first moment, and what it is at - every message of the one operation matches, and the last
        // thing the line says is the summary that the old status text said.
        var kinds = seen.Plain.Select(s => s.Kind).Distinct().ToList();
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Idle], kinds);
        Assert.Equal("Odczytywanie dzienników zdarzeń…", seen.Plain[0].Message);
        Assert.All(seen.Plain.Where(s => s.Kind == PageStatusKind.Working), s => Assert.Matches(PolishReading(), s.Message));
        Assert.Contains(seen.Plain, s => s.Message.Contains("źródła: 0 z 2, zdarzenia: 0", StringComparison.Ordinal));
        Assert.Equal((PageStatusKind.Idle, "Zdarzeń: 7 z 2 źródeł"), seen.Plain[^1]);
        Assert.Equal(reader.StatusText, reader.Status.Current.Message);
        Assert.Contains(seen.Seen, s => s.Fraction == 0d);
        Assert.Null(reader.Status.Lasting);
        Assert.False(reader.IsSearching);

        // The log copy is a key and the counts of sources and events - not a host, an account or an event.
        Assert.Contains("Working: EventLogs.Status.Reading", logs);
        Assert.Contains(logs, log => log.StartsWith("Working: EventLogs.Status.Reading (", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, log => log.Contains("dc0", StringComparison.OrdinalIgnoreCase) || log.Contains("contoso", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Idle", logs[^1]);
    }

    [Fact]
    public async Task TheReader_SaysItsRun_InEnglish_AndTheSelectionJoinsTheSummary()
    {
        var (harness, reader) = await OpenReaderAsync("en-US");
        reader.Sources[1].IsSelected = false;
        var seen = new StatusRecorder(reader.Status);

        await reader.RunCommand.ExecuteAsync(null);

        // One source: what the line says is deterministic.
        Assert.Equal("Reading the event logs…", seen.Plain[0].Message);
        Assert.Equal((PageStatusKind.Working, "Reading the event logs – sources: 0 of 1, events: 0…"), seen.Plain[1]);
        var last = seen.Plain.Where(s => s.Kind == PageStatusKind.Working).Last();
        Assert.StartsWith("Reading the event logs – sources: 1 of 1, events: ", last.Message, StringComparison.Ordinal);
        Assert.Equal((PageStatusKind.Idle, "4 events from 1 sources"), seen.Plain[^1]);

        // The selected record is the second part of the summary, as it was in the status bar of the shell.
        reader.SelectedRow = reader.Rows[0];
        Assert.Equal($"4 events from 1 sources · {reader.Rows[0].Summary}", reader.Status.Current.Message);
        Assert.Equal(PageStatusKind.Idle, reader.Status.Current.Kind);
        harness.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
        Assert.Equal(PageStatusKind.Idle, reader.Status.Current.Kind);
    }

    [Fact]
    public async Task Stop_ReturnsControlAtOnce_WhatWasReadStays_AndTheStoppedRunIsAnAttentionWithRefresh()
    {
        var (harness, reader) = await OpenReaderAsync();
        var started = HoldTheReads(harness);
        var seen = new StatusRecorder(reader.Status);

        var run = reader.RunCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Working, with Stop: the line, the button flag and the old Cancel command are one operation.
        var working = reader.Status.Current;
        Assert.Equal(PageStatusKind.Working, working.Kind);
        var stop = Assert.IsType<PageStatusAction>(working.Action);
        Assert.Equal("Zatrzymaj", stop.Label);
        Assert.Same(reader.Status.CancelCommand, stop.Command);
        Assert.True(reader.IsSearching);
        Assert.False(reader.IsLoadingChannels);
        Assert.True(reader.CancelCommand.CanExecute(null));
        Assert.False(reader.RunCommand.CanExecute(null));

        // "Stopping…" at once; the reads that are on their way to the sources still have to return.
        stop.Command.Execute(null);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (reader.Status.Current.Kind, reader.Status.Current.Message));
        Assert.True(reader.Status.IsCancelling);
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Equal(PageStatusKind.Working, seen.Plain[0].Kind);
        Assert.Contains((PageStatusKind.Working, "Zatrzymywanie…"), seen.Plain);
        var stopped = reader.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            "Odczytywanie dzienników zdarzeń zatrzymano; odczytane i pokazane zdarzenia: 0. Wybierz „Odśwież”, aby odczytać ponownie.",
            stopped.Message);
        Assert.Equal("EventLogs.Status.StoppedPartial (0)", stopped.Log.ToString());
        Assert.True(stopped.CanClose);
        Assert.Equal("Odśwież", stopped.Action?.Label);
        Assert.Same(reader.RefreshCommand, stopped.Action?.Command);
        Assert.False(reader.IsLoading);
        Assert.False(reader.IsSearching);
        Assert.StartsWith("Przerwano", reader.StatusText, StringComparison.Ordinal);

        // Closing the result shows the summary of the run - the old text, which says the same thing again.
        reader.Status.CloseCommand.Execute(null);
        Assert.Equal((PageStatusKind.Idle, reader.LoadedText), (reader.Status.Current.Kind, reader.Status.Current.Message));

        // The action reads again.
        harness.Connector.BeforeRead = null;
        await reader.RefreshCommand.ExecuteAsync(null);
        Assert.Equal((PageStatusKind.Idle, "Zdarzeń: 7 z 2 źródeł"), (reader.Status.Current.Kind, reader.Status.Current.Message));
    }

    [Fact]
    public async Task TheCancelOfTheDocument_IsTheSameStop()
    {
        var (harness, reader) = await OpenReaderAsync();
        var started = HoldTheReads(harness);

        var run = reader.RunCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        reader.Cancel();
        Assert.Equal("Zatrzymywanie…", reader.Status.Current.Message);
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.Equal(PageStatusKind.Attention, reader.Status.Current.Kind);
        Assert.False(reader.IsLoading);
    }

    [Fact]
    public async Task SourcesThatCouldNotBeRead_AreAnAttentionThatNamesThem_AndTheReasonOfEachIsInItsRow()
    {
        var (harness, reader) = await OpenReaderAsync();
        harness.Connector.FailHost(EventLogHarness.Dc02, EventLogAccessError.RpcUnavailable, 1722);
        var logs = LogsOf(reader.Status);

        await reader.RunCommand.ExecuteAsync(null);

        // One source of two: which, and where the reason is - the row of the panel says why, with its remedy.
        var status = reader.Status.Current;
        Assert.Equal(PageStatusKind.Attention, status.Kind);
        Assert.Equal(
            "Odczytywanie dzienników zdarzeń: nie odczytano źródeł: 1 z 2 (dc02.contoso.test). Przyczyna każdego jest w sekcji „Zasięg i dostęp”.",
            status.Message);
        Assert.Equal("EventLogs.Status.NotRead (1, 2)", status.Log.ToString());
        Assert.True(status.CanClose);
        Assert.Null(status.Action);
        Assert.Contains("Zdalne zarządzanie dziennikiem zdarzeń", reader.Hosts.Single(h => h.Host == EventLogHarness.Dc02).ErrorText, StringComparison.Ordinal);
        Assert.True(reader.HasHostProblems);
        Assert.DoesNotContain(logs, log => log.Contains("dc02", StringComparison.OrdinalIgnoreCase));

        // Closed: the summary of the run says the same in its own words.
        reader.Status.CloseCommand.Execute(null);
        Assert.Equal((PageStatusKind.Idle, "Zdarzeń: 4 z 2 źródeł, nie odczytano: 1"), (reader.Status.Current.Kind, reader.Status.Current.Message));

        // Every source failed: all of them are named, and a long list is cut after three.
        harness.Connector.FailHost(EventLogHarness.Dc01, EventLogAccessError.RpcUnavailable, 1722);
        await reader.RunCommand.ExecuteAsync(null);
        Assert.Contains("nie odczytano źródeł: 2 z 2 (dc01.contoso.test, dc02.contoso.test)", reader.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadingTheChannels_IsAnOperationOfItsOwn_AndTheSummaryOfTheLastRunStays()
    {
        var (harness, reader) = await OpenReaderAsync();
        await reader.RunCommand.ExecuteAsync(null);
        var summary = reader.LoadedText;
        var seen = new StatusRecorder(reader.Status);
        var logs = LogsOf(reader.Status);

        await reader.LoadChannelsCommand.ExecuteAsync(null);

        Assert.Equal(
            [
                (PageStatusKind.Working, "Wczytywanie kanałów komputera dc01.contoso.test…"),
                (PageStatusKind.Done, "dc01.contoso.test: wczytano kanały (2)."),
            ],
            seen.Plain);
        Assert.Equal(["Working: EventLogs.Status.Channels", "Done: EvtLog.ChannelsLoaded (2)"], logs);
        Assert.Equal(summary, reader.LoadedText);

        // The result closes to the summary of the last run, not to a sentence about the channels.
        reader.Status.CloseCommand.Execute(null);
        Assert.Equal((PageStatusKind.Idle, summary), (reader.Status.Current.Kind, reader.Status.Current.Message));
        Assert.False(reader.IsLoadingChannels);
    }

    [Fact]
    public async Task TheFlagsOfTheTwoStartButtons_FollowTheRunThatWasAskedFor()
    {
        var (harness, reader) = await OpenReaderAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flags = new List<(bool Searching, bool Channels)>();
        reader.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EventLogReaderDocumentViewModel.IsSearching) or nameof(EventLogReaderDocumentViewModel.IsLoadingChannels))
            {
                flags.Add((reader.IsSearching, reader.IsLoadingChannels));
            }
        };

        await reader.LoadChannelsCommand.ExecuteAsync(null);
        Assert.Contains((false, true), flags);
        Assert.DoesNotContain((true, true), flags);
        Assert.Equal((false, false), (reader.IsSearching, reader.IsLoadingChannels));

        harness.Connector.BeforeRead = async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var run = reader.RunCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.Equal((true, false), (reader.IsSearching, reader.IsLoadingChannels));
        reader.Cancel();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.Equal((false, false), (reader.IsSearching, reader.IsLoadingChannels));
    }

    // ---- The states that last: the same in the three documents ---------------------------------------------------------------

    [Fact]
    public async Task AWindowThatCannotReadEventLogs_SaysSoAsTheStateOfEveryDocument_AndNothingIsRead()
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.IsSupported = false;
        var documents = new EventLogDocumentBase[]
        {
            harness.Services.CreateReader(),
            harness.Services.CreateFailedLogons(),
            harness.Services.CreateLockout(harness.Company.JanKowalski, "Jan Kowalski"),
        };

        foreach (var document in documents)
        {
            var state = document.Status.Current;
            Assert.Equal(PageStatusKind.Attention, state.Kind);
            Assert.Equal("Dzienniki zdarzeń można czytać tylko w AD Managerze uruchomionym w Windows.", state.Message);
            Assert.Equal("EvtLog.NotSupported", state.Log.ToString());
            Assert.False(state.CanClose);
            Assert.Null(state.Action);
            Assert.Same(document.Status.Lasting, state);
            Assert.True(document.IsNotSupported);
            Assert.Equal(string.Empty, document.LoadedText);
            await document.RefreshAsync();
            Assert.Equal(PageStatusKind.Attention, document.Status.Current.Kind);
            Assert.False(document.RefreshCommand.CanExecute(null));
            document.Dispose();
        }

        Assert.Empty(harness.Connector.Reads);
    }

    [Fact]
    public async Task ADocumentWithoutAConnection_SaysNotConnected_AndTheLanguageSwitchIsFollowed()
    {
        var harness = await EventLogHarness.CreateAsync();
        await harness.B2.Sessions.DisconnectAsync();
        using var failed = harness.Services.CreateFailedLogons();

        var state = failed.Status.Current;
        Assert.Equal((PageStatusKind.Error, "Brak połączenia z domeną."), (state.Kind, state.Message));
        Assert.False(state.CanClose);
        Assert.Null(state.Action);
        Assert.Equal(L.Errors.NotConnected, state.Log.Key);
        Assert.False(failed.RunCommand.CanExecute(null));
        await failed.RunAsync();
        Assert.Equal("Brak połączenia z domeną.", failed.Error);

        harness.B2.Localizer.SetCulture(English);
        Assert.Equal("Not connected to a domain.", failed.Status.Current.Message);
        Assert.Equal(PageStatusKind.Error, failed.Status.Current.Kind);
    }

    // ---- The failed logons ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FailedLogons_SaysItsStages_AndEndsAsTheSummaryOfTheFindings()
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.Add(EventLogSamples.FailedLogonScenario());
        var document = harness.Services.CreateFailedLogons();
        document.EnumerationThreshold = 3;
        var neverRun = document.Status.Current;
        Assert.Equal((PageStatusKind.Idle, harness.B2.Localizer[L.EventLogsFailedLogons.NeverRun]), (neverRun.Kind, neverRun.Message));
        var seen = new StatusRecorder(document.Status);
        var logs = LogsOf(document.Status);

        await document.RunAsync();

        // The stages, in order, each one message of the one operation; the details of a stage are numbers.
        var stages = new List<string>();
        foreach (var said in seen.Plain.Where(s => s.Kind == PageStatusKind.Working))
        {
            var stage = said.Message switch
            {
                "Sprawdzanie nieudanych logowań…" => "start",
                var m when m.EndsWith("– odczyt kontrolerów domeny…", StringComparison.Ordinal) => "domain controllers",
                var m when m.Contains("– odczyt nieudanych logowań, źródła: ", StringComparison.Ordinal) => "failures",
                var m when m.Contains("– odczyt udanych logowań, konta: ", StringComparison.Ordinal) => "successes",
                var m when m.EndsWith("– analiza zdarzeń…", StringComparison.Ordinal) => "analysis",
                var m => $"unexpected: {m}",
            };
            if (stages.Count == 0 || stages[^1] != stage)
            {
                stages.Add(stage);
            }
        }

        Assert.Equal(["start", "domain controllers", "failures", "successes", "analysis"], stages);
        Assert.Equal(PageStatusKind.Idle, seen.Plain[^1].Kind);
        Assert.StartsWith(document.SummaryText, seen.Plain[^1].Message, StringComparison.Ordinal);
        Assert.StartsWith("Niepowodzenia: ", seen.Plain[^1].Message, StringComparison.Ordinal);
        Assert.Null(document.Status.Lasting);
        Assert.DoesNotContain(logs, log => log.Contains("dc0", StringComparison.OrdinalIgnoreCase) || log.Contains("zak", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, log => log.StartsWith("Working: EventLogs.Status.FailedLogons", StringComparison.Ordinal));

        // The selected finding joins the summary.
        document.SelectedFinding = document.Findings[0];
        Assert.Equal($"{document.SummaryText} · {document.Findings[0].Title}", document.Status.Current.Message);
    }

    [Fact]
    public async Task FailedLogons_Stop_LeavesTheResultsAsTheyWere_AndSaysSo()
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.Add(EventLogSamples.FailedLogonScenario());
        var document = harness.Services.CreateFailedLogons();
        var started = HoldTheReads(harness);
        var seen = new StatusRecorder(document.Status);

        var run = document.RunAsync();
        await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.Equal(PageStatusKind.Working, document.Status.Current.Kind);
        document.Status.CancelCommand.Execute(null);
        Assert.Equal("Zatrzymywanie…", document.Status.Current.Message);
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        var stopped = document.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal(
            "Sprawdzanie nieudanych logowań zatrzymano: pokazanych wyników nie zmieniono. Wybierz „Odśwież”, aby odczytać ponownie.",
            stopped.Message);
        Assert.Equal("EventLogs.Status.Stopped", stopped.Log.ToString());
        Assert.Same(document.RefreshCommand, stopped.Action?.Command);
        Assert.False(document.HasReport);
        Assert.False(document.IsLoading);

        // The action checks again.
        harness.Connector.BeforeRead = null;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)stopped.Action!.Command).ExecuteAsync(null);
        Assert.True(document.HasReport);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.Contains(seen.Plain, s => s.Kind == PageStatusKind.Attention);
    }

    [Fact]
    public async Task FailedLogons_AnActionThatFails_IsAnErrorThatCanBeClosed_ButNeverTakesTheLineOfARun()
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.Add(EventLogSamples.FailedLogonScenario());
        var document = harness.Services.CreateFailedLogons();
        document.EnumerationThreshold = 3;
        await document.RunAsync();
        var row = document.AuditRows[0];
        harness.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
        var logs = LogsOf(document.Status);

        await document.ShowInModelCommand.ExecuteAsync(row);

        var failure = document.Status.Current;
        Assert.Equal(PageStatusKind.Error, failure.Kind);
        Assert.StartsWith($"Nie udało się otworzyć modelu komputera {row.Host}: ", failure.Message, StringComparison.Ordinal);
        Assert.EndsWith("(ServerUnavailable)", failure.Message, StringComparison.Ordinal);
        Assert.True(failure.CanClose);
        Assert.Equal("EventLogs.Status.ModelFailed [ServerUnavailable]", failure.Log.ToString());
        Assert.DoesNotContain(logs, log => log.Contains(row.Host, StringComparison.OrdinalIgnoreCase));
        document.Status.CloseCommand.Execute(null);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
    }

    [Fact]
    public async Task ARunThatThrowsWhatNobodyExpected_EndsAsTheErrorOfTheRun_NeverAsALineThatWorksForGood()
    {
        // An exception outside the list the run knew (the directory, the event log): the operation was completed only after
        // the try, so the method left with it open - "working" with a live Stop for a run that no longer existed, the start
        // button still the Cancel of that run, and the taskbar following the line.
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.Add(EventLogSamples.LockoutScenario());
        var document = harness.Services.CreateLockout(harness.Company.JanKowalski, "Jan Kowalski");
        harness.Session.BeforeOperation = _ => throw new FormatException("a record that cannot be parsed");

        await document.RunAsync();

        Assert.False(document.IsLoading);
        Assert.False(document.Status.IsWorking);
        Assert.Null(document.Status.Operation);
        var line = document.Status.Current;
        Assert.Equal(PageStatusKind.Error, line.Kind);
        Assert.Same(document.RefreshCommand, line.Action!.Command);

        // Retry works once the cause is gone.
        harness.Session.BeforeOperation = null;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)line.Action.Command).ExecuteAsync(null);
        Assert.NotEqual(PageStatusKind.Error, document.Status.Current.Kind);
        Assert.False(document.Status.IsWorking);
    }

    // ---- The lockout investigation ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lockout_SaysItsStagesWithTheAccount_AndEndsAsTheSummary()
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.Add(EventLogSamples.LockoutScenario());
        var document = harness.Services.CreateLockout(harness.Company.JanKowalski, "Jan Kowalski");
        var seen = new StatusRecorder(document.Status);

        await document.RunAsync();

        var stages = new List<string>();
        foreach (var said in seen.Plain.Where(s => s.Kind == PageStatusKind.Working))
        {
            var stage = said.Message switch
            {
                "Badanie blokady konta „Jan Kowalski”…" => "start",
                var m when m.EndsWith("– odczyt konta…", StringComparison.Ordinal) => "account",
                var m when m.EndsWith("– odczyt kontrolerów domeny…", StringComparison.Ordinal) => "domain controllers",
                var m when m.Contains("– odczyt dzienników, źródła: ", StringComparison.Ordinal) => "logs",
                var m when m.EndsWith("– analiza zdarzeń…", StringComparison.Ordinal) => "analysis",
                var m => $"unexpected: {m}",
            };
            if (stages.Count == 0 || stages[^1] != stage)
            {
                stages.Add(stage);
            }
        }

        Assert.Equal(["start", "account", "domain controllers", "logs", "analysis"], stages);
        Assert.StartsWith("Badanie blokady konta „Jan Kowalski” – ", seen.Plain.First(s => s.Message.Contains('–', StringComparison.Ordinal)).Message, StringComparison.Ordinal);
        Assert.Equal(PageStatusKind.Idle, seen.Plain[^1].Kind);
        Assert.StartsWith(document.LoadedText, seen.Plain[^1].Message, StringComparison.Ordinal);
        Assert.Equal(document.Status.Current.Message, seen.Plain[^1].Message);
        Assert.Null(document.Status.Lasting);
    }

    [Fact]
    public async Task Lockout_AnAccountThatIsNoAccount_IsTheStateOfTheTab_UntilTheNextRun()
    {
        var harness = await EventLogHarness.CreateAsync();
        var document = harness.Services.CreateLockout(harness.Company.Computer1, null);
        var logs = LogsOf(document.Status);

        await document.RunAsync();

        // The tab shows nothing and this is why: an error that cannot be closed, without an action to offer.
        var state = document.Status.Current;
        Assert.Equal((PageStatusKind.Error, "Ten obiekt nie jest kontem użytkownika."), (state.Kind, state.Message));
        Assert.False(state.CanClose);
        Assert.Null(state.Action);
        Assert.Equal("EvtLock.NotAUser", state.Log.Key);
        Assert.Same(document.Status.Lasting, state);
        Assert.Equal("Ten obiekt nie jest kontem użytkownika.", document.Error);
        Assert.False(document.IsLoading);
        Assert.DoesNotContain(logs, log => log.Contains(harness.Company.Computer1, StringComparison.OrdinalIgnoreCase));

        // A new run begins - the state is covered while it runs and is back when it ends.
        var again = new StatusRecorder(document.Status);
        await document.RunAsync();
        Assert.Equal(PageStatusKind.Working, again.Plain[0].Kind);
        Assert.Equal((PageStatusKind.Error, "Ten obiekt nie jest kontem użytkownika."), again.Plain[^1]);
    }

    [Fact]
    public async Task Lockout_AFailedRead_IsAnErrorWithRetry_WhichRunsItAgain()
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.Add(EventLogSamples.LockoutScenario());
        var document = harness.Services.CreateLockout(harness.Company.JanKowalski, "Jan Kowalski");
        harness.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer");
        var seen = new StatusRecorder(document.Status);
        var logs = LogsOf(document.Status);

        await document.RunAsync();

        var failure = document.Status.Current;
        Assert.Equal(PageStatusKind.Error, failure.Kind);
        Assert.NotNull(document.Error);
        Assert.Equal(
            $"Nie udało się zbadać blokady konta „Jan Kowalski”: {document.Error} Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)",
            failure.Message);
        Assert.Equal("EventLogs.Status.LockoutFailed [ServerUnavailable]", failure.Log.ToString());
        Assert.True(failure.CanClose);
        Assert.Equal("Ponów", failure.Action?.Label);
        Assert.Same(document.RefreshCommand, failure.Action?.Command);
        Assert.False(document.IsLoading);
        Assert.False(document.HasReport);
        Assert.Contains((PageStatusKind.Working, "Badanie blokady konta „Jan Kowalski” – odczyt konta…"), seen.Plain);
        Assert.DoesNotContain(logs, log => log.Contains("Kowalski", StringComparison.OrdinalIgnoreCase));

        // The server answers again: Retry investigates, the failure is gone and the summary is back.
        harness.Session.BeforeOperation = null;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)failure.Action!.Command).ExecuteAsync(null);
        Assert.Null(document.Error);
        Assert.True(document.HasReport);
        Assert.Equal(PageStatusKind.Idle, document.Status.Current.Kind);
        Assert.StartsWith(document.LoadedText, document.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingATab_DuringARun_EndsTheOperation()
    {
        var harness = await EventLogHarness.CreateAsync();
        harness.Connector.Add(EventLogSamples.FailedLogonScenario());
        var document = harness.Services.CreateFailedLogons();
        var started = HoldTheReads(harness);

        var run = document.RunAsync();
        await started.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        document.Dispose();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);

        Assert.False(document.Status.IsWorking);
        Assert.Null(document.Status.Operation);
    }
}
