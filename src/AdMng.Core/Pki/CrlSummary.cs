using System.Globalization;
using System.Numerics;

namespace AdMng.Core.Pki;

/// <summary>
/// The header of one CRL (docs/dev/p-certificate-services.md 2.1) – .NET 10 has no CRL reader, so <see cref="Of"/>
/// parses the DER with <c>System.Formats.Asn1.AsnReader</c>. Never the individual revoked serials (rule 1 – export only).
/// <see cref="ToString"/> carries no issuer or number. Every member has a default (hand-built test instances).
/// </summary>
public sealed record CrlSummary
{
    /// <summary>The issuer DN.</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>Authority Key Identifier (key id), upper-case hex, or null.</summary>
    public string? AuthorityKeyIdentifier { get; init; }

    /// <summary>CRL Number (<c>2.5.29.20</c>), null when absent.</summary>
    public BigInteger? CrlNumber { get; init; }

    /// <summary>A delta CRL (Delta CRL Indicator <c>2.5.29.27</c> present).</summary>
    public bool IsDelta { get; init; }

    /// <summary>The base CRL number of a delta CRL.</summary>
    public BigInteger? BaseCrlNumber { get; init; }

    public DateTimeOffset ThisUpdate { get; init; }

    /// <summary>Null when the CRL has none (its state is <i>nieznany</i>, never <i>wygasła</i>).</summary>
    public DateTimeOffset? NextUpdate { get; init; }

    /// <summary>The number of revoked entries.</summary>
    public int RevokedCount { get; init; }

    /// <summary>
    /// Freshest CRL URLs of a base CRL (<c>2.5.29.46</c>, a CDP-syntax extension AD CS writes for the URLs marked
    /// <i>Include in CRLs. Clients use this to find Delta CRL locations</i>): where its delta CRL is.
    /// </summary>
    public IReadOnlyList<string> FreshestCrl { get; init; } = [];

    /// <summary>Parses a DER CRL; null when it is malformed.</summary>
    public static CrlSummary? Of(ReadOnlySpan<byte> der) => throw new NotImplementedException("P1 C1: CrlSummary.Of");

    /// <summary>Kind and count only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(CrlSummary)}({(IsDelta ? "delta" : "base")}, {RevokedCount} revoked)");
}
