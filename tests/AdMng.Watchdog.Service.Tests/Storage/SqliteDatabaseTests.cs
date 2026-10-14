using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Storage;

public sealed class SqliteDatabaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryConnection_HasThePrivacyAndSafetyPragmas()
    {
        await using var store = TestStore.Create();

        for (var i = 0; i < 3; i++)
        {
            using var connection = store.Database.Open();
            Assert.Equal(2L, Pragma(connection, "temp_store"));      // MEMORY: no sort/VACUUM spill to %TEMP%
            Assert.Equal(1L, Pragma(connection, "secure_delete"));
            Assert.Equal(1L, Pragma(connection, "foreign_keys"));
            Assert.Equal(1L, Pragma(connection, "synchronous"));     // NORMAL
            Assert.Equal(30000L, Pragma(connection, "busy_timeout"));
        }
    }

    [Fact]
    public async Task Writes_AreSerialized_AndReadersSeeOnlyCommittedData()
    {
        await using var store = TestStore.Create();
        using var firstInside = new SemaphoreSlim(0);
        using var releaseFirst = new SemaphoreSlim(0);
        var secondRan = false;

        var first = Task.Run(
            () => store.Database.WriteAsync(
                (connection, transaction) =>
                {
                    SqliteValues.Execute(connection, transaction, "INSERT INTO meta (key, value) VALUES ('próba', 'Zażółć');");
                    firstInside.Release();
                    releaseFirst.Wait(Ct);
                },
                Ct),
            Ct);
        await firstInside.WaitAsync(Ct);
        var second = store.Database.WriteAsync((_, _) => secondRan = true, Ct);

        // The second writer waits for the gate; a reader is not blocked and does not see the open transaction.
        Assert.False(second.IsCompleted);
        Assert.False(secondRan);
        Assert.Equal(0L, store.Database.Read(c => SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM meta WHERE key = 'próba';")));

        releaseFirst.Release();
        await first;
        await second;

        Assert.True(secondRan);
        Assert.Equal(1L, store.Database.Read(c => SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM meta WHERE key = 'próba';")));
    }

    [Fact]
    public async Task AFailedWrite_RollsBackEverything()
    {
        await using var store = TestStore.Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Database.WriteAsync(
            (connection, transaction) =>
            {
                SqliteValues.Execute(connection, transaction, "INSERT INTO meta (key, value) VALUES ('częściowo', 'tak');");
                throw new InvalidOperationException("boom");
            },
            Ct));

        Assert.Equal(0L, store.Database.Read(c => SqliteValues.Scalar<long>(c, null, "SELECT count(*) FROM meta;")));
        Assert.Equal(1, (await store.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated), Ct)));
    }

    private static long Pragma(Microsoft.Data.Sqlite.SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return (long)command.ExecuteScalar()!;
    }
}
