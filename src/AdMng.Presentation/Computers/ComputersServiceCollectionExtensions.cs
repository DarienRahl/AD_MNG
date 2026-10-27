using AdMng.Core.Batch;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Presentation.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Presentation.Computers;

/// <summary>DI registration of the computers (K1) presentation services.</summary>
public static class ComputersServiceCollectionExtensions
{
    /// <summary>
    /// The release catalogue (singleton, static data), the computer actions and the view model factory (per connection
    /// window, scoped). The remote client, the resolver, ping and the tool launcher come from the host (the WPF app
    /// registers the AdMng.Remote implementations); without them their actions are not offered.
    /// </summary>
    public static IServiceCollection AddAdMngComputers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // A factory, not the type: the container would pick the IEnumerable<WindowsRelease> constructor and give it an
        // empty sequence (no release known, nothing past its end of support).
        services.TryAddSingleton(_ => new WindowsReleaseCatalog());
        services.TryAddScoped(sp => new ComputerActions(
            sp.GetRequiredService<DialogServices>(),
            sp.GetRequiredService<IChangePlanner>(),
            sp.GetService<ILocalToolLauncher>(),
            sp.GetService<IPingService>(),
            sp.GetService<IHostAddressResolver>()));
        services.TryAddScoped<ComputerViewModelFactory>();
        return services;
    }
}
