using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>The outcome of <see cref="PemCertificateSource.Load"/>: the certificate with its private key, or the reason
/// (one of the <see cref="PemCertificateSource"/> problem constants).</summary>
public sealed record PemLoadResult(X509Certificate2? Certificate, string? Problem);

/// <summary>
/// The Kestrel TLS certificate of the Linux service as PEM files (stage B design 6.3): <c>Hosting.Certificate.Path</c>
/// (chain, leaf first) and <c>KeyPath</c> (unencrypted private key; when not set, the key is read from <c>Path</c>).
/// <see cref="CertificateSelector"/> reloads it every 10 minutes and on file change; a key that does not belong to the
/// certificate (<see cref="KeyMismatch"/> – e.g. between the two renames of a renewal) is never applied.
/// </summary>
public static class PemCertificateSource
{
    /// <summary>The certificate or key file does not exist.</summary>
    public const string Missing = "missing";

    /// <summary>The process may not read a file.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>A file holds no usable PEM certificate or key.</summary>
    public const string Invalid = "invalid";

    /// <summary>Both files are valid, but the key does not belong to the certificate.</summary>
    public const string KeyMismatch = "keyMismatch";

    /// <summary>Loads the leaf certificate of <paramref name="certificatePath"/> with the key of <paramref name="keyPath"/>.</summary>
    public static PemLoadResult Load(string certificatePath, string? keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificatePath);
        try
        {
            return new PemLoadResult(X509Certificate2.CreateFromPemFile(certificatePath, string.IsNullOrWhiteSpace(keyPath) ? null : keyPath), null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new PemLoadResult(null, Missing);
        }
        catch (UnauthorizedAccessException)
        {
            return new PemLoadResult(null, Unreadable);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or IOException)
        {
            return new PemLoadResult(null, Classify(certificatePath, string.IsNullOrWhiteSpace(keyPath) ? certificatePath : keyPath));
        }
    }

    /// <summary>The leaf certificate of <paramref name="certificatePath"/> without a key (the local pin of <c>--health</c>).</summary>
    public static X509Certificate2 LoadCertificateOnly(string certificatePath) => X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath));

    /// <summary>The SHA-256 fingerprint as <c>openssl x509 -fingerprint -sha256</c> prints it (upper-case, colons).</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return string.Join(':', certificate.GetCertHash(HashAlgorithmName.SHA256).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>Why the pair failed: both parts readable on their own means the key belongs to another certificate.</summary>
    private static string Classify(string certificatePath, string keyPath)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath));
            var keyPem = File.ReadAllText(keyPath);
            return IsKey(keyPem) ? KeyMismatch : Invalid;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Invalid;
        }
    }

    private static bool IsKey(string pem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
