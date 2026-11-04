using AdMng.App.Services;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Messaging;
using AdMng.Outlook;
using AdMng.Presentation.Correspondence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.Correspondence;

/// <summary>DI registration of the Korespondencja (U5) part of the WPF app.</summary>
public static class CorrespondenceAppRegistration
{
    /// <summary>
    /// Application-wide and stateless until used (no constructor touches COM or the file system): the Outlook signatures and
    /// the Outlook client (AdMng.Outlook; the client lists the signatures in "Sprawdź Outlooka"), the Outlook transport of
    /// messages to users (U1, over the same client), the loopback browser preview, the DPAPI key protector of the journal
    /// and the platform; per connection window (scoped) the dialogs and the launcher of the window's
    /// <see cref="DialogService"/>; the windows of the view models. Call after <c>AddAdMngPresentation</c>.
    /// </summary>
    public static IServiceCollection AddAdMngCorrespondenceApp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IOutlookSignatureSource>(_ => new OutlookSignatureFiles());
        services.TryAddSingleton<IOutlookClient>(sp => new OutlookClient(sp.GetService<TimeProvider>(), sp.GetService<IOutlookSignatureSource>()));

        // Replace: a default the presentation layer may have registered gives way to the real transport.
        services.Replace(ServiceDescriptor.Singleton<IOutlookUserMessageTransportFactory>(
            sp => new OutlookUserMessageTransportFactory(sp.GetRequiredService<IOutlookClient>())));
        services.TryAddSingleton<IBrowserPreview, LoopbackBrowserPreview>();
        services.TryAddSingleton<ICorrespondenceKeyProtector, DpapiKeyProtector>();
        services.TryAddSingleton<ICorrespondencePlatform, WindowsCorrespondencePlatform>();
        services.AddScoped<ICorrespondenceDialogs>(sp => sp.GetRequiredService<DialogService>());
        services.AddScoped<ICorrespondenceLauncher>(sp => sp.GetRequiredService<DialogService>());
        services.AddWindow<CorrespondenceViewModel, CorrespondenceWindow>();
        services.AddWindow<TextPromptViewModel, TextPromptWindow>();
        services.AddWindow<ReportViewModel, ReportWindow>();
        return services;
    }
}
