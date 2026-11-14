using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AdMng.Core.Pki;

/// <summary>
/// The header of one CRL (docs/dev/p-certificate-services.md 2.1) – .NET 10 has no CRL reader, so <see cref="Of"/>
/// parses the DER with <c>System.Formats.Asn1.AsnReader</c>. Never the individual revoked serials (rule 1 – export only).
/// <see cref="ToString"/> carries no issuer or number. Every member has a default (hand-built test instances).
/// </summary>
public sealed record CrlSummary
{
    /// <summary>The issuer DN (formatted as <see cref="CertificateSummary.Subject"/>, so the two compare as text).</summary>
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

    /// <summary>The X.509 CRL version: 2 when <c>version</c> is present (v2, what AD CS writes), 1 when it is absent.</summary>
    public int Version { get; init; } = 2;

    /// <summary>
    /// Parses a DER CRL (RFC 5280 5.1; a BER encoding is accepted as well); null when it is malformed. The signature is
    /// not verified (the issuer is not known here).
    /// </summary>
    public static CrlSummary? Of(ReadOnlySpan<byte> der)
    {
        if (der.IsEmpty)
        {
            return null;
        }

        var data = der.ToArray();
        return TryParse(data, AsnEncodingRules.DER) ?? TryParse(data, AsnEncodingRules.BER);
    }

    /// <summary>Kind and count only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(CrlSummary)}({(IsDelta ? "delta" : "base")}, {RevokedCount} revoked)");

    private static CrlSummary? TryParse(byte[] data, AsnEncodingRules rules)
    {
        try
        {
            return Parse(data, rules);
        }
        catch (AsnContentException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static CrlSummary Parse(byte[] data, AsnEncodingRules rules)
    {
        // CertificateList ::= SEQUENCE { tbsCertList, signatureAlgorithm, signatureValue BIT STRING }
        var outer = new AsnReader(data, rules);
        var list = outer.ReadSequence();
        outer.ThrowIfNotEmpty();
        var tbs = list.ReadSequence();
        _ = list.ReadSequence().ReadObjectIdentifier();
        _ = list.ReadBitString(out _);
        list.ThrowIfNotEmpty();

        // TBSCertList ::= SEQUENCE { version Version OPTIONAL (v2 = 1), signature, issuer Name, thisUpdate Time,
        // nextUpdate Time OPTIONAL, revokedCertificates SEQUENCE OF … OPTIONAL, crlExtensions [0] EXPLICIT Extensions OPTIONAL }
        var version = 1;
        if (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
        {
            if (!tbs.TryReadInt32(out var encoded) || encoded is not (0 or 1))
            {
                throw new AsnContentException("Unknown CRL version.");
            }

            version = encoded + 1;
        }

        _ = tbs.ReadSequence().ReadObjectIdentifier();
        var issuer = new X500DistinguishedName(tbs.ReadEncodedValue().Span).Name;
        var thisUpdate = ReadTime(tbs);
        DateTimeOffset? nextUpdate = tbs.HasData && IsTime(tbs.PeekTag()) ? ReadTime(tbs) : null;

        var revoked = 0;
        if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
        {
            var entries = tbs.ReadSequence();
            while (entries.HasData)
            {
                // SEQUENCE { userCertificate CertificateSerialNumber, revocationDate Time, crlEntryExtensions OPTIONAL }
                var entry = entries.ReadSequence();
                _ = entry.ReadIntegerBytes();
                _ = ReadTime(entry);
                revoked++;
            }
        }

        string? authorityKeyIdentifier = null;
        BigInteger? crlNumber = null;
        BigInteger? baseCrlNumber = null;
        IReadOnlyList<string> freshestCrl = [];
        var extensionsTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(extensionsTag))
        {
            var extensions = tbs.ReadSequence(extensionsTag).ReadSequence();
            while (extensions.HasData)
            {
                var extension = extensions.ReadSequence();
                var oid = extension.ReadObjectIdentifier();
                if (extension.HasData && extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean))
                {
                    _ = extension.ReadBoolean();
                }

                var value = extension.ReadOctetString();
                switch (oid)
                {
                    case PkiDer.AuthorityKeyIdentifierOid:
                        authorityKeyIdentifier = PkiDer.ReadAuthorityKeyIdentifier(value, rules);
                        break;
                    case PkiDer.CrlNumberOid:
                        crlNumber = new AsnReader(value, rules).ReadInteger();
                        break;
                    case PkiDer.DeltaCrlIndicatorOid:
                        baseCrlNumber = new AsnReader(value, rules).ReadInteger();
                        break;
                    case PkiDer.FreshestCrlOid:
                        freshestCrl = PkiDer.ReadDistributionPointUrls(value, rules);
                        break;
                }
            }
        }

        tbs.ThrowIfNotEmpty();
        return new CrlSummary
        {
            Version = version,
            Issuer = issuer,
            AuthorityKeyIdentifier = authorityKeyIdentifier,
            CrlNumber = crlNumber,
            IsDelta = baseCrlNumber is not null,
            BaseCrlNumber = baseCrlNumber,
            ThisUpdate = thisUpdate,
            NextUpdate = nextUpdate,
            RevokedCount = revoked,
            FreshestCrl = freshestCrl,
        };
    }

    private static bool IsTime(Asn1Tag tag) => tag.HasSameClassAndValue(Asn1Tag.UtcTime) || tag.HasSameClassAndValue(Asn1Tag.GeneralizedTime);

    /// <summary><c>Time ::= CHOICE { utcTime UTCTime, generalTime GeneralizedTime }</c> (UTCTime years 50–99 are 19xx, RFC 5280 4.1.2.5.1).</summary>
    private static DateTimeOffset ReadTime(AsnReader reader)
    {
        var tag = reader.PeekTag();
        var time = tag.HasSameClassAndValue(Asn1Tag.UtcTime) ? reader.ReadUtcTime()
            : tag.HasSameClassAndValue(Asn1Tag.GeneralizedTime) ? reader.ReadGeneralizedTime()
            : throw new AsnContentException("Not a time.");
        return time.ToUniversalTime();
    }
}
