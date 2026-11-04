using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Csv;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Presentation.Readiness;

/// <summary>DI registration of the readiness check (stage S).</summary>
public static class ReadinessServiceCollectionExtensions
{
    /// <summary>
    /// Per connection window (scoped): the store of the last results (<see cref="IReadinessResults"/>, read by the problem
    /// report), the services of the check and the factory of its tab. Application-wide: the facts of this computer
    /// (<see cref="RuntimeComputerInfo"/> unless the host registered its own – the WPF app reads the registry and the
    /// WebView2 loader). Everything else is reused from the features: SYSVOL through the Group Policy services, the event
    /// logs, the K1 remote client and resolver, the Watchdog connection (only when the host has a Watchdog client) and
    /// classic Outlook; a host without one of them gets "not checked" rows for it.
    /// </summary>
    public static IServiceCollection AddAdMngReadiness(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ILocalComputerInfo, RuntimeComputerInfo>();
        services.TryAddScoped<ReadinessResults>();
        services.TryAddScoped<IReadinessResults>(sp => sp.GetRequiredService<ReadinessResults>());
        services.TryAddScoped(sp => new ReadinessServices(
            sp.GetRequiredService<DialogServices>(),
            sp.GetRequiredService<ReadinessResults>(),
            sp.GetRequiredService<ICsvCodec>(),
            sp.GetRequiredService<WindowsReleaseCatalog>(),
            sp.GetRequiredService<GroupPolicyReader>(),
            sp.GetService<GroupPolicyServices>(),
            sp.GetService<EventLogServices>(),
            sp.GetService<IRemoteComputerClient>(),
            sp.GetService<IHostAddressResolver>(),
            sp.GetService<IWatchdogClient>() is null ? null : sp.GetService<WatchdogConnectionService>(),
            sp.GetService<IOutlookClient>(),
            sp.GetService<ILocalComputerInfo>()));

        // A factory with "new": a transient tab resolved from the scope would be kept alive by it until the window closes.
        services.TryAddScoped<Func<ReadinessDocumentViewModel>>(sp =>
        {
            var readiness = sp.GetRequiredService<ReadinessServices>();
            return () => new ReadinessDocumentViewModel(readiness);
        });
        return services;
    }
}
