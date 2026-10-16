using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

namespace AdMng.Watchdog.IntegrationTests.Detection;

/// <summary>The baseline of the whole lab domain (spec 10.4, 22.4): silent (health events only) and under a minute.</summary>
public sealed class BaselineTests
{
    private static readonly TimeSpan MaxBaseline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Baseline_OfTheWholeLabDomain_IsSilentAndTakesLessThanAMinute()
    {
        WatchdogLab.RequireLab();
        var ct = TestContext.Current.CancellationToken;

        await using var host = await EngineHost.StartAsync(EngineAccount.ReadOnly, null, ct);

        Assert.True(host.BaselineDuration < MaxBaseline, $"baseline took {host.BaselineDuration}");
        var events = await host.AllEventsAsync(0, ct);
        Assert.Contains(events, e => e.Kind == WatchEventKind.HealthBaselineCompleted);
        Assert.All(events, e => Assert.Equal(EventCategory.Health, EventKindCatalog.Get(e.Kind).Category));
        Assert.True(await host.Snapshots.CountLiveObjectsAsync(ct) > 500, "the lab has hundreds of watched objects");

        // No directory value (DNs of the ~800 objects read) and no credential reaches the diagnostic log.
        Assert.NotEmpty(host.Log.Lines);
        Assert.DoesNotContain("OU=", host.Log.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CN=", host.Log.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(WatchdogLab.ReadOnlyPassword, host.Log.Text, StringComparison.Ordinal);
    }
}
