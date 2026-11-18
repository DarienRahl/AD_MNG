using System.Globalization;
using AdMng.Core.Ldap;
using AdMng.Core.Pki;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Pki;

/// <summary>One label/value line of a details pane (texts only; built when shown, never stored).</summary>
/// <param name="Label">The localized label.</param>
/// <param name="Value">The value as shown.</param>
public sealed record PkiDetailLine(string Label, string Value)
{
    /// <summary>The type only (the value is directory data, the label may quote it).</summary>
    public override string ToString() => nameof(PkiDetailLine);
}

/// <summary>
/// The display rules the PKI documents share (docs/dev/p-certificate-services.md 2.5): dates in the UI culture, the state of
/// a certificate or CRL at the current thresholds and clock (decision 2 – computed when shown), the days left, the names of
/// purposes, periods in the largest whole unit with the Polish plural forms, the leaf name of a DN.
/// </summary>
internal static class PkiDisplay
{
    private static readonly TimeSpan MinimumCrlMargin = TimeSpan.FromDays(1);

    /// <summary>A date (and time) in the UI culture, local time.</summary>
    public static string Date(ILocalizer localizer, DateTimeOffset? value) =>
        value is { } date ? date.ToLocalTime().ToString("g", localizer.Culture) : localizer[L.Pki.None];

    /// <summary>A date only, local time.</summary>
    public static string Day(ILocalizer localizer, DateTimeOffset? value) =>
        value is { } date ? date.ToLocalTime().ToString("d", localizer.Culture) : localizer[L.Pki.None];

    /// <summary>The state of a certificate (decision 2): expired after <c>NotAfter</c>, expiring at most the threshold before, unknown before <c>NotBefore</c>.</summary>
    public static PkiHealthStatus CertificateStatus(CertificateSummary? summary, bool isCa, PkiHealthThresholds thresholds, DateTimeOffset now)
    {
        if (summary is null)
        {
            return PkiHealthStatus.Unavailable;
        }

        if (now < summary.NotBefore)
        {
            return PkiHealthStatus.Unknown;
        }

        if (now > summary.NotAfter)
        {
            return PkiHealthStatus.Expired;
        }

        var days = isCa ? thresholds.CaWarningDays : thresholds.EndEntityWarningDays;
        return summary.NotAfter - now <= TimeSpan.FromDays(days) ? PkiHealthStatus.Expiring : PkiHealthStatus.Ok;
    }

    /// <summary>The state of a CRL (decision 2): expiring when less than <c>max(1 day, 20 %)</c> is left; unknown without <c>NextUpdate</c>.</summary>
    public static PkiHealthStatus CrlStatus(CrlSummary? summary, DateTimeOffset now)
    {
        if (summary is null)
        {
            return PkiHealthStatus.Unavailable;
        }

        if (summary.NextUpdate is not { } next || now < summary.ThisUpdate)
        {
            return PkiHealthStatus.Unknown;
        }

        if (now > next)
        {
            return PkiHealthStatus.Expired;
        }

        var fifth = TimeSpan.FromTicks((next - summary.ThisUpdate).Ticks / 5);
        var margin = fifth > MinimumCrlMargin ? fifth : MinimumCrlMargin;
        return next - now < margin ? PkiHealthStatus.Expiring : PkiHealthStatus.Ok;
    }

    /// <summary><i>Stan: OK / Wygasa / Wygasła / Niedostępna / Nieznany</i>.</summary>
    public static string Status(ILocalizer localizer, PkiHealthStatus status) => localizer[PkiTextKeys.HealthKey(status)];

    /// <summary>"zostało 120 dni" / "wygasł 3 dni temu" / "jeszcze nieważny".</summary>
    public static string DaysLeft(ILocalizer localizer, CertificateSummary summary, DateTimeOffset now)
    {
        if (now < summary.NotBefore)
        {
            return localizer[L.Pki.NotYetValid];
        }

        var days = (int)Math.Floor((summary.NotAfter - now).TotalDays);
        return days >= 0 ? localizer.Format(L.Pki.DaysLeft, days) : localizer.Format(L.Pki.ExpiredAgo, -days);
    }

    /// <summary>The validity of a certificate: "od … do … (zostało N dni)".</summary>
    public static string Validity(ILocalizer localizer, CertificateSummary summary, DateTimeOffset now) =>
        localizer.Format(L.Pki.ValidityRange, Day(localizer, summary.NotBefore), Day(localizer, summary.NotAfter)) + " (" + DaysLeft(localizer, summary, now) + ")";

    /// <summary>"RSA (2048 bitów)".</summary>
    public static string Key(ILocalizer localizer, CertificateSummary summary) => summary.KeySize is { } bits
        ? localizer.Format(L.Pki.KeyText, summary.KeyAlgorithm, bits.ToString(localizer.Culture))
        : summary.KeyAlgorithm;

    /// <summary>The name of a purpose OID (<see cref="PkiFlags.KnownEku"/>, the OID itself otherwise).</summary>
    public static string Purpose(string oid) => PkiFlags.KnownEku.TryGetValue(oid, out var name) ? name : oid;

    /// <summary>The purposes of an EKU list ("Dowolny cel (brak EKU)" for an empty one).</summary>
    public static string Purposes(ILocalizer localizer, IReadOnlyList<string> ekus) =>
        ekus.Count == 0 ? localizer[L.PkiTemplates.AnyPurpose] : string.Join(", ", ekus.Select(Purpose));

    /// <summary>A list as text, or "—" when empty.</summary>
    public static string List(ILocalizer localizer, IEnumerable<string> values)
    {
        var text = string.Join(", ", values);
        return text.Length == 0 ? localizer[L.Pki.None] : text;
    }

    /// <summary>A template period in its largest whole unit ("1 rok", "6 tygodni"), its duration when no unit fits, "—" when absent.</summary>
    public static string Period(ILocalizer localizer, PkiPeriod? period)
    {
        if (period is not { } value)
        {
            return localizer[L.Pki.None];
        }

        if (value.Unit is not { } unit)
        {
            return value.Duration.ToString("g", localizer.Culture);
        }

        var count = value.Count;
        var form = count == 1 ? "One" : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? "Few" : "Many";
        return localizer.Format(L.PkiTemplates.UnitKey(unit, form), count.ToString("N0", localizer.Culture));
    }

    /// <summary>The value of a DN's first RDN (the object's name), the DN itself when it does not parse.</summary>
    public static string LeafName(string? dn) =>
        dn is null ? string.Empty : DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot ? parsed.Name : dn;

    /// <summary>The common name of an X.500 name as <c>X500DistinguishedName.Name</c> writes it ("CN=Kontoso Główny CA, O=…"), else the whole name.</summary>
    public static string CommonName(string subject)
    {
        foreach (var part in subject.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return part[3..].Trim('"');
            }
        }

        return subject;
    }

    /// <summary>A count in the UI culture.</summary>
    public static string Count(ILocalizer localizer, long value) => value.ToString("N0", localizer.Culture);

    /// <summary>A CRL number in decimal (big numbers stay exact), "—" when absent.</summary>
    public static string Number(System.Numerics.BigInteger? value) =>
        value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "—";
}
