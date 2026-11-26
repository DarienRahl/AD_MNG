using AdMng.Core.Pki.Tls;
using AdMng.Directory.Pki;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.Pki;

/// <summary>DI registration of AD CS (P1, docs/dev/p-certificate-services.md 2.6) in the WPF application.</summary>
public static class PkiAppRegistration
{
    /// <summary>
    /// The DC LDAPS / GC-LDAPS handshake probe of <c>AdMng.Directory</c> (<see cref="SslStreamTlsProbe"/>: stateless, one for
    /// the application) – a plain <c>AddSingleton</c>: Presentation registers no fallback and consumes the probe through
    /// <c>GetService</c>. Called from <c>AppHost</c> after <c>AddAdMngPresentation()</c>.
    /// </summary>
    public static IServiceCollection AddAdMngPkiApp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IDomainControllerTlsProbe, SslStreamTlsProbe>();
        return services;
    }
}
