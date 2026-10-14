using AdMng.Watchdog.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Watchdog.Service.Hosting.Fallbacks;

/// <summary>
/// Conservative implementations of Core services the host cannot run without, registered with <c>TryAdd</c> AFTER
/// <c>AddWatchdogCore()</c>: whatever Core registers wins; these fill the gaps of a build whose Core areas are not
/// complete (and keep <c>--smoke-test</c> meaningful there). Services that only some endpoints need (rule preview, digest,
/// HTML renderer) have no fallback – those endpoints answer 503 <c>unavailable</c>.
/// </summary>
public static class CoreFallbacks
{
    public static void Add(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IWatchdogSignals, FallbackWatchdogSignals>();
        services.TryAddSingleton<IHealthRegistry, FallbackHealthRegistry>();
        services.TryAddSingleton<IDefaultConfigurationFactory, FallbackDefaultConfigurationFactory>();
        services.TryAddSingleton<IConfigurationValidator, FallbackConfigurationValidator>();
        services.TryAddSingleton<IWatchdogConfigProvider, StoreConfigProvider>();
    }
}
