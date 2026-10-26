using System.Collections.Concurrent;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Client;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>Live updates (D9): long-poll while visible, status-only while hidden, back-off, wake-up and stop.</summary>
public sealed class WatchdogPollerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(SampleEvents.Now);
    private readonly FakeWatchdogClient _client;
    private readonly TestUiThread _ui = new();
    private readonly WatchdogPoller _poller;
    private readonly ConcurrentQueue<EventPage> _pages = new();
    private readonly ConcurrentQueue<StatusDto> _statuses = new();
    private readonly ConcurrentQueue<Exception> _failures = new();
    private int _recoveries;

    public WatchdogPollerTests()
    {
        _client = new FakeWatchdogClient(_time);
        _poller = new WatchdogPoller(_client, _time, _ui);
        _poller.EventsReceived += (_, page) =>
        {
            Assert.True(_ui.CheckAccess());
            _pages.Enqueue(page);
        };
        _poller.StatusReceived += (_, status) => _statuses.Enqueue(status);
        _poller.Failed += (_, error) => _failures.Enqueue(error);
        _poller.Recovered += (_, _) => Interlocked.Increment(ref _recoveries);
    }

    public void Dispose()
    {
        _poller.Dispose();
        _ui.Dispose();
        _client.Dispose();
    }

    [Fact]
    public async Task Visible_LongPollsAfterTheNewestId_WithTheFeedFilter_AndRaisesNewEventsOnTheUiThread()
    {
        _poller.Filter = () => new EventQuery { MinSeverity = Severity.Low, Text = "Kowalski", Limit = 100 };
        _poller.Cursor = 30;
        _poller.IsVisible = true;
        _poller.Start();

        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 1);
        var query = Assert.Single(_client.EventQueries);
        Assert.Equal(30, query.After);
        Assert.Null(query.Before);
        Assert.Equal(WatchdogPoller.WaitSeconds, query.WaitSeconds);
        Assert.Equal(WatchdogPoller.PageLimit, query.Limit);
        Assert.Equal(Severity.Low, query.MinSeverity);
        Assert.Equal("Kowalski", query.Text);
        Assert.Equal(1, _client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync)));

        var added = _client.AddEvent(SampleEvents.NewUser("Jan Kowalski-Nowak", _time.GetUtcNow()));
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 2);
        await _ui.IdleAsync();

        var page = Assert.Single(_pages);
        Assert.Equal(added.Id, Assert.Single(page.Items).Id);
        Assert.Equal(31, _poller.Cursor);
        Assert.Equal(31, _client.EventQueries[^1].After);
        Assert.Empty(_failures);
    }

    [Fact]
    public async Task Visible_FilteredOutEvents_MoveTheCursorToTheLatestId()
    {
        _poller.Filter = () => new EventQuery { MinSeverity = Severity.Critical };
        _poller.Cursor = 30;
        _poller.IsVisible = true;
        _poller.Start();
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 1);

        _client.AddEvent(SampleEvents.NewUser("Celina Mróz", _time.GetUtcNow(), Severity.Low));
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 2);
        await _ui.IdleAsync();

        Assert.Empty(_pages);
        Assert.Equal(31, _poller.Cursor);
        Assert.Equal(31, _client.EventQueries[^1].After);
    }

    [Fact]
    public async Task Visible_WithoutALoadedFeed_ReadsOnlyTheStatus()
    {
        _poller.Filter = () => new EventQuery();
        _poller.IsVisible = true;
        _poller.Start();

        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), 1);
        _time.Advance(WatchdogPoller.VisibleStatusInterval);
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), 2);

        Assert.Equal(0, _client.Calls.Count(nameof(IWatchdogClient.GetEventsAsync)));
    }

    [Fact]
    public async Task Hidden_ReadsOnlyTheStatus_EverySixtySeconds()
    {
        _poller.Filter = () => new EventQuery();
        _poller.Cursor = 30;
        _poller.IsVisible = false;
        _poller.Start();

        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), 1);
        _time.Advance(WatchdogPoller.HiddenStatusInterval - TimeSpan.FromSeconds(1));
        await _ui.IdleAsync();
        Assert.Equal(1, _client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync)));

        _time.Advance(TimeSpan.FromSeconds(1));
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), 2);
        await _ui.IdleAsync();

        Assert.Equal(0, _client.Calls.Count(nameof(IWatchdogClient.GetEventsAsync)));
        Assert.Equal(2, _statuses.Count);
        Assert.Equal(_time.GetUtcNow(), _statuses.Last().Now);
    }

    [Fact]
    public async Task BecomingVisible_StartsTheLongPollAtOnce()
    {
        _poller.Filter = () => new EventQuery();
        _poller.Cursor = 30;
        _poller.Start();
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), 1);

        // The status answer must have arrived: the call is recorded before it returns, and waking the poller while it
        // is still running cancels it (the next round then reads the status again).
        await WatchdogTestWait.UntilAsync(_ui, () => !_statuses.IsEmpty, "the status");
        _poller.IsVisible = true;
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 1);

        // The status read a moment ago is still fresh: no second status call.
        Assert.Equal(1, _client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync)));
    }

    [Fact]
    public async Task Wake_RestartsTheLongPoll_WithTheNewFilter()
    {
        var minimum = Severity.Info;
        _poller.Filter = () => new EventQuery { MinSeverity = minimum };
        _poller.Cursor = 30;
        _poller.IsVisible = true;
        _poller.Start();
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 1);

        minimum = Severity.High;
        _poller.Wake();
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 2);

        Assert.Equal(Severity.High, _client.EventQueries[^1].MinSeverity);
        Assert.Empty(_failures);
    }

    [Fact]
    public async Task AnswerForAnOldCursor_IsDropped()
    {
        _poller.Filter = () => new EventQuery();
        _poller.Cursor = 30;
        _poller.IsVisible = true;
        _client.Hold(nameof(IWatchdogClient.GetEventsAsync));
        _poller.Start();
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 1);

        // The feed reloads meanwhile (new filter): the answer to the old request must not be prepended.
        _client.AddEvent(SampleEvents.NewUser("Stara Odpowiedź", _time.GetUtcNow()));
        _poller.Cursor = 31;
        _client.Release(nameof(IWatchdogClient.GetEventsAsync));
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 2);
        await _ui.IdleAsync();

        Assert.Empty(_pages);
        Assert.Equal(31, _client.EventQueries[^1].After);
    }

    [Fact]
    public async Task Failures_BackOffFromTwoToSixtySeconds_AndRecover()
    {
        _poller.Filter = () => new EventQuery();
        _poller.IsVisible = false;
        _client.FailWith(WatchdogClientErrorKind.Unreachable);
        _poller.Start();

        var expected = new[] { 2, 4, 8, 16, 32, 60, 60 };
        for (var i = 0; i < expected.Length; i++)
        {
            await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), i + 1);
            await WaitForFailuresAsync(i + 1);
            Assert.Equal(TimeSpan.FromSeconds(expected[i]), _poller.CurrentBackoff);

            // Nothing happens before the back-off ends.
            _time.Advance(_poller.CurrentBackoff - TimeSpan.FromMilliseconds(1));
            await _ui.IdleAsync();
            Assert.Equal(i + 1, _client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync)));
            _time.Advance(TimeSpan.FromMilliseconds(1));
        }

        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), expected.Length + 1);
        _client.ClearFailures();
        await WaitForFailuresAsync(expected.Length + 1);
        _time.Advance(_poller.CurrentBackoff);
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), expected.Length + 2);
        await WatchdogTestWait.UntilAsync(_ui, () => Volatile.Read(ref _recoveries) > 0, "the recovery");

        Assert.Equal(TimeSpan.Zero, _poller.CurrentBackoff);
        Assert.Equal(1, _recoveries);
        Assert.All(_failures, f => Assert.Equal(WatchdogClientErrorKind.Unreachable, Assert.IsType<WatchdogClientException>(f).Kind));
        Assert.Single(_statuses);
    }

    [Fact]
    public async Task Stop_AndDispose_EndTheLoop()
    {
        _poller.Filter = () => new EventQuery();
        _poller.Cursor = 30;
        _poller.IsVisible = true;
        _poller.Start();
        _poller.Start();
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 1);
        Assert.True(_poller.IsRunning);

        _poller.Stop();
        await _poller.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(_poller.IsRunning);
        Assert.Empty(_failures);

        _poller.Start();
        await _client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 2);
        _poller.Dispose();
        await _poller.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Throws<ObjectDisposedException>(_poller.Start);
    }

    private Task WaitForFailuresAsync(int count) => WatchdogTestWait.UntilAsync(_ui, () => _failures.Count >= count, $"{count} failures");
}
