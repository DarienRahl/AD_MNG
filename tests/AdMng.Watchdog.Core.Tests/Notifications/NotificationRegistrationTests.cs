using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Notifications.Digest;
using AdMng.Watchdog.Core.Retention;
using AdMng.Watchdog.Core.Tests.Notifications.Fakes;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class NotificationRegistrationTests
{
    [Fact]
    public void AddWatchdogCore_RegistersEveryNotificationJobOnce()
    {
        using var provider = Services().AddWatchdogCore().AddWatchdogCore().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var jobs = provider.GetServices<IWatchdogJob>().ToList();

        Assert.Equal(["digest", "dispatch", "heartbeat", "outbox", "retention"], jobs.Select(j => j.Name).Order());
        Assert.Same(provider.GetRequiredService<DigestJob>(), provider.GetRequiredService<IDigestService>());
        Assert.Same(provider.GetRequiredService<NotificationDispatchJob>(), jobs.OfType<NotificationDispatchJob>().Single());
        Assert.IsType<RetentionJob>(jobs.Single(j => j.Name == "retention"));
    }

    [Fact]
    public void AddWatchdogCore_RegistersTheSharedServices()
    {
        using var provider = Services().AddWatchdogCore().BuildServiceProvider();

        Assert.IsType<WatchdogSignals>(provider.GetRequiredService<IWatchdogSignals>());
        Assert.IsType<HealthRegistry>(provider.GetRequiredService<IHealthRegistry>());
        Assert.IsType<AdMng.Watchdog.Core.Formatting.EventHtmlRenderer>(provider.GetRequiredService<IEventHtmlRenderer>());
        Assert.IsType<AttributionDependentRecheck>(provider.GetRequiredService<IHeldNotificationMatcher>());
        Assert.IsType<LiveDirectoryReports>(provider.GetRequiredService<IDigestDirectoryReports>());
        Assert.NotNull(provider.GetRequiredService<NotificationPlanner>());
        Assert.NotNull(provider.GetRequiredService<FollowUpPlanner>());
        Assert.NotNull(provider.GetRequiredService<RetryPolicy>());
        Assert.Same(provider.GetRequiredService<IWatchdogSignals>(), provider.GetRequiredService<IWatchdogSignals>());
    }

    [Fact]
    public void RegistrationsMadeBefore_Win()
    {
        var matcher = new StubMatcher();
        var services = Services();
        services.AddSingleton<IHeldNotificationMatcher>(matcher);

        using var provider = services.AddWatchdogCore().BuildServiceProvider();

        Assert.Same(matcher, provider.GetRequiredService<IHeldNotificationMatcher>());
    }

    private static ServiceCollection Services()
    {
        var clock = new FakeTimeProvider(NotificationHarness.Now);
        var store = new InMemoryWatchdogStore(clock);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IEventStore>(store);
        services.AddSingleton<INotificationQueueStore>(store);
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IDetectionStore>(store);
        services.AddSingleton<IDigestRunStore>(store);
        services.AddSingleton<IExpiryNoticeStore>(store);
        services.AddSingleton<IStoreMaintenance>(store);
        services.AddSingleton<IWatchdogConfigProvider>(new FakeConfigProvider(NotificationHarness.ConfigWith()));
        services.AddSingleton<IDirectorySessionProvider>(new FakeSessionProvider(null));
        services.AddSingleton<INotificationTransport>(new FakeTransport(DeliveryChannelType.Email));
        return services;
    }

    private sealed class StubMatcher : IHeldNotificationMatcher
    {
        public PlannedMatch? Rematch(WatchEvent watchEvent, RuleSettings rule, WatchdogConfiguration configuration, DateTimeOffset now,
            bool attributionGraceExpired) => null;
    }
}
