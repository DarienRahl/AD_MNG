using System.Reflection;
using AdMng.Watchdog.Service.Secrets;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Service.Tests.Storage;

/// <summary>The shared store contract suite (spec 22.2) against the SQLite store: one temporary database per fact.</summary>
public sealed class SqliteStoreContractTests : WatchdogStoreContractTests<SqliteWatchdogStore>, IDisposable
{
    private readonly List<TestDataDirectory> _directories = [];
    private readonly FakeTimeProvider _fallbackClock = new(Now);

    protected override Task<SqliteWatchdogStore> CreateStoreAsync()
    {
        var directory = new TestDataDirectory();
        _directories.Add(directory);
        var store = SqliteWatchdogStore.Open(
            directory.Path,
            new SecretProtector(new EphemeralDataProtectionProvider()),
            StoreClock,
            NullLogger<MigrationRunner>.Instance);
        return Task.FromResult(store);
    }

    protected override Task<long> StoredImageBytesAsync(SqliteWatchdogStore store) => store.UserMessages.StoredImageBytesAsync(CancellationToken.None);

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            directory.Dispose();
        }
    }

    /// <summary>The clock the suite pins store-written times with: its protected <c>Clock</c> property when the suite
    /// has one (the full suite of W-notify), otherwise a fake clock at <c>Now</c>.</summary>
    private TimeProvider StoreClock =>
        typeof(WatchdogStoreContractTests<SqliteWatchdogStore>)
            .GetProperty("Clock", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?.GetValue(this) as TimeProvider ?? _fallbackClock;
}
