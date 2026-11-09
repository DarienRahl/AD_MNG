using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.Pki;

/// <summary>DI registration of AD CS (P1, docs/dev/p-certificate-services.md 2.6) in the WPF application.</summary>
public static class PkiAppRegistration
{
    /// <summary>
    /// W7: <c>services.AddSingleton&lt;IDomainControllerTlsProbe, SslStreamTlsProbe&gt;()</c> (a plain <c>AddSingleton</c>:
    /// Presentation registers no fallback and consumes the probe through <c>GetService</c>). Called from <c>AppHost</c> after
    /// <c>AddAdMngPresentation()</c>. The contract stub registers nothing (the LDAPS tab then says <i>niedostępne</i>).
    /// </summary>
    public static IServiceCollection AddAdMngPkiApp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
