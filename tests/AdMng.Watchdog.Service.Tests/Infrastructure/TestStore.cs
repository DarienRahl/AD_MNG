using AdMng.Watchdog.Service.Secrets;
using AdMng.Watchdog.Service.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>A migrated SQLite store in its own temporary data folder with a fake clock (storage tests).</summary>
public sealed class TestStore : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private TestStore(TestDataDirectory directory, FakeTimeProvider clock, SqliteWatchdogStore store)
    {
        Directory = directory;
        Clock = clock;
        Store = store;
    }

    public TestDataDirectory Directory { get; }

    public FakeTimeProvider Clock { get; }

    public SqliteWatchdogStore Store { get; }

    public SqliteDatabase Database => Store.Database;

    public static TestStore Create(TestDataDirectory? directory = null, IDataProtectionProvider? dataProtection = null)
    {
        directory ??= new TestDataDirectory();
        var clock = new FakeTimeProvider(Start);
        var store = SqliteWatchdogStore.Open(
            directory.Path,
            new SecretProtector(dataProtection ?? new EphemeralDataProtectionProvider()),
            clock,
            NullLogger<MigrationRunner>.Instance);
        return new TestStore(directory, clock, store);
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        Directory.Dispose();
    }
}
