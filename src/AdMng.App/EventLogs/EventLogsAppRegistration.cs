using AdMng.Core.Computers.Remote;
using AdMng.Core.EventLogs;
using AdMng.EventLogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.EventLogs;

/// <summary>DI registration of the event log reader (track E) in the WPF application.</summary>
public static class EventLogsAppRegistration
{
    /// <summary>
    /// The Windows reader of event logs (AdMng.EventLogs: MS-EVEN6 with Kerberos and the window's credential, .evtx files) –
    /// application-wide and stateless: every read takes the session of the connection window that asks (rule 4). With the
    /// K1 host address resolver a name that does not resolve is reported as unreachable. The documents and their views are
    /// tabs of the main window (DataTemplates); nothing else is registered here.
    /// </summary>
    public static IServiceCollection AddAdMngEventLogsApp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IEventLogConnector>(sp => new WindowsEventLogConnector(sp.GetService<IHostAddressResolver>()));
        return services;
    }
}
