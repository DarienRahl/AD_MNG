using AdMng.App.Services;
using AdMng.Core.Computers.Remote;
using AdMng.Presentation.Computers;
using AdMng.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.Computers;

/// <summary>DI registration of the computers (K1) part of the WPF application.</summary>
public static class ComputersAppRegistration
{
    /// <summary>
    /// The AdMng.Remote implementations (CIM over WinRM with the window's credential, the local tool launcher, ping, DNS) –
    /// application-wide and stateless: every call takes the session of the connection window that asks (K1 design note 7)
    /// – and the computer windows (opened through <see cref="ToolWindows"/> and <see cref="DialogService"/>).
    /// </summary>
    public static IServiceCollection AddAdMngComputersApp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRemoteComputerClient>(sp => new CimRemoteComputerClient(sp.GetService<TimeProvider>()));
        services.TryAddSingleton<ILocalToolLauncher, WindowsLocalToolLauncher>();
        services.TryAddSingleton<IPingService, IcmpPingService>();
        services.TryAddSingleton<IHostAddressResolver, DnsHostAddressResolver>();
        services.AddWindow<ComputerReviewViewModel, ComputerReviewWindow>();
        services.AddWindow<RemoteComputerViewModel, RemoteComputerWindow>();
        services.AddWindow<RestartComputerViewModel, RestartComputerWindow>();
        return services;
    }
}
