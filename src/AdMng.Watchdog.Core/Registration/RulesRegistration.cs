using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Watchdog.Core.Registration;

/// <summary>Registrations of Attribution/, Rules/ and Configuration/ (owner W-rules): AttributionEngine,
/// RuleEvaluationJob, the configuration provider/validator/defaults, rule preview and the classes they use.</summary>
/// <remarks>
/// Everything is a singleton and registered with <c>TryAdd</c>, so a registration made before
/// <c>AddWatchdogCore()</c> wins – in particular the extension points whose real implementations belong to the
/// detection and notification areas (<see cref="IDomainControllerCatalog"/>, <see cref="IWatchedAttributeCatalog"/>,
/// <see cref="ISyncAccountCatalog"/>, <see cref="ILockoutPolicySource"/>) get conservative fallbacks here
/// (<see cref="DetectionRegistration"/> runs first and can register adapters). <see cref="IRuleMatchPlanner"/>,
/// <see cref="IAttributionFollowUpPlanner"/> and <see cref="IBusinessHoursCalendar"/> are optional and registered by
/// the notification area. The two jobs are registered once as themselves and once as <see cref="IWatchdogJob"/>.
/// </remarks>
internal static class RulesRegistration
{
    internal static void Add(IServiceCollection services)
    {
        // Shared Core classes implemented before the split (spec 25.6); the static helpers (SecretBindings,
        // TimeZoneResolver) need no registration.
        services.TryAddSingleton<ConfigRedactor>();
        services.TryAddSingleton<ConfigChangeAuditor>();

        AddConfiguration(services);
        AddRules(services);
        AddAttribution(services);
    }

    private static void AddConfiguration(IServiceCollection services)
    {
        services.TryAddSingleton<IDefaultConfigurationFactory, DefaultConfigurationFactory>();
        services.TryAddSingleton<IConfigurationValidator, ConfigurationValidator>();
        services.TryAddSingleton<IWatchdogConfigProvider, WatchdogConfigProvider>();
        services.TryAddSingleton<IConfigTokenSource, DirectoryConfigTokenSource>();
    }

    private static void AddRules(IServiceCollection services)
    {
        services.TryAddSingleton<RuleEngine>();
        services.TryAddSingleton<IRulePreviewService, RulePreviewService>();
        AddJob<RuleEvaluationJob>(services);
    }

    private static void AddAttribution(IServiceCollection services)
    {
        // Extension points with fallbacks (see remarks).
        services.TryAddSingleton<IDomainControllerCatalog, SessionDomainControllerCatalog>();
        services.TryAddSingleton<IWatchedAttributeCatalog, ConfiguredWatchedAttributeCatalog>();
        services.TryAddSingleton<ISyncAccountCatalog, NoSyncAccounts>();
        services.TryAddSingleton<ILockoutPolicySource, DomainLockoutPolicySource>();
        services.TryAddSingleton<IReverseDnsResolver, SystemReverseDnsResolver>();

        services.TryAddSingleton<SecurityEventParser>();
        services.TryAddSingleton<DcLogHealthTracker>();
        services.TryAddSingleton<SourceQueryGate>();
        services.TryAddSingleton<OriginatingDcResolver>();
        services.TryAddSingleton<AttributionCorrelator>();
        services.TryAddSingleton<ActorResolver>();
        services.TryAddSingleton<LogonSourceResolver>();
        services.TryAddSingleton<LockoutSourceResolver>();
        services.TryAddSingleton<UnresolvedReasonResolver>();
        services.TryAddSingleton<PrivilegeAttributionInheritor>();
        services.TryAddSingleton<PendingAttributionProcessor>();
        services.TryAddSingleton<EventLogGapDetector>();
        services.TryAddSingleton<EventLogOnlyDetector>();
        services.TryAddSingleton<SecurityEventReductionFactory>();
        services.TryAddSingleton<SecurityLogIngestor>();
        services.TryAddSingleton<AttributionSourceResolver>();
        services.TryAddSingleton<AttributionDegradedMonitor>();
        services.TryAddSingleton<ActorConditionHealthMonitor>();
        AddJob<AttributionEngine>(services);
    }

    private static void AddJob<TJob>(IServiceCollection services)
        where TJob : class, IWatchdogJob
    {
        if (services.Any(d => d.ServiceType == typeof(TJob)))
        {
            return;
        }

        services.AddSingleton<TJob>();
        services.AddSingleton<IWatchdogJob>(provider => provider.GetRequiredService<TJob>());
    }
}
