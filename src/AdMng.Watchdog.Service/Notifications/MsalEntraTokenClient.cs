using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Identity.Client;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// <see cref="IEntraTokenClient"/> over MSAL confidential clients (spec 15.6–15.7): authority
/// <c>EntraCloudInfo.Authority(cloud, tenant)</c>, a certificate from LocalMachine\My by thumbprint (preferred) or the
/// client secret. One application per (cloud, tenant, client, credential) is kept for the process lifetime so its token
/// cache is reused; MSAL logging stays off (no PII).
/// </summary>
public sealed class MsalEntraTokenClient : IEntraTokenClient
{
    private readonly ICertificateStore _certificates;
    private readonly IMsalHttpClientFactory? _httpClientFactory;
    private readonly ConcurrentDictionary<string, IConfidentialClientApplication> _applications = new(StringComparer.Ordinal);

    public MsalEntraTokenClient(ICertificateStore certificates)
        : this(certificates, httpClientFactory: null)
    {
    }

    /// <summary>Tests: an HTTP client factory that answers the token endpoint.</summary>
    internal MsalEntraTokenClient(ICertificateStore certificates, IMsalHttpClientFactory? httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        _certificates = certificates;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> AcquireTokenAsync(EntraTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var application = _applications.GetOrAdd(CacheKey(request), _ => Build(request));
        var result = await application.AcquireTokenForClient([request.Scope]).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return result.AccessToken;
    }

    private IConfidentialClientApplication Build(EntraTokenRequest request)
    {
        var builder = ConfidentialClientApplicationBuilder.Create(request.ClientId)
            .WithAuthority(EntraCloudInfo.Authority(request.Cloud, request.TenantId));
        if (_httpClientFactory is not null)
        {
            builder = builder.WithHttpClientFactory(_httpClientFactory);
        }

        if (!string.IsNullOrWhiteSpace(request.CertificateThumbprint))
        {
            builder = builder.WithCertificate(FindCertificate(request.CertificateThumbprint));
        }
        else if (request.ClientSecret is { } secret)
        {
            builder = builder.WithClientSecret(secret.Reveal());
        }
        else
        {
            throw new InvalidOperationException("Neither a certificate thumbprint nor a client secret is configured.");
        }

        return builder.Build();
    }

    private X509Certificate2 FindCertificate(string thumbprint)
    {
        var normalized = thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal);
        X509Certificate2? found = null;
        foreach (var certificate in _certificates.GetCertificates(StoreName.My, StoreLocation.LocalMachine))
        {
            if (found is null && string.Equals(certificate.Thumbprint, normalized, StringComparison.OrdinalIgnoreCase) && certificate.HasPrivateKey)
            {
                found = certificate;
            }
            else
            {
                certificate.Dispose();
            }
        }

        return found ?? throw new InvalidOperationException("The OAuth certificate was not found in LocalMachine\\My (or has no accessible private key).");
    }

    /// <summary>Secrets enter the key only as a SHA-256 hash, so a re-entered secret builds a new application.</summary>
    private static string CacheKey(EntraTokenRequest request)
    {
        var credential = request.CertificateThumbprint is { Length: > 0 } thumbprint
            ? "cert:" + thumbprint.ToUpperInvariant()
            : "secret:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.ClientSecret?.Reveal() ?? string.Empty)));
        return string.Join('|', request.Cloud, request.TenantId.ToLowerInvariant(), request.ClientId.ToLowerInvariant(), credential);
    }
}
