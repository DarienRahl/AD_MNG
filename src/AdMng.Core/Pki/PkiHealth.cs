using System.Globalization;

namespace AdMng.Core.Pki;

/// <summary>The expiry thresholds of the health rules (decision 2; from the settings, never directory data).</summary>
/// <param name="EndEntityWarningDays">End-entity certificates (DC LDAPS, KRA, published): default 30.</param>
/// <param name="CaWarningDays">CA certificates: default 180.</param>
public sealed record PkiHealthThresholds(int EndEntityWarningDays, int CaWarningDays)
{
    /// <summary>30 and 180 days.</summary>
    public static PkiHealthThresholds Default { get; } = new(30, 180);

    /// <summary>The two numbers (settings, not directory data).</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiHealthThresholds)}({EndEntityWarningDays} d, CA {CaWarningDays} d)");
}

/// <summary>The state of a health row (<i>Stan: OK / Wygasa / Wygasła / Niedostępna / Nieznany</i>).</summary>
public enum PkiHealthStatus
{
    Ok,
    Expiring,
    Expired,
    Unavailable,
    Unknown,
}

/// <summary>What a health row is about (<c>Pki.HealthItem.&lt;PkiHealthItem&gt;</c>).</summary>
public enum PkiHealthItem
{
    CaCertificate,
    AiaLocation,
    CdpLocation,
    BaseCrl,
    DeltaCrl,
    KraCertificate,
    NtAuthCertificate,
}

/// <summary>One row of the <i>pkiview</i>-style health tab.</summary>
/// <param name="CaGuid">The enterprise CA the row belongs to (null for KRA and NTAuth rows).</param>
/// <param name="Item">What it is about.</param>
/// <param name="Index">The position among rows of the same item (the n-th AIA URL …).</param>
/// <param name="Status">Its state at evaluation time.</param>
/// <param name="ValidUntil"><c>NotAfter</c> / <c>NextUpdate</c>, null when unknown.</param>
/// <param name="Url">The URL of a location row.</param>
public sealed record PkiHealthRow(Guid? CaGuid, PkiHealthItem Item, int Index, PkiHealthStatus Status, DateTimeOffset? ValidUntil, string? Url)
{
    /// <summary>Item, index, status and the CA GUID only (never the URL).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Item}[{Index}] {Status}{(CaGuid is { } ca ? " " + ca.ToString("B") : string.Empty)}");
}

/// <summary>
/// The health rules (docs/dev/p-certificate-services.md 2.4): pure, called by the Presentation at display time with the
/// current thresholds and clock – never cached with the snapshot.
/// </summary>
public static class PkiHealth
{
    /// <summary>The rows of every enterprise CA (certificate, AIA and CDP URLs, base and delta CRL) and of the KRA and NTAuth certificates.</summary>
    public static IReadOnlyList<PkiHealthRow> Evaluate(PkiSnapshot snapshot, PkiHealthThresholds thresholds, DateTimeOffset now, IReadOnlyList<PkiHttpCheckResult>? http) =>
        throw new NotImplementedException("P1 C4: PkiHealth.Evaluate");

    /// <summary>The <c>http</c>/<c>https</c> URLs (AIA CA Issuers, CDP, delta CDP, OCSP) of the snapshot's certificates for the HTTP check.</summary>
    public static IReadOnlyList<PkiUrl> HttpUrls(PkiSnapshot snapshot) => throw new NotImplementedException("P1 C4: PkiHealth.HttpUrls");
}
