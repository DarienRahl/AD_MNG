using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Scans;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Watchdog.Core.Registration;

/// <summary>Registrations of Detection/ and Scans/ (owner W-detect): ChangeDetectionEngine, ScheduledScanJob and the
/// classes they use. The stores, <see cref="IDirectorySessionProvider"/>, <see cref="IWatchdogConfigProvider"/>,
/// <see cref="IHealthRegistry"/>, <see cref="IWatchdogSignals"/> and the optional <see cref="ISecurityEventSource"/>
/// are resolved lazily (registered by the service and the other Core areas). The detection classes are also the
/// attribution area's extension points (<see cref="IDomainControllerCatalog"/>, <see cref="IWatchedAttributeCatalog"/>,
/// <see cref="ISyncAccountCatalog"/>, <see cref="ILockoutPolicySource"/>); the business-day calendar of the scanners is
/// the notification area's <c>BusinessCalendar</c>. Everything is TryAdd, so a host may register its own
/// <see cref="DetectionHostOptions"/> or <see cref="IBusinessDayCalendar"/> first.</summary>
internal static class DetectionRegistration
{
    internal static void Add(IServiceCollection services)
    {
        // Shared state of the detection pipeline.
        services.TryAddSingleton(new DetectionHostOptions());
        services.TryAddSingleton<IWatchdogAccountSource>(NoWatchdogAccount.Instance);
        services.TryAddSingleton<ExclusionSuggestionRegistry>();
        services.TryAddSingleton<SeverityResolver>();
        services.TryAddSingleton<DomainControllerDirectory>();
        services.TryAddSingleton<SyncAccountDetector>();
        services.TryAddSingleton<LockoutPolicyResolver>();
        services.TryAddSingleton<DcStateReader>();
        services.TryAddSingleton<ChangeNotificationWaker>();
        services.TryAddSingleton<IBusinessDayCalendar, ConfiguredBusinessDayCalendar>();
        services.TryAddSingleton<InactiveAccountsReport>();
        AddAttributionAdapters(services);
        services.TryAddSingleton(sp => new PermissionSelfCheck(
            sp.GetRequiredService<IHealthRegistry>(),
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<IWatchdogSignals>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<PermissionSelfCheck>(sp)));

        // Each job once, also when AddWatchdogCore() is called again (the job host would run it twice).
        if (!services.Any(d => d.ServiceType == typeof(ChangeDetectionEngine)))
        {
            services.AddSingleton(CreateEngine);
            services.AddSingleton<IWatchdogJob>(sp => sp.GetRequiredService<ChangeDetectionEngine>());
        }

        if (!services.Any(d => d.ServiceType == typeof(ScheduledScanJob)))
        {
            services.AddSingleton(CreateScanJob);
            services.AddSingleton<IWatchdogJob>(sp => sp.GetRequiredService<ScheduledScanJob>());
        }
    }

    /// <summary>The detection classes as the attribution area's extension points (registered before
    /// <c>RulesRegistration</c>, TryAdd so a test or host registration made earlier wins).</summary>
    private static void AddAttributionAdapters(IServiceCollection services)
    {
        services.TryAddSingleton<SessionDomainControllerCatalog>();
        services.TryAddSingleton<IDomainControllerCatalog, DomainControllerCatalog>();
        services.TryAddSingleton<IWatchedAttributeCatalog, WatchedAttributeCatalog>();
        services.TryAddSingleton<ISyncAccountCatalog>(sp => sp.GetRequiredService<SyncAccountDetector>());
        services.TryAddSingleton<ILockoutPolicySource, LockoutObservationWindowSource>();
    }

    private static ChangeDetectionEngine CreateEngine(IServiceProvider services) => new(
        services.GetRequiredService<IDirectorySessionProvider>(),
        services.GetRequiredService<IWatchdogConfigProvider>(),
        services.GetRequiredService<IHealthRegistry>(),
        services.GetRequiredService<IWatchdogSignals>(),
        Logger<ChangeDetectionEngine>(services),
        new DetectionServices
        {
            Store = services.GetRequiredService<IDetectionStore>(),
            Events = services.GetRequiredService<IEventStore>(),
            Time = services.GetRequiredService<TimeProvider>(),
            DomainControllers = services.GetRequiredService<DomainControllerDirectory>(),
            SyncAccounts = services.GetRequiredService<SyncAccountDetector>(),
            LockoutPolicies = services.GetRequiredService<LockoutPolicyResolver>(),
            Severity = services.GetRequiredService<SeverityResolver>(),
            HostOptions = services.GetRequiredService<DetectionHostOptions>(),
            WatchdogAccount = services.GetRequiredService<IWatchdogAccountSource>(),
        },
        services.GetRequiredService<DcStateReader>(),
        services.GetRequiredService<ChangeNotificationWaker>());

    private static ScheduledScanJob CreateScanJob(IServiceProvider services) => new(
        new ScanJobServices
        {
            Sessions = services.GetRequiredService<IDirectorySessionProvider>(),
            Configuration = services.GetRequiredService<IWatchdogConfigProvider>(),
            Store = services.GetRequiredService<IDetectionStore>(),
            Events = services.GetRequiredService<IEventStore>(),
            Notices = services.GetRequiredService<IExpiryNoticeStore>(),
            Signals = services.GetRequiredService<IWatchdogSignals>(),
            Time = services.GetRequiredService<TimeProvider>(),
            DcStateReader = services.GetRequiredService<DcStateReader>(),
            Severity = services.GetRequiredService<SeverityResolver>(),
            LockoutPolicies = services.GetRequiredService<LockoutPolicyResolver>(),
            Suggestions = services.GetRequiredService<ExclusionSuggestionRegistry>(),
            DomainControllers = services.GetRequiredService<DomainControllerDirectory>(),
            Calendar = services.GetRequiredService<IBusinessDayCalendar>(),
            SelfCheck = services.GetRequiredService<PermissionSelfCheck>(),
            HostOptions = services.GetRequiredService<DetectionHostOptions>(),
            WatchdogAccount = services.GetRequiredService<IWatchdogAccountSource>(),
            SecurityEvents = services.GetService<ISecurityEventSource>(),
        },
        Logger<ScheduledScanJob>(services));

    private static ILogger<T> Logger<T>(IServiceProvider services) =>
        services.GetService<ILogger<T>>() ?? NullLogger<T>.Instance;
}
