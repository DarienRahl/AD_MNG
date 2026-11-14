using System.Globalization;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Presentation.Watchdog;
using AdMng.Testing;
using AdMng.Watchdog.Client;
using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// What the status tab of the service ("Stan") says in the status line (stage V2b-4, unit 4): the read of the status, of the
/// delivery log and of the configuration audit one after another on the holder of the tab, each stoppable; the summary says when
/// the status shown was received, a read that failed with nothing on screen is the state of the tab, and the retry of a dead
/// delivery is a write without Stop.
/// </summary>
public sealed class WatchdogServiceStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task TheReads_FollowEachOther_OnTheHolderOfTheTab_AndTheSummaryIsWhenTheStatusWasReceived()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services) { IsAdmin = true };
            var recorder = new StatusRecorder(status.PageHolder);

            await status.LoadAsync();

            // The status, then the delivery log, then the audit - one operation after another, never two at once.
            var working = recorder.Plain.Where(s => s.Kind == PageStatusKind.Working).Select(s => s.Message).ToList();
            Assert.Equal(["Reading the status of the service…", "Reading the delivery log…", "Reading the configuration audit…"], working);
            Assert.DoesNotContain(recorder.Plain, s => s.Kind is PageStatusKind.Error or PageStatusKind.Attention);
            Assert.Equal(PageStatusKind.Idle, status.PageHolder.Current.Kind);
            Assert.StartsWith("Status read ", status.PageHolder.Current.Message, StringComparison.Ordinal);
            Assert.NotEmpty(status.DeliveryLog.Items);
            Assert.NotEmpty(status.Audit);

            h.Ad.Localizer.SetCulture(Polish);
            status.Relocalize();
            Assert.StartsWith("Stan odczytano ", status.PageHolder.Current.Message, StringComparison.Ordinal);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AViewer_ReadsOnlyTheStatus()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services) { IsAdmin = false };
            var recorder = new StatusRecorder(status.PageHolder);

            await status.LoadAsync();

            Assert.Equal(["Reading the status of the service…"], recorder.Plain.Where(s => s.Kind == PageStatusKind.Working).Select(s => s.Message));
            Assert.Empty(status.DeliveryLog.Items);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task StopOfTheStatusRead_ReturnsControlAtOnce_AndTheListsAreNotRead()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services) { IsAdmin = true };
            h.Client.Hold(nameof(IWatchdogClient.GetStatusAsync));

            var reading = status.LoadAsync();
            Assert.Equal("Stop", status.PageHolder.Current.Action!.Label);
            status.PageHolder.CancelCommand.Execute(null);
            Assert.Equal("Stopping…", status.PageHolder.Current.Message);
            await reading;
            h.Client.Release(nameof(IWatchdogClient.GetStatusAsync));

            // Nothing is shown: the stop is the state of the tab, with Refresh.
            Assert.False(status.IsLoading);
            Assert.False(status.HasStatus);
            var line = status.PageHolder.Current;
            Assert.Equal(PageStatusKind.Attention, line.Kind);
            Assert.False(line.CanClose);
            Assert.Equal("Reading the status was stopped: the tab may show an older one. Press “Refresh” to read it again.", line.Message);
            Assert.Same(status.RefreshCommand, line.Action!.Command);
            Assert.Empty(status.DeliveryLog.Items);
            Assert.Equal(0, h.Client.Calls.Count(nameof(IWatchdogClient.GetOutboxAsync)));

            await status.RefreshCommand.ExecuteAsync(null);
            Assert.True(status.HasStatus);
            Assert.StartsWith("Status read ", status.PageHolder.Current.Message, StringComparison.Ordinal);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AFailedRead_IsTheStateOfTheTab_WhileNothingIsShown_AndAClosableResultAfterwards()
    {
        var log = new ListLogger<PageStatusHolder>();
        using var h = await WatchdogHarness.CreateAsync(statusLog: log);
        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services) { IsAdmin = false };
            Exception? failed = null;
            status.Failed += (_, e) => failed = e;
            h.Client.FailNext(nameof(IWatchdogClient.GetStatusAsync), new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down"));

            await status.LoadAsync();

            var line = status.PageHolder.Current;
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.False(line.CanClose);
            Assert.StartsWith("Could not read the status of the service: ", line.Message, StringComparison.Ordinal);
            Assert.EndsWith(" Press Retry to try again. (Unreachable)", line.Message, StringComparison.Ordinal);
            Assert.Equal("Retry", line.Action!.Label);
            Assert.NotNull(failed);
            Assert.NotNull(status.Error);

            // Retry reads it and the state goes.
            await status.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Idle, status.PageHolder.Current.Kind);

            // With a status on screen a failed read leaves it as it was: a result that can be closed.
            h.Client.FailNext(nameof(IWatchdogClient.GetStatusAsync), new WatchdogClientException(WatchdogClientErrorKind.ServerError, "boom"));
            await status.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Error, status.PageHolder.Current.Kind);
            Assert.True(status.PageHolder.Current.CanClose);
            Assert.True(status.HasStatus);
        });
        h.AssertNoUiErrors();
        Assert.DoesNotContain("admng-wd", log.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(L.WatchdogPageStatus.ReadFailed, StringComparison.Ordinal) && e.Message.Contains("[Unreachable]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheDeliveryLog_SaysItsStop_ItsFailureWithRetry_AndTheRetryOfADeadDeliveryIsAWriteWithoutStop()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services) { IsAdmin = true };
            await status.LoadAsync();
            var log = status.DeliveryLog;
            var holder = status.PageHolder;

            // A list that is read again can be stopped; it is as it was.
            var items = log.Items.Count;
            h.Client.Hold(nameof(IWatchdogClient.GetOutboxAsync));
            var reading = log.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("Reading the delivery log…", holder.Current.Message);
            holder.CancelCommand.Execute(null);
            await reading;
            h.Client.Release(nameof(IWatchdogClient.GetOutboxAsync));
            Assert.Equal(items, log.Items.Count);
            Assert.False(log.IsLoading);
            Assert.Equal(PageStatusKind.Attention, holder.Current.Kind);
            Assert.Equal("Reading the delivery log was stopped: the list was not changed. Press “Refresh” to try again.", holder.Current.Message);
            Assert.Same(log.RefreshCommand, holder.Current.Action!.Command);

            // A failure: an error with Retry, the list as it was.
            h.Client.FailNext(nameof(IWatchdogClient.GetOutboxAsync), new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down"));
            await log.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Error, holder.Current.Kind);
            Assert.StartsWith("Could not read the delivery log: ", holder.Current.Message, StringComparison.Ordinal);
            Assert.NotNull(log.Error);
            Assert.Equal(items, log.Items.Count);

            // The retry of a dead delivery: working without Stop, and - when it works - the row says it, not the line.
            var dead = log.Items.First(r => r.IsDead);
            log.SelectedItem = dead;
            h.Client.Hold(nameof(IWatchdogClient.RetryOutboxAsync));
            var retrying = log.RetryCommand.ExecuteAsync(null);
            Assert.Equal("Queueing the delivery again…", holder.Current.Message);
            Assert.Null(holder.Current.Action);
            h.Client.Release(nameof(IWatchdogClient.RetryOutboxAsync));
            await retrying;
            Assert.False(dead.IsDead);
            Assert.Equal(PageStatusKind.Idle, holder.Current.Kind);

            // Retried elsewhere in the meantime: the service refuses, the error is said in the line.
            var other = log.Items.First(r => r.IsDead);
            h.Client.FailNext(nameof(IWatchdogClient.RetryOutboxAsync), new WatchdogClientException(WatchdogClientErrorKind.NotFound, "gone"));
            await log.RetryCommand.ExecuteAsync(other);
            Assert.Equal(PageStatusKind.Error, holder.Current.Kind);
            Assert.StartsWith("Could not queue the delivery again: ", holder.Current.Message, StringComparison.Ordinal);
            Assert.Null(holder.Current.Action);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task TheAudit_CanBeStoppedAndReadAgain_AndAFailureIsSaidWithRetry()
    {
        using var h = await WatchdogHarness.CreateAsync();
        for (var i = 0; i < 80; i++)
        {
            h.Client.Audit.Add(h.Client.Audit[0] with { Id = 1000 + i });
        }

        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services) { IsAdmin = true };
            await status.LoadAsync();
            Assert.True(status.AuditHasMore);
            var holder = status.PageHolder;
            var shown = status.Audit.Count;

            h.Client.FailNext(nameof(IWatchdogClient.GetConfigAuditAsync), new WatchdogClientException(WatchdogClientErrorKind.ServerError, "boom"));
            await status.LoadMoreAuditCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Error, holder.Current.Kind);
            Assert.StartsWith("Could not read the configuration audit: ", holder.Current.Message, StringComparison.Ordinal);
            Assert.Same(status.LoadMoreAuditCommand, holder.Current.Action!.Command);
            Assert.NotNull(status.AuditError);
            Assert.Equal(shown, status.Audit.Count);

            h.Client.Hold(nameof(IWatchdogClient.GetConfigAuditAsync));
            var reading = status.LoadMoreAuditCommand.ExecuteAsync(null);
            Assert.Equal("Reading the configuration audit…", holder.Current.Message);
            holder.CancelCommand.Execute(null);
            await reading;
            h.Client.Release(nameof(IWatchdogClient.GetConfigAuditAsync));
            Assert.Equal(PageStatusKind.Attention, holder.Current.Kind);
            Assert.Equal("Reading the configuration audit was stopped: the list was not changed. Press “Load older” to try again.", holder.Current.Message);
            Assert.Equal(shown, status.Audit.Count);

            await status.LoadMoreAuditCommand.ExecuteAsync(null);
            Assert.True(status.Audit.Count > shown);
            Assert.Equal(PageStatusKind.Idle, holder.Current.Kind);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task OnTheTab_TheStatusPageIsInFront_AndTheLineSaysItsRead()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.SelectedSubTab = WatchdogSubTab.Status;
            await tab.ServiceStatus.LoadAsync();
            Assert.StartsWith("Status read ", tab.Status.Current.Message, StringComparison.Ordinal);

            h.Client.Hold(nameof(IWatchdogClient.GetStatusAsync));
            var reading = tab.ServiceStatus.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Working, tab.Status.Current.Kind);
            Assert.Equal("Reading the status of the service…", tab.Status.Current.Message);
            h.Client.Release(nameof(IWatchdogClient.GetStatusAsync));
            await reading;
            Assert.Equal(PageStatusKind.Idle, tab.Status.Current.Kind);
        });
        h.AssertNoUiErrors();
    }
}
