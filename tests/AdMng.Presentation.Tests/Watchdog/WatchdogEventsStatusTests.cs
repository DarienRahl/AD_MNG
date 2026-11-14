using System.Collections;
using System.Globalization;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Presentation.Watchdog;
using AdMng.Testing;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Testing.Client;
using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// What the event feed of the Watchdog tab says in its status line (stage V2b-4, unit 4): the read with Stop, a read that was
/// stopped or failed (the feed is empty and paused: the state of the page), older events, the acknowledgement with the part
/// done, and the details pane's actions - with log copies that are keys, counts and a result code, never an event.
/// </summary>
public sealed class WatchdogEventsStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task TheRead_SaysWorkingWithStop_ThenTheSummary_AndTheSelectionJoinsIt()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var feed = tab.Events;
            var recorder = new StatusRecorder(feed.Status);
            h.Client.Hold(nameof(IWatchdogClient.GetEventsAsync));

            var reading = feed.LoadAsync();
            Assert.Equal(PageStatusKind.Working, feed.Status.Current.Kind);
            Assert.Equal("Reading the events…", feed.Status.Current.Message);
            Assert.Equal("Stop", feed.Status.Current.Action!.Label);
            Assert.True(feed.IsLoading);

            h.Client.Release(nameof(IWatchdogClient.GetEventsAsync));
            await reading;

            Assert.Equal(PageStatusKind.Idle, feed.Status.Current.Kind);
            Assert.StartsWith("Events: 30 · unacknowledged: 28 · as of ", feed.Status.Current.Message, StringComparison.Ordinal);
            Assert.Equal(PageStatusKind.Working, recorder.Plain[0].Kind);
            Assert.DoesNotContain(recorder.Plain, said => said.Kind is PageStatusKind.Error or PageStatusKind.Attention);

            // Three selected events join the summary.
            feed.SelectedItems = new List<object>(feed.Items.Take(3));
            Assert.EndsWith(" · 3 selected", feed.Status.Current.Message, StringComparison.Ordinal);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Stop_ReturnsControlAtOnce_AndTheFeedIsEmptyAndPausedUntilItIsReadAgain()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var feed = tab.Events;
            var recorder = new StatusRecorder(feed.Status);
            h.Client.Hold(nameof(IWatchdogClient.GetEventsAsync));

            var reading = feed.LoadAsync();
            feed.Status.CancelCommand.Execute(null);

            // "Stopping…" is said before the call heard anything.
            Assert.Equal("Stopping…", recorder.Plain.Select(s => s.Message).Single(m => m == "Stopping…"));
            await reading;

            // The read was stopped: the feed cannot go on from where it was, so it says that it is empty and paused - the
            // state of the page, which cannot be closed - with Refresh.
            Assert.Empty(feed.Items);
            Assert.False(feed.IsEmpty);
            Assert.False(feed.IsLoading);
            var line = feed.Status.Current;
            Assert.Equal(PageStatusKind.Attention, line.Kind);
            Assert.False(line.CanClose);
            Assert.Equal("Reading the events was stopped: the list is empty and live updates are paused. Press “Refresh” to read it again.", line.Message);
            Assert.Equal("Refresh", line.Action!.Label);
            Assert.Same(feed.RefreshCommand, line.Action.Command);

            // Refresh reads it again, and the state is gone.
            h.Client.Release(nameof(IWatchdogClient.GetEventsAsync));
            await feed.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(30, feed.Items.Count);
            Assert.Equal(PageStatusKind.Idle, feed.Status.Current.Kind);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AFailedRead_IsTheStateOfThePage_WithRetry_AndItsLogCopyHasNoEventAndNoHost()
    {
        var log = new ListLogger<PageStatusHolder>();
        using var h = await WatchdogHarness.CreateAsync(statusLog: log);
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var feed = tab.Events;
            h.Client.FailNext(nameof(IWatchdogClient.GetEventsAsync), new WatchdogClientException(WatchdogClientErrorKind.ServerError, "boom") { StatusCode = 500 });

            await feed.LoadAsync();

            Assert.Empty(feed.Items);
            var line = feed.Status.Current;
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.False(line.CanClose);
            Assert.StartsWith("Could not read the events: ", line.Message, StringComparison.Ordinal);
            Assert.Contains(feed.Error!, line.Message, StringComparison.Ordinal);
            Assert.EndsWith(" Press Retry to try again. (ServerError)", line.Message, StringComparison.Ordinal);
            Assert.Equal("Retry", line.Action!.Label);
            Assert.False(feed.IsEmpty);

            // Retry reads again and the state goes.
            await feed.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Idle, feed.Status.Current.Kind);
            Assert.Equal(30, feed.Items.Count);
        });
        h.AssertNoUiErrors();

        var written = Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(L.WatchdogEventsStatus.ReadFailed, StringComparison.Ordinal));
        Assert.Contains("[ServerError]", written.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("admng-wd", log.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Łukasz", log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRead_SaysItInPolish_WithTheWordsOfThePolishLine()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var feed = tab.Events;
            h.Client.FailNext(nameof(IWatchdogClient.GetEventsAsync), new WatchdogClientException(WatchdogClientErrorKind.ServerError, "boom"));

            await feed.LoadAsync();

            Assert.StartsWith("Nie udało się wczytać zdarzeń: ", feed.Status.Current.Message, StringComparison.Ordinal);
            Assert.EndsWith(" Wybierz Ponów, aby spróbować ponownie. (ServerError)", feed.Status.Current.Message, StringComparison.Ordinal);
            Assert.Equal("Ponów", feed.Status.Current.Action!.Label);

            // Stopped, in Polish as well.
            h.Client.Hold(nameof(IWatchdogClient.GetEventsAsync));
            var reading = feed.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("Wczytywanie zdarzeń…", feed.Status.Current.Message);
            feed.Status.CancelCommand.Execute(null);
            Assert.Equal("Zatrzymywanie…", feed.Status.Current.Message);
            await reading;
            Assert.Equal(
                "Wczytywanie zdarzeń zatrzymano: lista jest pusta, a aktualizacje na żywo wstrzymane. Wybierz „Odśwież”, aby wczytać ją ponownie.",
                feed.Status.Current.Message);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ANewerRead_ReplacesTheRunningOne_WithoutAWord()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var feed = tab.Events;
            var recorder = new StatusRecorder(feed.Status);
            h.Client.Hold(nameof(IWatchdogClient.GetEventsAsync));

            var first = feed.LoadAsync();
            var second = feed.LoadAsync();
            await first;
            h.Client.Release(nameof(IWatchdogClient.GetEventsAsync));
            await second;

            Assert.DoesNotContain(recorder.Plain, said => said.Kind is PageStatusKind.Error or PageStatusKind.Attention);
            Assert.Equal(PageStatusKind.Idle, feed.Status.Current.Kind);
            Assert.Equal(30, feed.Items.Count);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task OlderEvents_SayHowManyAreLoaded_StopLeavesTheListAsItWas_AndAFailureCanBeRepeated()
    {
        using var h = await WatchdogHarness.CreateAsync();
        for (var i = 0; i < 120; i++)
        {
            h.Client.AddEvent(SampleEvents.NewUser($"Użytkownik {i:D3}", SampleEvents.Now.AddMinutes(-5)));
        }

        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: true);
            var feed = tab.Events;
            Assert.Equal(100, feed.Items.Count);

            // Stop: the call is held, Stop answers at once, the list is as it was and the attention offers the button again.
            h.Client.HoldEvents(q => q.Before is not null);
            var older = feed.LoadOlderCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Working, feed.Status.Current.Kind);
            Assert.Equal("Reading older events (100 loaded)…", feed.Status.Current.Message);
            feed.Status.CancelCommand.Execute(null);
            Assert.Equal("Stopping…", feed.Status.Current.Message);
            await older;
            h.Client.ReleaseEvents();

            Assert.Equal(100, feed.Items.Count);
            var line = feed.Status.Current;
            Assert.Equal(PageStatusKind.Attention, line.Kind);
            Assert.True(line.CanClose);
            Assert.Equal("Reading older events was stopped: the list was not changed. Press “Load older” to try again.", line.Message);
            Assert.Same(feed.LoadOlderCommand, line.Action!.Command);

            // A failure leaves the list as it was as well: a result that can be closed, with Retry for the same read.
            h.Client.FailNext(nameof(IWatchdogClient.GetEventsAsync), new WatchdogClientException(WatchdogClientErrorKind.ServerError, "boom"));
            await feed.LoadOlderCommand.ExecuteAsync(null);
            line = feed.Status.Current;
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.True(line.CanClose);
            Assert.StartsWith("Could not read older events: ", line.Message, StringComparison.Ordinal);
            Assert.Equal(100, feed.Items.Count);
            Assert.Same(feed.LoadOlderCommand, line.Action!.Command);

            // Retry, and the older events arrive; the line goes back to the summary.
            await feed.LoadOlderCommand.ExecuteAsync(null);
            Assert.Equal(150, feed.Items.Count);
            Assert.Equal(PageStatusKind.Idle, feed.Status.Current.Kind);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Acknowledging_IsAWriteWithoutStop_ThePartDoneIsTheRing_AndTheResultIsTheRows()
    {
        var log = new ListLogger<PageStatusHolder>();
        using var h = await WatchdogHarness.CreateAsync(statusLog: log);
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var feed = tab.Events;
            var rows = Enumerable.Range(1, 1200)
                .Select(i => new WatchdogEventRowViewModel(SampleEvents.NewUser($"U{i}", SampleEvents.Now) with { Id = 1000 + i }, h.Ad.Localizer, TimeZoneInfo.Utc))
                .ToList<object>();
            feed.SelectedItems = (IList)rows;
            var recorder = new StatusRecorder(feed.Status);

            await feed.AcknowledgeSelectedCommand.ExecuteAsync(null);

            // 0, 500 and 1000 of 1200, with the part done; nothing could be stopped.
            var working = recorder.Seen.Where(s => s.Kind == PageStatusKind.Working).ToList();
            Assert.Equal(
                ["Acknowledging the events: 0 of 1,200…", "Acknowledging the events: 500 of 1,200…", "Acknowledging the events: 1,000 of 1,200…", "Acknowledging the events: 1,200 of 1,200…"],
                working.Select(s => s.Message).Distinct());
            Assert.Equal(0d, working[0].Fraction);
            Assert.Equal(500d / 1200, working[1].Fraction!.Value, 6);
            Assert.DoesNotContain(recorder.Seen, s => s.Kind == PageStatusKind.Working && s.Message == "Stopping…");

            // The result is the rows and the counts of the summary; the toast is the main window's, as before.
            Assert.Equal(PageStatusKind.Idle, feed.Status.Current.Kind);
            Assert.Contains(h.Ad.Notifications.Items, n => n.Severity == AdMng.Presentation.Services.NotificationSeverity.Success);
        });
        h.AssertNoUiErrors();
        Assert.Contains(log.Entries, e => e.Message.Contains(L.WatchdogEventsStatus.Acknowledging, StringComparison.Ordinal) && e.Message.Contains("0, 1200", StringComparison.Ordinal));
        Assert.DoesNotContain("U1", log.AllText.Replace("0, 1200", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedAcknowledgement_SaysWhatWasDone_AndWhatToDo_AndLeavesTheRowsThatWereNotAcknowledged()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var feed = tab.Events;
            var rows = Enumerable.Range(1, 600)
                .Select(i => new WatchdogEventRowViewModel(SampleEvents.NewUser($"U{i}", SampleEvents.Now) with { Id = 1000 + i }, h.Ad.Localizer, TimeZoneInfo.Utc))
                .ToList<object>();
            feed.SelectedItems = (IList)rows;

            // The first request goes through, the second fails.
            h.Client.FailNext(nameof(IWatchdogClient.AcknowledgeManyAsync), new WatchdogClientException(WatchdogClientErrorKind.ServerError, "x"));
            await feed.AcknowledgeSelectedCommand.ExecuteAsync(null);
            var line = feed.Status.Current;

            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.True(line.CanClose);
            Assert.StartsWith("Could not acknowledge the events: ", line.Message, StringComparison.Ordinal);
            Assert.EndsWith("(ServerError)", line.Message, StringComparison.Ordinal);
            Assert.Null(line.Action);
            Assert.False(feed.IsBusy);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AnAcknowledgementThatFailsAfterAReadTookTheLine_IsAToast_NotLost()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var feed = tab.Events;
            var rows = Enumerable.Range(1, 3)
                .Select(i => new WatchdogEventRowViewModel(SampleEvents.NewUser($"U{i}", SampleEvents.Now) with { Id = 1000 + i }, h.Ad.Localizer, TimeZoneInfo.Utc))
                .ToList<object>();
            feed.SelectedItems = (IList)rows;
            h.Client.Hold(nameof(IWatchdogClient.AcknowledgeManyAsync));
            h.Client.FailNext(nameof(IWatchdogClient.AcknowledgeManyAsync), new WatchdogClientException(WatchdogClientErrorKind.ServerError, "boom"));

            // The feed is read again (a filter changed) while the acknowledgement is on its way: the read has the line.
            var acknowledging = feed.AcknowledgeSelectedCommand.ExecuteAsync(null);
            await feed.LoadAsync();
            h.Client.Release(nameof(IWatchdogClient.AcknowledgeManyAsync));
            await acknowledging;

            // What finished elsewhere is a toast, and the line is not taken from the read that has it.
            Assert.NotEqual(PageStatusKind.Error, feed.Status.Current.Kind);
            Assert.Contains(
                h.Ad.Notifications.Items,
                n => n.Severity == AdMng.Presentation.Services.NotificationSeverity.Error
                    && n.Message.StartsWith("Could not acknowledge the events: ", StringComparison.Ordinal));
            Assert.False(feed.IsBusy);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task TheDetailsPane_SaysItsAnswers_InTheLineOfTheFeed_AndTheNextSelectionClosesThem()
    {
        var log = new ListLogger<PageStatusHolder>();
        using var h = await WatchdogHarness.CreateAsync(statusLog: log);
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: true);
            var feed = tab.Events;
            var row = feed.Items.First(r => !r.IsAcknowledged);
            feed.SelectedItem = row;
            await feed.Details.Loading;

            // A failed acknowledgement: an error of the pane, said in the line of the feed.
            h.Client.FailNext(nameof(IWatchdogClient.AcknowledgeAsync), new WatchdogClientException(WatchdogClientErrorKind.Forbidden, "no"));
            await feed.Details.AcknowledgeCommand.ExecuteAsync(null);
            var line = feed.Status.Current;
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.StartsWith("Could not acknowledge the event: ", line.Message, StringComparison.Ordinal);
            Assert.EndsWith("(Forbidden)", line.Message, StringComparison.Ordinal);
            Assert.True(line.CanClose);

            // Another event selected: what the line said about the last one is closed.
            feed.SelectedItem = feed.Items.First(r => r.Id != row.Id);
            await feed.Details.Loading;
            Assert.Equal(PageStatusKind.Idle, feed.Status.Current.Kind);

            // An acknowledgement that works says nothing: the pane and the row show it.
            await feed.Details.AcknowledgeCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Idle, feed.Status.Current.Kind);
            Assert.True(feed.SelectedItem!.IsAcknowledged);
        });
        h.AssertNoUiErrors();
        Assert.DoesNotContain("Forbidden", log.AllText.Replace("[Forbidden]", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReadOfTheDetails_IsNoOperationOfTheLine_ItsFailureIsTheNoteOfThePane()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: true);
            var feed = tab.Events;
            var recorder = new StatusRecorder(feed.Status);
            h.Client.FailNext(nameof(IWatchdogClient.GetEventAsync), new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down"));

            feed.SelectedItem = feed.Items[0];
            await feed.Details.Loading;

            // The pane's own read: a ring and, when it failed, a sentence about this event - not an operation, not a result.
            Assert.NotNull(feed.Details.Error);
            Assert.True(feed.Details.HasEvent);
            Assert.DoesNotContain(recorder.Plain, said => said.Kind is PageStatusKind.Working or PageStatusKind.Error or PageStatusKind.Attention);

            // The next selection takes the note away.
            feed.SelectedItem = feed.Items[1];
            await feed.Details.Loading;
            Assert.Null(feed.Details.Error);
        });
        h.AssertNoUiErrors();
    }
}
