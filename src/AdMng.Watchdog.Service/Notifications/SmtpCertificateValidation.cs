using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// Server certificate validation of the SMTP connection (spec 15.6): <see cref="CertificateValidationMode.System"/>
/// accepts only <see cref="SslPolicyErrors.None"/>; <see cref="CertificateValidationMode.PinnedSha256"/> accepts the leaf
/// whose SHA-256 equals the pin; <see cref="CertificateValidationMode.AcceptAnyDevelopmentOnly"/> accepts anything only
/// in the Development environment (rejects everything otherwise). The instance records why it rejected the server so
/// the failure can carry a hint (<see cref="DeliveryHints.CertificateUntrusted"/> /
/// <see cref="DeliveryHints.RevocationUnavailable"/>). One instance per connection.
/// </summary>
public sealed class SmtpCertificateValidation
{
    private readonly CertificateValidationMode _mode;
    private readonly string? _pin;
    private readonly bool _isDevelopment;

    public SmtpCertificateValidation(CertificateValidationMode mode, string? pinnedSha256, bool isDevelopment)
    {
        _mode = mode;
        _pin = NormalizePin(pinnedSha256);
        _isDevelopment = isDevelopment;
    }

    /// <summary>The hint of the last rejection, or null when nothing was rejected.</summary>
    public string? FailureHint { get; private set; }

    /// <summary>The callback for <c>SmtpClient.ServerCertificateValidationCallback</c>.</summary>
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var accepted = _mode switch
        {
            CertificateValidationMode.System => errors == SslPolicyErrors.None,
            CertificateValidationMode.PinnedSha256 => certificate is not null && _pin is not null && MatchesPin(certificate, _pin),
            CertificateValidationMode.AcceptAnyDevelopmentOnly => _isDevelopment,
            _ => false,
        };
        if (!accepted)
        {
            FailureHint = IsRevocationProblem(chain) ? DeliveryHints.RevocationUnavailable : DeliveryHints.CertificateUntrusted;
        }

        return accepted;
    }

    /// <summary>Hex SHA-256 of the certificate (the pin format: 64 hex characters).</summary>
    public static string Sha256(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    }

    private static bool MatchesPin(X509Certificate certificate, string pin) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Sha256(certificate)), Convert.FromHexString(pin));

    private static string? NormalizePin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return null;
        }

        var hex = new string([.. pin.Where(char.IsAsciiHexDigit)]);
        return hex.Length == 64 ? hex.ToUpperInvariant() : null;
    }

    private static bool IsRevocationProblem(X509Chain? chain) =>
        chain?.ChainStatus.Any(s => s.Status is X509ChainStatusFlags.RevocationStatusUnknown or X509ChainStatusFlags.OfflineRevocation) == true;
}
