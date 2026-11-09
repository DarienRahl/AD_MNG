using AdMng.Core.Pki;

namespace AdMng.Presentation.Pki;

/// <summary>
/// The HTTP check of AIA / CDP / OCSP URLs (docs/dev/p-certificate-services.md 2.5, W5; modelled on
/// <c>PolicyTemplatePackageLoader</c>): only absolute <c>http</c>/<c>https</c> URLs are contacted (<c>file:</c>, UNC and
/// <c>ldap://host</c> are <see cref="PkiHttpOutcome.NotChecked"/> without a connection); default handler
/// <c>SocketsHttpHandler { AllowAutoRedirect = false, Credentials = null, ConnectTimeout = 5 s, SslOptions.CertificateChainPolicy
/// = { DisableCertificateDownloads = true, RevocationMode = NoCheck } }</c> with the system proxy's credentials only; at most 3
/// redirects (each target checked again), 15 s per URL including the body, 20 MB cap, 4 URLs in parallel. AIA bodies parse
/// as a DER or PEM certificate, CDP bodies as a CRL; OCSP is reachability only. Singleton; nothing kept.
/// </summary>
/// <param name="handler">The HTTP handler factory (tests pass a fake; null: the default handler above).</param>
public sealed class PkiHealthProbe(Func<HttpMessageHandler>? handler = null) : IPkiHealthProbe
{
    /// <summary>The handler factory of the tests, null for the default handler.</summary>
    public Func<HttpMessageHandler>? Handler { get; } = handler;

    public Task<IReadOnlyList<PkiHttpCheckResult>> CheckAsync(IReadOnlyList<PkiUrl> urls, IProgress<int>? progress, CancellationToken cancellationToken) =>
        throw new NotImplementedException("P1 W5: PkiHealthProbe.CheckAsync");
}
