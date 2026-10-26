using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdMng.Core.Messaging;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Mail;

/// <summary>
/// Server certificate check of the direct SMTP connection, as in the Watchdog's SMTP channel minus its development-only
/// mode: <see cref="SmtpCertificateCheck.SystemTrust"/> accepts only <see cref="SslPolicyErrors.None"/>;
/// <see cref="SmtpCertificateCheck.PinnedSha256"/> accepts the leaf whose SHA-256 equals the pin. There is no "accept
/// any". Records the hint of a rejection and the SHA-256 of the certificate it saw (for pinning). One instance per
/// connection.
/// </summary>
public sealed class SmtpCertificatePolicy
{
    private readonly SmtpCertificateCheck _mode;
    private readonly string? _pin;

    public SmtpCertificatePolicy(SmtpCertificateCheck mode, string? pinnedSha256)
    {
        _mode = mode;
        _pin = NormalizePin(pinnedSha256);
    }

    /// <summary>The hint of the last rejection, or null.</summary>
    public string? FailureHint { get; private set; }

    /// <summary>SHA-256 (hex) of the last certificate presented by the server.</summary>
    public string? SeenSha256 { get; private set; }

    /// <summary>The callback for <c>SmtpClient.ServerCertificateValidationCallback</c>.</summary>
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        SeenSha256 = certificate is null ? null : Sha256(certificate);
        var accepted = _mode switch
        {
            SmtpCertificateCheck.SystemTrust => errors == SslPolicyErrors.None,
            SmtpCertificateCheck.PinnedSha256 => certificate is not null && _pin is not null
                && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(SeenSha256!), Convert.FromHexString(_pin)),
            _ => false,
        };
        if (!accepted)
        {
            FailureHint = chain?.ChainStatus.Any(s => s.Status is X509ChainStatusFlags.RevocationStatusUnknown or X509ChainStatusFlags.OfflineRevocation) == true
                ? DeliveryHints.RevocationUnavailable
                : DeliveryHints.CertificateUntrusted;
        }

        return accepted;
    }

    /// <summary>Hex SHA-256 of a certificate (the pin format: 64 hex characters, upper case).</summary>
    public static string Sha256(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    }

    /// <summary>The pin in its comparable form (see <see cref="DirectSmtpOptions.NormalizePin"/>).</summary>
    public static string? NormalizePin(string? pin) => DirectSmtpOptions.NormalizePin(pin);
}
