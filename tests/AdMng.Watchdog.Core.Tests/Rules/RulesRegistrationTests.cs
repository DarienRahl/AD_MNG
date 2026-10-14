using AdMng.Testing;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Registration;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Scans;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using AdMng.Watchdog.Testing.Attribution;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class RulesRegistrationTests
{
    private static readonly Type[] Singletons =
    [
        typeof(ConfigRedactor), typeof(ConfigChangeAuditor), typeof(IDefaultConfigurationFactory), typeof(IConfigurationValidator),
        typeof(IWatchdogConfigProvider), typeof(IConfigTokenSource), typeof(RuleEngine), typeof(IRulePreviewService), typeof(RuleEvaluationJob),
        typeof(IDomainControllerCatalog), typeof(IWatchedAttributeCatalog), typeof(ISyncAccountCatalog), typeof(ILockoutPolicySource),
        typeof(IReverseDnsResolver), typeof(SecurityEventParser), typeof(DcLogHealthTracker), typeof(SourceQueryGate), typeof(OriginatingDcResolver),
        typeof(PendingAttributionProcessor), typeof(SecurityLogIngestor), typeof(AttributionSourceResolver), typeof(AttributionEngine),
    ];

    /// <summary>The services the host provides (stores, reader, session, logging) and the other areas' shared services.</summary>
    private static IServiceCollection Host(IServiceCollection services)
    {
        var time = new FakeTimeProvider(TestEvents.Now);
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IEventStore, FakeEventStore>();
        services.AddSingleton<ISecurityEventStore, FakeSecurityEventStore>();
        services.AddSingleton<IDetectionStore, FakeDetectionStore>();
        services.AddSingleton<INotificationQueueStore, FakeNotificationQueue>();
        services.AddSingleton<IConfigStore>(new FakeConfigStore(time));
        services.AddSingleton<ISecurityEventSource>(new FakeSecurityEventSource());
        services.AddSingleton<IDirectorySessionProvider>(new FakeSessionProvider(new FakeDirectorySession()));
        services.AddSingleton<IHealthRegistry, FakeHealthRegistry>();
        services.AddSingleton<IWatchdogSignals, FakeSignals>();
        return services;
    }

    /// <summary>The extension points the detection area implements (RulesRegistration alone registers none).</summary>
    private static IServiceCollection DetectionExtensionPoints(IServiceCollection services) => services
        .AddSingleton<IDomainControllerCatalog>(new FakeDomainControllers("dc01.contoso.test"))
        .AddSingleton<IWatchedAttributeCatalog>(new WatchedAttributeCatalog(new FakeConfigProvider(new WatchdogConfiguration())))
        .AddSingleton<ISyncAccountCatalog>(new FakeSyncAccounts())
        .AddSingleton<ILockoutPolicySource>(new FixedLockoutPolicy(TimeSpan.FromMinutes(30)));

    [Fact]
    public void EveryRulesService_Resolves_AndValidatesOnBuild()
    {
        var services = DetectionExtensionPoints(Host(new ServiceCollection()));
        RulesRegistration.Add(services);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        foreach (var type in Singletons)
        {
            Assert.Same(provider.GetRequiredService(type), provider.GetRequiredService(type));
        }

        var jobs = provider.GetServices<IWatchdogJob>().ToList();
        Assert.Equal(["attribution", "rules"], jobs.Select(j => j.Name).Order());
        Assert.Same(provider.GetRequiredService<RuleEvaluationJob>(), jobs.OfType<RuleEvaluationJob>().Single());
        Assert.Same(provider.GetRequiredService<AttributionEngine>(), jobs.OfType<AttributionEngine>().Single());
    }

    /// <summary><see cref="Host"/> plus the stores only the other areas use (the whole of <c>AddWatchdogCore()</c> resolves).</summary>
    private static IServiceCollection FullHost(IServiceCollection services)
    {
        var store = new InMemoryWatchdogStore(new FakeTimeProvider(TestEvents.Now));
        return Host(services).AddSingleton<IOutboxStore>(store).AddSingleton<IExpiryNoticeStore>(store).AddSingleton<IDigestRunStore>(store)
            .AddSingleton<IStoreMaintenance>(store).AddSingleton<ISecretStore>(store);
    }

    [Fact]
    public void AddWatchdogCore_WiresTheExtensionPoints_ToTheDetectionAndNotificationClasses()
    {
        using var provider = FullHost(new ServiceCollection()).AddWatchdogCore().AddWatchdogCore()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.IsType<DomainControllerCatalog>(provider.GetRequiredService<IDomainControllerCatalog>());
        Assert.IsType<WatchedAttributeCatalog>(provider.GetRequiredService<IWatchedAttributeCatalog>());
        Assert.Same(provider.GetRequiredService<SyncAccountDetector>(), provider.GetRequiredService<ISyncAccountCatalog>());
        Assert.IsType<LockoutObservationWindowSource>(provider.GetRequiredService<ILockoutPolicySource>());
        Assert.Same(provider.GetRequiredService<NotificationPlanner>(), Assert.Single(provider.GetServices<IRuleMatchPlanner>()));
        Assert.Same(provider.GetRequiredService<FollowUpPlanner>(), Assert.Single(provider.GetServices<IAttributionFollowUpPlanner>()));
        Assert.Same(provider.GetRequiredService<BusinessCalendarProvider>(), provider.GetRequiredService<IBusinessHoursCalendar>());
        Assert.IsType<RuleEngineHeldMatcher>(provider.GetRequiredService<IHeldNotificationMatcher>());
        Assert.IsType<ConfiguredBusinessDayCalendar>(provider.GetRequiredService<IBusinessDayCalendar>());
    }

    [Fact]
    public void EarlierRegistrations_Win()
    {
        var catalog = new FakeDomainControllers("dc01.contoso.test");
        var services = DetectionExtensionPoints(Host(new ServiceCollection())).AddSingleton<IDomainControllerCatalog>(catalog);
        RulesRegistration.Add(services);
        RulesRegistration.Add(services);
        using var provider = services.BuildServiceProvider();

        Assert.Same(catalog, provider.GetRequiredService<IDomainControllerCatalog>());
        Assert.Equal(2, provider.GetServices<IWatchdogJob>().Count());
    }

    [Fact]
    public void AddWatchdogCore_ProvidesThePublicRulesServices()
    {
        using var provider = FullHost(new ServiceCollection().AddWatchdogCore()).BuildServiceProvider();

        Assert.IsType<WatchdogConfigProvider>(provider.GetRequiredService<IWatchdogConfigProvider>());
        Assert.IsType<ConfigurationValidator>(provider.GetRequiredService<IConfigurationValidator>());
        Assert.IsType<DefaultConfigurationFactory>(provider.GetRequiredService<IDefaultConfigurationFactory>());
        Assert.IsType<RulePreviewService>(provider.GetRequiredService<IRulePreviewService>());
        Assert.NotNull(provider.GetRequiredService<RuleEvaluationJob>());
        Assert.NotNull(provider.GetRequiredService<AttributionEngine>());
    }
}
