using AdMng.Core.Csv;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.Presentation.Pki;

/// <summary>DI registration of the AD CS (P1) presentation services.</summary>
public static class PkiServiceCollectionExtensions
{
    /// <summary>
    /// The window's snapshot service, PKI services and document factory (scoped: the session, dialogs and shell of one
    /// connection window) and the HTTP check (singleton: stateless). The DC LDAPS probe comes from the host
    /// (<c>AddAdMngPkiApp()</c> registers <c>SslStreamTlsProbe</c>; Presentation registers no fallback) and is consumed
    /// through <c>GetService</c>, so without it the LDAPS tab says <i>niedostępne</i>.
    /// </summary>
    public static IServiceCollection AddAdMngPki(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPkiHealthProbe, PkiHealthProbe>();
        services.TryAddScoped(sp => new PkiSnapshotService(
            sp.GetRequiredService<DialogServices>(),
            sp.GetService<IDomainControllerTlsProbe>(),
            sp.GetService<IPkiHealthProbe>()));
        services.TryAddScoped<IPkiSnapshotService>(sp => sp.GetRequiredService<PkiSnapshotService>());
        services.TryAddScoped(sp => new PkiServices(
            sp.GetRequiredService<DialogServices>(),
            sp.GetRequiredService<ICsvCodec>(),
            sp.GetRequiredService<IPkiSnapshotService>(),
            sp.GetService<IDomainControllerTlsProbe>(),
            sp.GetService<IPkiHealthProbe>()));
        services.TryAddScoped(sp => new PkiViewModelFactory(sp.GetRequiredService<PkiServices>()));
        return services;
    }
}
