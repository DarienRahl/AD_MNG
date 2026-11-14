using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdMng.Core.Pki;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// <see cref="CrlSummary.Of"/> (docs/dev/p-certificate-services.md 2.1): the CRLs of <see cref="PkiSamples"/> (base CRLs
/// through <see cref="CertificateRevocationListBuilder"/> and by hand, the delta, the CRL without <c>nextUpdate</c>) and
/// hand-encoded edge cases – version, times, extensions, revoked entries, malformed input.
/// </summary>
public sealed class CrlSummaryTests
{
    [Fact]
    public void TheRootBaseCrl_FromTheBuilder()
    {
        var summary = CrlSummary.Of(PkiSamples.RootCrlDer())!;

        Assert.Equal(CertificateSummary.Of(PkiSamples.RootCaDer)!.Subject, summary.Issuer);
        Assert.Contains("Kontoso Główny CA", summary.Issuer, StringComparison.Ordinal);
        Assert.Equal(new BigInteger(7), summary.CrlNumber);
        Assert.False(summary.IsDelta);
        Assert.Null(summary.BaseCrlNumber);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero), summary.ThisUpdate);
        Assert.Equal(new DateTimeOffset(2027, 6, 1, 8, 0, 0, TimeSpan.Zero), summary.NextUpdate);
        Assert.Equal(0, summary.RevokedCount);
        Assert.Equal(2, summary.Version);
        Assert.Empty(summary.FreshestCrl);
        Assert.Equal(CertificateSummary.Of(PkiSamples.RootCaDer)!.SubjectKeyIdentifier, summary.AuthorityKeyIdentifier);
    }

    [Fact]
    public void TheIssuingBaseCrl_HasTwoRevokedEntries_AndTheFreshestCrl()
    {
        var summary = CrlSummary.Of(PkiSamples.IssuingCrlDer())!;

        Assert.Equal(CertificateSummary.Of(PkiSamples.IssuingCaDer)!.Subject, summary.Issuer);
        Assert.Equal(new BigInteger(42), summary.CrlNumber);
        Assert.False(summary.IsDelta);
        Assert.Equal(2, summary.RevokedCount);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), summary.ThisUpdate);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), summary.NextUpdate);
        Assert.Equal([PkiSamples.IssuingDeltaCrlHttpUrl], summary.FreshestCrl);
        Assert.Equal(CertificateSummary.Of(PkiSamples.IssuingCaDer)!.SubjectKeyIdentifier, summary.AuthorityKeyIdentifier);
    }

    [Fact]
    public void TheDeltaCrl_CarriesTheIndicator()
    {
        var summary = CrlSummary.Of(PkiSamples.IssuingDeltaCrlDer())!;

        Assert.True(summary.IsDelta);
        Assert.Equal(new BigInteger(43), summary.CrlNumber);
        Assert.Equal(new BigInteger(42), summary.BaseCrlNumber);
        Assert.Equal(1, summary.RevokedCount);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), summary.NextUpdate);
        Assert.Equal("CrlSummary(delta, 1 revoked)", summary.ToString());
    }

    [Fact]
    public void ACrlWithoutNextUpdate_HasNone()
    {
        var summary = CrlSummary.Of(PkiSamples.CrlWithoutNextUpdateDer())!;

        Assert.Null(summary.NextUpdate);
        Assert.Equal(new BigInteger(44), summary.CrlNumber);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), summary.ThisUpdate);
        Assert.Equal(0, summary.RevokedCount);
    }

    [Fact]
    public void BuilderEntries_WithReasonCodes_AreCounted_AndGeneralizedTimeIsRead()
    {
        var revoked = Enumerable.Range(1, 5).Select(i => (PkiSamples.Serial(0x5100 + i), PkiSamples.Now.AddDays(-i))).ToList();
        var crl = PkiSamples.BuildCrl(PkiSamples.IssuingCa, 1234, new DateTimeOffset(2051, 1, 2, 3, 4, 5, TimeSpan.Zero),
            new DateTimeOffset(2051, 2, 2, 3, 4, 5, TimeSpan.Zero), revoked);

        var summary = CrlSummary.Of(crl)!;

        Assert.Equal(5, summary.RevokedCount);
        Assert.Equal(new BigInteger(1234), summary.CrlNumber);
        Assert.Equal(new DateTimeOffset(2051, 1, 2, 3, 4, 5, TimeSpan.Zero), summary.ThisUpdate);
        Assert.Equal(new DateTimeOffset(2051, 2, 2, 3, 4, 5, TimeSpan.Zero), summary.NextUpdate);
    }

    [Fact]
    public void ACrlNumberBeyondLong_IsKept()
    {
        var number = BigInteger.Pow(2, 100) + 17;
        var builder = new CertificateRevocationListBuilder();
        var crl = builder.Build(PkiSamples.IssuingCa, number, PkiSamples.Now.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, PkiSamples.Now);

        Assert.Equal(number, CrlSummary.Of(crl)!.CrlNumber);
    }

    [Fact]
    public void AVersion1Crl_WithoutExtensions()
    {
        var crl = Encode(version: null, PkiSamples.IssuingCa.SubjectName.RawData, thisUpdate: new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero),
            nextUpdate: new DateTimeOffset(2050, 1, 1, 0, 0, 0, TimeSpan.Zero), revoked: 3, extensions: null);

        var summary = CrlSummary.Of(crl)!;

        Assert.Equal(1, summary.Version);
        Assert.Null(summary.CrlNumber);
        Assert.Null(summary.AuthorityKeyIdentifier);
        Assert.False(summary.IsDelta);
        Assert.Equal(3, summary.RevokedCount);
        Assert.Equal(new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero), summary.ThisUpdate);
        Assert.Equal(new DateTimeOffset(2050, 1, 1, 0, 0, 0, TimeSpan.Zero), summary.NextUpdate);
    }

    [Fact]
    public void AnEmptyRevokedList_AndUnknownExtensions_AreAccepted()
    {
        var crl = Encode(version: 1, new X500DistinguishedName("CN=Żółty CA, O=Kontoso").RawData, PkiSamples.Now, nextUpdate: null, revoked: 0,
            extensions: w =>
            {
                WriteExtension(w, "1.3.6.1.4.1.311.21.1", critical: false, inner => inner.WriteInteger(0)); // CA version
                WriteExtension(w, "2.5.29.20", critical: false, inner => inner.WriteInteger(9));
            },
            emptyRevokedList: true);

        var summary = CrlSummary.Of(crl)!;

        Assert.Equal(2, summary.Version);
        Assert.Equal(0, summary.RevokedCount);
        Assert.Equal(new BigInteger(9), summary.CrlNumber);
        Assert.Contains("Żółty CA", summary.Issuer, StringComparison.Ordinal);
        Assert.Null(summary.NextUpdate);
    }

    [Fact]
    public void ANonMinimalLength_FallsBackToBer()
    {
        // A revoked entry whose length is in the long form (0x81 0x14): valid BER, not DER – some CAs write it.
        var crl = Encode(version: 1, PkiSamples.IssuingCa.SubjectName.RawData, PkiSamples.Now, PkiSamples.Now.AddDays(1), revoked: 0, extensions: null,
            rawEntries: [[0x30, 0x81, 0x14, 0x02, 0x03, 0x00, 0x80, 0x7F, 0x17, 0x0D, .. "261013120000Z"u8.ToArray()]]);

        var summary = CrlSummary.Of(crl)!;

        Assert.Equal(1, summary.RevokedCount);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x30, 0x82, 0x01, 0x00, 0x02, 0x01 })]
    [InlineData(new byte[] { 0x30, 0x00 })]
    public void MalformedDer_IsNull(byte[] der)
    {
        Assert.Null(CrlSummary.Of(der));
    }

    [Fact]
    public void ACertificate_TrailingData_AndAnUnknownVersion_AreNotCrls()
    {
        Assert.Null(CrlSummary.Of(PkiSamples.IssuingCaDer));
        Assert.Null(CrlSummary.Of([.. PkiSamples.RootCrlDer(), 0x00]));
        var truncated = PkiSamples.IssuingCrlDer();
        Assert.Null(CrlSummary.Of(truncated.AsSpan(0, truncated.Length - 10)));
        Assert.Null(CrlSummary.Of(Encode(version: 5, PkiSamples.IssuingCa.SubjectName.RawData, PkiSamples.Now, null, 0, null)));
    }

    [Fact]
    public void ToString_CarriesNoIssuerOrNumber()
    {
        var summary = CrlSummary.Of(PkiSamples.IssuingCrlDer())!;

        Assert.Equal("CrlSummary(base, 2 revoked)", summary.ToString());
        Assert.DoesNotContain("Kontoso", summary.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("42", summary.ToString(), StringComparison.Ordinal);
    }

    /// <summary>An unsigned CRL (the signature is not checked by the parser) with the given parts.</summary>
    private static byte[] Encode(
        int? version,
        byte[] issuer,
        DateTimeOffset thisUpdate,
        DateTimeOffset? nextUpdate,
        int revoked,
        Action<AsnWriter>? extensions,
        bool emptyRevokedList = false,
        IReadOnlyList<byte[]>? rawEntries = null)
    {
        var tbs = new AsnWriter(AsnEncodingRules.BER);
        tbs.PushSequence();
        if (version is { } v)
        {
            tbs.WriteInteger(v);
        }

        WriteAlgorithm(tbs);
        tbs.WriteEncodedValue(issuer);
        WriteTime(tbs, thisUpdate);
        if (nextUpdate is { } next)
        {
            WriteTime(tbs, next);
        }

        if (revoked > 0 || emptyRevokedList || rawEntries is not null)
        {
            tbs.PushSequence();
            for (var i = 0; i < revoked; i++)
            {
                tbs.PushSequence();
                tbs.WriteInteger(0x7000 + i);
                tbs.WriteUtcTime(thisUpdate.AddHours(-i - 1));
                tbs.PopSequence();
            }

            foreach (var entry in rawEntries ?? [])
            {
                tbs.WriteEncodedValue(entry);
            }

            tbs.PopSequence();
        }

        if (extensions is not null)
        {
            var tag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
            tbs.PushSequence(tag);
            tbs.PushSequence();
            extensions(tbs);
            tbs.PopSequence();
            tbs.PopSequence(tag);
        }

        tbs.PopSequence();
        var crl = new AsnWriter(AsnEncodingRules.BER);
        crl.PushSequence();
        crl.WriteEncodedValue(tbs.Encode());
        WriteAlgorithm(crl);
        crl.WriteBitString([0x01, 0x02, 0x03]);
        crl.PopSequence();
        return crl.Encode();
    }

    private static void WriteTime(AsnWriter writer, DateTimeOffset time)
    {
        if (time.Year is >= 1950 and < 2050)
        {
            writer.WriteUtcTime(time);
        }
        else
        {
            writer.WriteGeneralizedTime(time);
        }
    }

    private static void WriteAlgorithm(AsnWriter writer)
    {
        writer.PushSequence();
        writer.WriteObjectIdentifier("1.2.840.113549.1.1.11");
        writer.WriteNull();
        writer.PopSequence();
    }

    private static void WriteExtension(AsnWriter writer, string oid, bool critical, Action<AsnWriter> value)
    {
        var inner = new AsnWriter(AsnEncodingRules.DER);
        value(inner);
        writer.PushSequence();
        writer.WriteObjectIdentifier(oid);
        if (critical)
        {
            writer.WriteBoolean(true);
        }

        writer.WriteOctetString(inner.Encode());
        writer.PopSequence();
    }
}
