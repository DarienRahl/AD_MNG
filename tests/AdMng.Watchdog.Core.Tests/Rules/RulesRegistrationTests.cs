using AdMng.Testing;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Registration;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using AdMng.Watchdog.Testing.Attribution;
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

    [Fact]
    public void EveryRulesService_Resolves_AndValidatesOnBuild()
    {
        var services = Host(new ServiceCollection());
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

    [Fact]
    public void TheFallbacks_AreUsedWithoutOtherRegistrations()
    {
        var services = Host(new ServiceCollection());
        RulesRegistration.Add(services);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SessionDomainControllerCatalog>(provider.GetRequiredService<IDomainControllerCatalog>());
        Assert.IsType<ConfiguredWatchedAttributeCatalog>(provider.GetRequiredService<IWatchedAttributeCatalog>());
        Assert.IsType<NoSyncAccounts>(provider.GetRequiredService<ISyncAccountCatalog>());
        Assert.IsType<DomainLockoutPolicySource>(provider.GetRequiredService<ILockoutPolicySource>());
    }

    [Fact]
    public void EarlierRegistrations_Win()
    {
        var catalog = new FakeDomainControllers("dc01.contoso.test");
        var services = Host(new ServiceCollection()).AddSingleton<IDomainControllerCatalog>(catalog);
        RulesRegistration.Add(services);
        RulesRegistration.Add(services);
        using var provider = services.BuildServiceProvider();

        Assert.Same(catalog, provider.GetRequiredService<IDomainControllerCatalog>());
        Assert.Equal(2, provider.GetServices<IWatchdogJob>().Count());
    }

    [Fact]
    public void AddWatchdogCore_ProvidesThePublicRulesServices()
    {
        using var provider = Host(new ServiceCollection().AddWatchdogCore()).BuildServiceProvider();

        Assert.IsType<WatchdogConfigProvider>(provider.GetRequiredService<IWatchdogConfigProvider>());
        Assert.IsType<ConfigurationValidator>(provider.GetRequiredService<IConfigurationValidator>());
        Assert.IsType<DefaultConfigurationFactory>(provider.GetRequiredService<IDefaultConfigurationFactory>());
        Assert.IsType<RulePreviewService>(provider.GetRequiredService<IRulePreviewService>());
        Assert.NotNull(provider.GetRequiredService<RuleEvaluationJob>());
        Assert.NotNull(provider.GetRequiredService<AttributionEngine>());
    }
}
