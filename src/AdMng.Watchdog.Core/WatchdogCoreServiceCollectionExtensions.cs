using AdMng.Watchdog.Core.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Watchdog.Core;

/// <summary>DI registration of the watchdog engine.</summary>
public static class WatchdogCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers every Core implementation (section 6.2): TimeProvider.System (TryAdd), the jobs as singletons and as
    /// <c>IWatchdogJob</c>, signals, health registry, configuration provider/validator/defaults, rule preview, HTML
    /// renderer, formatters, parsers. It does NOT register stores, transports, <c>IDirectorySessionProvider</c>,
    /// <c>ISecurityEventSource</c> or <c>IDeliveryTester</c> (the service does). Each area registers in its own file
    /// under <c>Registration/</c>, so the detection, rules and notification parts never edit each other's lines.
    /// </summary>
    public static IServiceCollection AddWatchdogCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Shared by all three areas; TryAdd so a host or test that registered a TimeProvider (FakeTimeProvider) keeps it.
        services.TryAddSingleton(TimeProvider.System);
        DetectionRegistration.Add(services);
        RulesRegistration.Add(services);
        NotificationRegistration.Add(services);
        return services;
    }
}
