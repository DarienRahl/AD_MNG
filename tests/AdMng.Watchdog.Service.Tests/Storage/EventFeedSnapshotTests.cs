using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Storage;

/// <summary>
/// The live feed (GUI long-poll) advances its cursor to <see cref="EventPage.LatestId"/> when a page has no more rows, so
/// the page rows and <c>LatestId</c> must come from ONE database snapshot – otherwise events committed between the two
/// statements are skipped for good.
/// </summary>
public sealed class EventFeedSnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadSnapshot_EveryStatementSeesTheSameCommittedState()
    {
        await using var store = TestStore.Create();
        await store.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated, TestData.User("Zofia Żak")), Ct);

        var (first, second) = store.Database.ReadSnapshot((connection, transaction) =>
        {
            var before = SqliteValues.Scalar<long>(connection, transaction, "SELECT coalesce(max(id), 0) FROM events;");

            // Another writer commits between the two statements (the detection job appending one event per transaction).
            store.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated, TestData.User("Łucja Ćwik")), Ct).GetAwaiter().GetResult();

            var after = SqliteValues.Scalar<long>(connection, transaction, "SELECT coalesce(max(id), 0) FROM events;");
            return (before, after);
        });

        Assert.Equal(1L, first);
        Assert.Equal(first, second);
        Assert.Equal(2L, await store.Store.GetLatestIdAsync(Ct));
    }

    [Fact]
    public async Task LiveFeed_UsingTheGuiCursorRule_NeverSkipsEventsAppendedConcurrently()
    {
        await using var store = TestStore.Create();
        const int total = 400;
        using var appendsDone = new CancellationTokenSource();
        var seen = new HashSet<long>();
        var cursor = 0L;

        // The reader polls as tightly as possible – exactly the WatchdogPoller rule: when HasMore is false the cursor
        // jumps to max(cursor, newest, LatestId).
        var reader = Task.Run(
            async () =>
            {
                var drainedAfterStop = false;
                while (!drainedAfterStop)
                {
                    drainedAfterStop = appendsDone.IsCancellationRequested;
                    var page = await store.Store.QueryAsync(new EventQuery { After = cursor, Limit = 500 }, Ct);
                    foreach (var item in page.Items)
                    {
                        seen.Add(item.Id);
                    }

                    var newest = page.Items.Count == 0 ? cursor : page.Items.Max(e => e.Id);
                    cursor = page.HasMore ? Math.Max(cursor, newest) : Math.Max(Math.Max(cursor, newest), page.LatestId);
                    drainedAfterStop &= !page.HasMore;
                }
            },
            Ct);

        var ids = new List<long>(total);
        for (var i = 0; i < total; i++)
        {
            ids.Add(await store.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated, TestData.User($"Paweł Łąkowski {i}")), Ct));
        }

        await appendsDone.CancelAsync();
        await reader;

        var lost = ids.Where(id => !seen.Contains(id)).ToList();
        Assert.True(lost.Count == 0, $"{lost.Count} of {total} events never reached the live feed.");
    }
}
