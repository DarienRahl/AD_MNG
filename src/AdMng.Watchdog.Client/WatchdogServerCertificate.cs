using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AdMng.Watchdog.Client;

/// <summary>
/// The leaf certificate a watchdog service presented when the TLS handshake was refused because it does not chain to a
/// trusted root and is not (or no longer) pinned (stage B design 18 item 1): what the operator compares with
/// <c>sudo admng-watchdog cert-fingerprint</c> on the server before trusting it. Public certificate data only.
/// </summary>
public sealed record WatchdogServerCertificate
{
    /// <summary>SHA-256 of the DER encoding, 64 upper-case hex digits without separators.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Subject distinguished name, e.g. <c>CN=admng-wd.contoso.com</c>.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>DNS names of the subject alternative name extension.</summary>
    public IReadOnlyList<string> DnsNames { get; init; } = [];

    public DateTimeOffset NotBefore { get; init; }

    public DateTimeOffset NotAfter { get; init; }

    /// <summary>Reads the public data of <paramref name="certificate"/>.</summary>
    public static WatchdogServerCertificate From(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var raw = certificate.GetRawCertData();
        using var owned = certificate is X509Certificate2 ? null : X509CertificateLoader.LoadCertificate(raw);
        var leaf = certificate as X509Certificate2 ?? owned!;
        var names = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
        return new WatchdogServerCertificate
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(raw)),
            Subject = leaf.Subject,
            DnsNames = names,
            NotBefore = new DateTimeOffset(leaf.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            NotAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero),
        };
    }

    /// <summary>
    /// The hex digits of a SHA-256 fingerprint typed or printed in any common form (<c>AB:CD:…</c>, <c>abcd ef01 …</c>),
    /// upper case without separators; null when it is not 64 hex digits.
    /// </summary>
    public static string? NormalizeSha256(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        var hex = new string([.. fingerprint.Where(char.IsAsciiHexDigit)]).ToUpperInvariant();
        return hex.Length == 64 ? hex : null;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"WatchdogServerCertificate({Subject}, {Sha256})");
}

/// <summary>
/// The verdict of the most recent TLS handshake of one handler (thread-safe): the presented leaf when it was refused
/// only for its chain (no pin, or another pin), else nothing. <see cref="WatchdogHttpClient"/> reads it when a request
/// fails with an <see cref="System.Security.Authentication.AuthenticationException"/>.
/// </summary>
public sealed class WatchdogCertificateCapture
{
    private WatchdogServerCertificate? _rejected;

    /// <summary>The leaf of the last handshake refused for its chain only, or null (accepted, or refused for another reason).</summary>
    public WatchdogServerCertificate? Rejected => Volatile.Read(ref _rejected);

    internal void Set(WatchdogServerCertificate? rejected) => Volatile.Write(ref _rejected, rejected);
}
