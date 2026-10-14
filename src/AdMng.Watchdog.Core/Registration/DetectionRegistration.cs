using AdMng.Watchdog.Core.Abstractions;
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
/// are resolved lazily (registered by the service and the other Core areas). Everything is TryAdd, so a host may
/// register its own <see cref="DetectionHostOptions"/> or <see cref="IBusinessDayCalendar"/> first.</summary>
internal static class DetectionRegistration
{
    internal static void Add(IServiceCollection services)
    {
        // Shared state of the detection pipeline.
        services.TryAddSingleton(new DetectionHostOptions());
        services.TryAddSingleton<ExclusionSuggestionRegistry>();
        services.TryAddSingleton<SeverityResolver>();
        services.TryAddSingleton<DomainControllerDirectory>();
        services.TryAddSingleton<SyncAccountDetector>();
        services.TryAddSingleton<LockoutPolicyResolver>();
        services.TryAddSingleton<DcStateReader>();
        services.TryAddSingleton<ChangeNotificationWaker>();
        services.TryAddSingleton<IBusinessDayCalendar, ConfiguredBusinessDayCalendar>();
        services.TryAddSingleton<InactiveAccountsReport>();
        services.TryAddSingleton(sp => new PermissionSelfCheck(
            sp.GetRequiredService<IHealthRegistry>(),
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<IWatchdogSignals>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<PermissionSelfCheck>(sp)));

        services.TryAddSingleton(CreateEngine);
        services.AddSingleton<IWatchdogJob>(sp => sp.GetRequiredService<ChangeDetectionEngine>());
        services.TryAddSingleton(CreateScanJob);
        services.AddSingleton<IWatchdogJob>(sp => sp.GetRequiredService<ScheduledScanJob>());
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
            SecurityEvents = services.GetService<ISecurityEventSource>(),
        },
        Logger<ScheduledScanJob>(services));

    private static ILogger<T> Logger<T>(IServiceProvider services) =>
        services.GetService<ILogger<T>>() ?? NullLogger<T>.Instance;
}
