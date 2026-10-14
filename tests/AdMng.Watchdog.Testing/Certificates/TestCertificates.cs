using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AdMng.Watchdog.Testing.Certificates;

/// <summary>
/// Self-signed TLS server certificates for the SMTP/HTTP sinks and the API tests, created in memory (never written to
/// disk or a certificate store): ECDSA P-256, CN=localhost, SAN localhost / 127.0.0.1 / ::1, EKU serverAuth. The PFX
/// round trip gives the private key the form SslStream needs on Windows.
/// </summary>
public static class TestCertificates
{
    private static readonly Lazy<X509Certificate2> LazyLocalhost = new(() => Create("localhost"));

    /// <summary>A shared certificate for 127.0.0.1 / localhost (valid from yesterday for 30 days).</summary>
    public static X509Certificate2 Localhost => LazyLocalhost.Value;

    /// <summary>A new self-signed server certificate.</summary>
    /// <param name="commonName">CN and DNS SAN.</param>
    /// <param name="notBefore">Start of validity (default: now − 1 day).</param>
    /// <param name="notAfter">End of validity (default: now + 30 days).</param>
    /// <param name="serverAuth">Include the serverAuth EKU (false = a certificate the API must refuse).</param>
    public static X509Certificate2 Create(string commonName, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null, bool serverAuth = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(commonName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        if (serverAuth)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        }

        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(notBefore ?? now.AddDays(-1), notAfter ?? now.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Hex SHA-256 of the DER certificate (the format of <c>SmtpSettings.PinnedSha256</c> and the client pin).</summary>
    public static string Sha256(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }
}
