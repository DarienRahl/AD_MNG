namespace AdMng.Presentation.Settings;

/// <summary>
/// AD CS (P1, docs/dev/p-certificate-services.md decision 2 and 2.5): the tree visibility and the two expiry warning
/// thresholds. Settings only – never PKI data (rule 1).
/// </summary>
public sealed class PkiSettings
{
    /// <summary>Smallest threshold in days.</summary>
    public const int MinWarningDays = 1;

    /// <summary>Largest threshold in days.</summary>
    public const int MaxWarningDays = 3650;

    /// <summary>"Widok → Usługi kluczy publicznych w drzewie": the PKI node under the domain (default on).</summary>
    public bool ShowInTree { get; set; } = true;

    /// <summary><i>Próg ostrzeżenia o wygasaniu certyfikatów</i> for end-entity certificates (DC LDAPS, KRA, published): 30 days.</summary>
    public int CertificateExpiryWarningDays { get; set; } = 30;

    /// <summary>The threshold of CA certificates (renewing a CA has to be planned): 180 days.</summary>
    public int CaCertificateExpiryWarningDays { get; set; } = 180;

    /// <summary>Repairs a hand-edited or older settings file: both thresholds clamped to 1–3650 days.</summary>
    public void Normalize()
    {
        CertificateExpiryWarningDays = Math.Clamp(CertificateExpiryWarningDays, MinWarningDays, MaxWarningDays);
        CaCertificateExpiryWarningDays = Math.Clamp(CaCertificateExpiryWarningDays, MinWarningDays, MaxWarningDays);
    }

    /// <summary>The thresholds as the Core health rules take them.</summary>
    public Core.Pki.PkiHealthThresholds Thresholds() => new(CertificateExpiryWarningDays, CaCertificateExpiryWarningDays);
}
