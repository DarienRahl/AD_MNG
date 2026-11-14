using AdMng.Core.Pki;

namespace AdMng.Presentation.Pki;

/// <summary>
/// The HTTP check of AIA / CDP / OCSP URLs (decision 1, docs/dev/p-certificate-services.md 2.5; implementation W5,
/// <c>PkiHealthProbe</c>): only absolute <c>http</c>/<c>https</c> URLs are contacted, no credentials, at most 3 redirects
/// (each target checked again), 15 s per URL including the body, 20 MB cap, 4 in parallel; run only on the operator's
/// button. Bodies are parsed in memory and dropped.
/// </summary>
public interface IPkiHealthProbe
{
    /// <summary>Checks <paramref name="urls"/>; one result per URL in the same order. Reports the number of URLs done.</summary>
    Task<IReadOnlyList<PkiHttpCheckResult>> CheckAsync(IReadOnlyList<PkiUrl> urls, IProgress<int>? progress, CancellationToken cancellationToken);
}
