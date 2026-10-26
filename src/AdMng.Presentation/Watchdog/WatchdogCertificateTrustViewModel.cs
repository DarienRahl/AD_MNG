using System.Text;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Client;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// "Potwierdź certyfikat usługi Watchdog" (stage B design 18 item 1, modal): the service presented a certificate of no
/// trusted authority (<see cref="WatchdogClientErrorKind.CertificateUntrusted"/>) or one other than the pinned
/// (<see cref="WatchdogClientErrorKind.CertificateChanged"/>). Shows the SHA-256 fingerprint in groups of four hex digits
/// to compare with <c>sudo admng-watchdog cert-fingerprint</c> on the server, the subject, the DNS names and the validity;
/// for a changed certificate the old and the new fingerprint. "Ufam temu certyfikatowi" pins it (the caller stores
/// <see cref="Certificate"/>'s fingerprint in the settings), "Anuluj" leaves everything as it is – nothing is pinned
/// without this explicit confirmation.
/// </summary>
public sealed class WatchdogCertificateTrustViewModel : ObservableObject
{
    public WatchdogCertificateTrustViewModel(ILocalizer localizer, WatchdogServerCertificate certificate, string? previousSha256, string host, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(zone);
        Certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        PreviousSha256 = WatchdogServerCertificate.NormalizeSha256(previousSha256);
        Title = localizer[L.WatchdogLinux.CertificateTitle];
        Message = localizer[IsChanged ? L.WatchdogLinux.CertificateChanged : L.WatchdogLinux.CertificateUntrusted];
        CompareText = localizer[L.WatchdogLinux.CertificateCompare];
        HostText = host ?? string.Empty;
        Fingerprint = FormatFingerprint(certificate.Sha256);
        PreviousFingerprint = PreviousSha256 is null ? string.Empty : FormatFingerprint(PreviousSha256);
        Subject = certificate.Subject;
        DnsNames = string.Join(", ", certificate.DnsNames);
        Validity = localizer.Format(L.WatchdogLinux.CertificateValidity, localizer.FormatTime(certificate.NotBefore, zone), localizer.FormatTime(certificate.NotAfter, zone));
        IsOutsideValidity = now < certificate.NotBefore || now > certificate.NotAfter;
        ValidityWarning = IsOutsideValidity ? localizer[L.WatchdogLinux.CertificateOutsideValidity] : string.Empty;
    }

    /// <summary>The certificate the service presented (its fingerprint is pinned when the operator trusts it).</summary>
    public WatchdogServerCertificate Certificate { get; }

    /// <summary>The pinned fingerprint the certificate differs from (64 hex digits), or null on a first connection.</summary>
    public string? PreviousSha256 { get; }

    /// <summary>The service presented another certificate than the pinned one (old and new fingerprint in red).</summary>
    public bool IsChanged => PreviousSha256 is not null;

    public string Title { get; }

    /// <summary>Why the confirmation is needed (not trusted / changed since it was pinned).</summary>
    public string Message { get; }

    /// <summary>„Porównaj z wynikiem sudo admng-watchdog cert-fingerprint na serwerze”.</summary>
    public string CompareText { get; }

    /// <summary>"admng-wd.contoso.com:8443".</summary>
    public string HostText { get; }

    /// <summary>"3F2A 9C1E 0B7D …" – 16 groups of four hex digits on two lines.</summary>
    public string Fingerprint { get; }

    /// <summary>The pinned fingerprint, formatted like <see cref="Fingerprint"/>; empty on a first connection.</summary>
    public string PreviousFingerprint { get; }

    public string Subject { get; }

    /// <summary>"admng-wd.contoso.com, admng-wd".</summary>
    public string DnsNames { get; }

    /// <summary>"Ważny od 01.10.2026 10:00 do 01.10.2028 10:00".</summary>
    public string Validity { get; }

    public bool IsOutsideValidity { get; }

    public string ValidityWarning { get; }

    /// <summary>
    /// A SHA-256 fingerprint in groups of four hex digits, eight groups per line ("3F2A 9C1E … 2C4F 6B8D"); a value that
    /// is not 64 hex digits is shown in groups as far as it goes.
    /// </summary>
    public static string FormatFingerprint(string? sha256)
    {
        var hex = WatchdogServerCertificate.NormalizeSha256(sha256)
                  ?? new string([.. (sha256 ?? string.Empty).Where(char.IsAsciiHexDigit)]).ToUpperInvariant();
        var text = new StringBuilder(hex.Length * 5 / 4);
        for (var i = 0; i < hex.Length; i += 4)
        {
            if (i > 0)
            {
                text.Append(i % 32 == 0 ? '\n' : ' ');
            }

            text.Append(hex.AsSpan(i, Math.Min(4, hex.Length - i)));
        }

        return text.ToString();
    }
}
