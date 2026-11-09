using System.Globalization;

namespace AdMng.Core.Pki;

/// <summary>The four flag attributes of a certificate template (docs/dev/p-certificate-services.md 2.2).</summary>
public enum PkiFlagAttribute
{
    /// <summary><c>msPKI-Certificate-Name-Flag</c> (MS-CRTD 2.28).</summary>
    NameFlag,

    /// <summary><c>msPKI-Enrollment-Flag</c> (MS-CRTD 2.26).</summary>
    EnrollmentFlag,

    /// <summary><c>msPKI-Private-Key-Flag</c> (MS-CRTD 2.27).</summary>
    PrivateKeyFlag,

    /// <summary>The general <c>flags</c> of a <c>pKICertificateTemplate</c> (MS-CRTD 2.4) – chosen by the class, never by attribute name.</summary>
    GeneralFlag,
}

/// <summary>One bit of a flag table: its mask, its technical name (<c>CT_FLAG_…</c>) and whether the spec reserves it.</summary>
/// <param name="Mask">The bit.</param>
/// <param name="Name">The technical name (shown in the tooltip; the sentence is a resource, <c>PkiTpl.Flag.&lt;attribute&gt;.&lt;Name&gt;</c>).</param>
/// <param name="IsReserved">Reserved and ignored by every protocol (shown as <i>przestarzała, ignorowana</i>).</param>
public readonly record struct PkiFlagEntry(long Mask, string Name, bool IsReserved)
{
    /// <summary>The mask only (a constant of the spec, no directory value).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"0x{Mask:X}{(IsReserved ? " reserved" : string.Empty)}");
}

/// <summary>A multi-bit field of a flag value, decoded as a number (<i>wersja klienta: n</i>).</summary>
/// <param name="Mask">The bits of the field.</param>
/// <param name="Shift">The right shift that yields the number.</param>
/// <param name="Name">The field's name (resource <c>PkiTpl.Field.&lt;Name&gt;</c>).</param>
public readonly record struct PkiFlagField(long Mask, int Shift, string Name)
{
    /// <summary>The mask and shift only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"0x{Mask:X}>>{Shift}");
}

/// <summary>The number a multi-bit field holds.</summary>
/// <param name="Field">The field.</param>
/// <param name="Value">Its number.</param>
public readonly record struct PkiFlagFieldValue(PkiFlagField Field, int Value)
{
    /// <summary>The field only (the number is template data).</summary>
    public override string ToString() => Field.ToString();
}

/// <summary>A decoded flag value.</summary>
/// <param name="Set">The known bits that are set, in table order.</param>
/// <param name="UnknownBits">The set bits no table entry or field names (shown as <c>0x…</c>).</param>
/// <param name="Fields">The multi-bit fields with their numbers (private key flag only).</param>
public sealed record PkiDecodedFlags(IReadOnlyList<PkiFlagEntry> Set, long UnknownBits, IReadOnlyList<PkiFlagFieldValue> Fields)
{
    /// <summary>Counts only (the value is template data).</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Set.Count} flags, {(UnknownBits == 0 ? "no" : "some")} unknown bits, {Fields.Count} fields");
}

/// <summary>
/// The flag tables of certificate templates (2.2; values checked against Certipy <c>constants.py</c> at commit 3ae3442,
/// MS-CRTD 2.4/2.26/2.27/2.28 and the Watchdog's <c>FlagNames</c>), the EKU catalogue and the EKU predicates the ESC rules
/// use. Pure; shared with the Watchdog (decision 8).
/// </summary>
public static class PkiFlags
{
    /// <summary>Client Authentication.</summary>
    public const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    /// <summary>Server Authentication.</summary>
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    /// <summary>Smart-Card Logon.</summary>
    public const string SmartCardLogon = "1.3.6.1.4.1.311.20.2.2";

    /// <summary>PKINIT Client Authentication.</summary>
    public const string PkinitClientAuthentication = "1.3.6.1.5.2.3.4";

    /// <summary>KDC Authentication.</summary>
    public const string KdcAuthentication = "1.3.6.1.5.2.3.5";

    /// <summary>Certificate Request Agent (Enrollment Agent).</summary>
    public const string CertificateRequestAgent = "1.3.6.1.4.1.311.20.2.1";

    /// <summary>Any Purpose.</summary>
    public const string AnyPurpose = "2.5.29.37.0";

    /// <summary>The EKU OIDs the predicates and the UI name (OID → English name of the spec).</summary>
    public static IReadOnlyDictionary<string, string> KnownEku { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ClientAuthentication] = "Client Authentication",
        [ServerAuthentication] = "Server Authentication",
        [SmartCardLogon] = "Smart Card Logon",
        [PkinitClientAuthentication] = "PKINIT Client Authentication",
        [KdcAuthentication] = "KDC Authentication",
        [CertificateRequestAgent] = "Certificate Request Agent",
        [AnyPurpose] = "Any Purpose",
    };

    /// <summary>The bits of a table, in the order of 2.2 (reserved bits included).</summary>
    public static IReadOnlyList<PkiFlagEntry> Table(PkiFlagAttribute attribute) =>
        throw new NotImplementedException("P1 C2: PkiFlags.Table");

    /// <summary>The multi-bit fields of <c>msPKI-Private-Key-Flag</c> (minimum CA version, shift 16; minimum client version, shift 24).</summary>
    public static IReadOnlyList<PkiFlagField> PrivateKeyFields => throw new NotImplementedException("P1 C2: PkiFlags.PrivateKeyFields");

    /// <summary>Decodes a value: the known bits, the unknown bits and (private key flag) the fields as numbers.</summary>
    public static PkiDecodedFlags Decode(PkiFlagAttribute attribute, long value) =>
        throw new NotImplementedException("P1 C2: PkiFlags.Decode");

    /// <summary>
    /// The entries of <paramref name="bits"/> in the given order (the Watchdog's <c>FlagNames</c> subset, decision 8: today's
    /// bits in today's order, behaviour-neutral).
    /// </summary>
    public static IReadOnlyList<PkiFlagEntry> Select(PkiFlagAttribute attribute, IReadOnlyList<long> bits) =>
        throw new NotImplementedException("P1 C2: PkiFlags.Select");

    /// <summary>Any Purpose, or no EKU at all (an empty set means Any Purpose, as Certipy).</summary>
    public static bool IsAnyPurpose(IReadOnlyCollection<string> ekus) =>
        throw new NotImplementedException("P1 C2: PkiFlags.IsAnyPurpose");

    /// <summary>Client Authentication, Smart-Card Logon, PKINIT Client Authentication or <see cref="IsAnyPurpose"/>.</summary>
    public static bool IsClientAuth(IReadOnlyCollection<string> ekus) =>
        throw new NotImplementedException("P1 C2: PkiFlags.IsClientAuth");

    /// <summary>Server Authentication or <see cref="IsAnyPurpose"/>.</summary>
    public static bool IsServerAuth(IReadOnlyCollection<string> ekus) =>
        throw new NotImplementedException("P1 C2: PkiFlags.IsServerAuth");

    /// <summary>Certificate Request Agent or <see cref="IsAnyPurpose"/>.</summary>
    public static bool IsEnrollmentAgent(IReadOnlyCollection<string> ekus) =>
        throw new NotImplementedException("P1 C2: PkiFlags.IsEnrollmentAgent");
}
