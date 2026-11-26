using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Presentation.PowerShell;

/// <summary>DI registration of the PowerShell window's presentation services (track R, stage R1).</summary>
public static class PowerShellServiceCollectionExtensions
{
    /// <summary>
    /// The services of a PowerShell window, per connection window (scoped): <see cref="PowerShellServices"/>, built from what the
    /// host registered - the <see cref="IPowerShellSessionFactory"/> over the engine, the <see cref="IPowerShellWindowActions"/>
    /// of the grid, the object dialog and the export, and the <see cref="IPowerShellPolicyReader"/> (or, without one, the
    /// default reader over the host's <see cref="IPowerShellPolicySource"/>). A host that registers none still gets the services:
    /// the window then says that this installation has no engine. The window itself is created by
    /// <see cref="Dialogs.IDialogViewModelFactory.CreatePowerShell"/>.
    /// </summary>
    public static IServiceCollection AddAdMngPowerShell(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped(provider => new PowerShellServices(
            provider.GetService<IPowerShellSessionFactory>(),
            provider.GetService<IPowerShellPolicyReader>()
                ?? (provider.GetService<IPowerShellPolicySource>() is { } source ? new PowerShellPolicyReader(source) : null),
            provider.GetService<IPowerShellWindowActions>()));
        return services;
    }
}
