using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Pki;

/// <summary>
/// The <i>Certyfikaty</i> section of the settings window (P1 decision 2, docs/dev/p-certificate-services.md 2.5; the K1
/// <c>ComputerSettingsViewModel</c> pattern): <i>Próg ostrzeżenia o wygasaniu certyfikatów</i> of end-entity certificates
/// (default 30 days) and the separate threshold of CA certificates (default 180 days), both 1–3650. Only the two numbers are
/// stored (settings, never directory data); the PKI documents re-evaluate their health rows when they change.
/// </summary>
public sealed partial class PkiSettingsViewModel : ObservableObject
{
    private readonly DialogServices _services;

    public PkiSettingsViewModel(DialogServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        var current = services.Settings.Current.Pki;
        CertificateExpiryWarningDays = current.CertificateExpiryWarningDays;
        CaCertificateExpiryWarningDays = current.CaCertificateExpiryWarningDays;
    }

    /// <summary>End-entity certificates (DC LDAPS, key recovery agents, published certificates), in days.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial int CertificateExpiryWarningDays { get; set; }

    /// <summary>CA certificates (renewing a CA has to be planned), in days.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial int CaCertificateExpiryWarningDays { get; set; }

    /// <summary>What the thresholds mean (the CRL rule included).</summary>
    public string Hint => _services.Localizer[L.Pki.SettingsHint];

    /// <summary>Localized range error of the section, or null.</summary>
    public string? Error =>
        Range(L.Pki.SettingsCertificateDays, CertificateExpiryWarningDays)
        ?? Range(L.Pki.SettingsCaDays, CaCertificateExpiryWarningDays);

    /// <summary>Writes the section into <paramref name="settings"/> (the settings window's Save).</summary>
    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Pki.CertificateExpiryWarningDays = CertificateExpiryWarningDays;
        settings.Pki.CaCertificateExpiryWarningDays = CaCertificateExpiryWarningDays;
        settings.Pki.Normalize();
    }

    /// <summary>The two numbers only (settings, not directory data).</summary>
    public override string ToString() => $"{nameof(PkiSettingsViewModel)}({CertificateExpiryWarningDays} d, CA {CaCertificateExpiryWarningDays} d)";

    private string? Range(string labelKey, int value) =>
        value is < PkiSettings.MinWarningDays or > PkiSettings.MaxWarningDays
            ? _services.Localizer.Format(L.Settings.OutOfRange, _services.Localizer[labelKey], PkiSettings.MinWarningDays, PkiSettings.MaxWarningDays)
            : null;
}
