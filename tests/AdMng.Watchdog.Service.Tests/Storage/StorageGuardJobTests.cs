using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Storage;

/// <summary>The hourly size-cap and free-space checks (spec 16.3).</summary>
public sealed class StorageGuardJobTests
{
    [Fact]
    public async Task ReportsSizeAndFreeSpace()
    {
        await using var store = TestStore.Create();
        var health = new FakeHealthRegistry();
        var config = new FakeConfigProvider(new WatchdogConfiguration { Retention = new RetentionSettings { MaxDatabaseMb = 512 } });
        var job = new StorageGuardJob(store.Store, config, health, new DataDirectory(store.Directory.Path), store.Clock, new TestLog().For<StorageGuardJob>());

        var result = await job.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal("storage", job.Name);
        Assert.Equal(TimeSpan.FromHours(1), job.DefaultInterval);
        var size = Assert.Single(health.Checks, c => c.Code == "storage.size");
        Assert.Equal(HealthState.Ok, size.State);
        Assert.Contains(health.Checks, c => c.Code == "storage.free");
    }
}
