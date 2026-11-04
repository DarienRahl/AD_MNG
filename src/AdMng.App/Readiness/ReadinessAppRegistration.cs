using AdMng.App.Services;
using AdMng.Presentation.Readiness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.Readiness;

/// <summary>DI registration of the readiness check ("Gotowość środowiska", stage S) in the WPF application.</summary>
public static class ReadinessAppRegistration
{
    /// <summary>
    /// The facts of this computer on Windows (<see cref="WindowsComputerInfo"/>: registry and the WebView2 probe of
    /// <see cref="WebView2Host"/>), application-wide, replacing the runtime default of the presentation layer. The document
    /// and its view are a tab of the main window (DataTemplates); its services are registered by the presentation layer.
    /// Call after <c>AddAdMngPresentation</c> and the registration of <see cref="WebView2Host"/>.
    /// </summary>
    public static IServiceCollection AddAdMngReadinessApp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<ILocalComputerInfo>(sp => new WindowsComputerInfo(sp.GetRequiredService<WebView2Host>())));
        return services;
    }
}
