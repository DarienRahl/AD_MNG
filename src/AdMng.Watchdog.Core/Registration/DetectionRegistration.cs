using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Watchdog.Core.Registration;

/// <summary>Registrations of Detection/ and Scans/ (owner W-detect): ChangeDetectionEngine, ScheduledScanJob and the
/// classes they use. The stores, <see cref="IDirectorySessionProvider"/>, <see cref="IWatchdogConfigProvider"/>,
/// <see cref="IHealthRegistry"/> and <see cref="IWatchdogSignals"/> are resolved lazily (registered by the service and
/// the other Core areas).</summary>
internal static class DetectionRegistration
{
    internal static void Add(IServiceCollection services)
    {
        // Shared state of the detection pipeline (the service may register its own DetectionHostOptions first).
        services.TryAddSingleton(new DetectionHostOptions());
        services.TryAddSingleton<ExclusionSuggestionRegistry>();
        services.TryAddSingleton<SeverityResolver>();
        services.TryAddSingleton<DomainControllerDirectory>();
        services.TryAddSingleton<SyncAccountDetector>();
        services.TryAddSingleton<LockoutPolicyResolver>();
        services.TryAddSingleton<DcStateReader>();
        services.TryAddSingleton<ChangeNotificationWaker>();

        services.TryAddSingleton(CreateEngine);
        services.AddSingleton<IWatchdogJob>(sp => sp.GetRequiredService<ChangeDetectionEngine>());
    }

    private static ChangeDetectionEngine CreateEngine(IServiceProvider services) => new(
        services.GetRequiredService<IDirectorySessionProvider>(),
        services.GetRequiredService<IWatchdogConfigProvider>(),
        services.GetRequiredService<IHealthRegistry>(),
        services.GetRequiredService<IWatchdogSignals>(),
        Logger<ChangeDetectionEngine>(services),
        CreateDetectionServices(services),
        services.GetRequiredService<DcStateReader>(),
        services.GetRequiredService<ChangeNotificationWaker>());

    private static DetectionServices CreateDetectionServices(IServiceProvider services) => new()
    {
        Store = services.GetRequiredService<IDetectionStore>(),
        Events = services.GetRequiredService<IEventStore>(),
        Time = services.GetRequiredService<TimeProvider>(),
        DomainControllers = services.GetRequiredService<DomainControllerDirectory>(),
        SyncAccounts = services.GetRequiredService<SyncAccountDetector>(),
        LockoutPolicies = services.GetRequiredService<LockoutPolicyResolver>(),
        Severity = services.GetRequiredService<SeverityResolver>(),
        HostOptions = services.GetRequiredService<DetectionHostOptions>(),
    };

    private static ILogger<T> Logger<T>(IServiceProvider services) =>
        services.GetService<ILogger<T>>() ?? NullLogger<T>.Instance;
}
