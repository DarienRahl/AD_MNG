using AdMng.Core.Computers.Remote;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Watchdog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Presentation.EventLogs;

/// <summary>DI registration of the event log documents (track E).</summary>
public static class EventLogsServiceCollectionExtensions
{
    /// <summary>
    /// The launcher and the services of the documents per connection window (scoped) and the reverse name lookup
    /// (application-wide). The reader itself (<see cref="IEventLogConnector"/>) comes from the host – the WPF app registers
    /// <c>AdMng.EventLogs.WindowsEventLogConnector</c>; without it the documents show the "not supported" banner.
    /// </summary>
    public static IServiceCollection AddAdMngEventLogs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IHostNameLookup, DnsHostNameLookup>();
        services.TryAddScoped<EventLogLauncher>();
        services.TryAddScoped(sp => new EventLogServices(
            sp.GetRequiredService<DialogServices>(),
            sp.GetRequiredService<ICsvCodec>(),
            sp.GetRequiredService<EventLogLauncher>(),
            sp.GetService<IEventLogConnector>(),
            sp.GetService<IDirectoryConnector>(),
            sp.GetService<GroupPolicyServices>(),
            sp.GetService<ComputerActions>(),
            sp.GetService<IRemoteComputerClient>(),
            sp.GetService<IHostAddressResolver>(),
            sp.GetService<IHostNameLookup>(),
            sp.GetService<WatchdogObjectHistoryLauncher>()));
        return services;
    }
}
