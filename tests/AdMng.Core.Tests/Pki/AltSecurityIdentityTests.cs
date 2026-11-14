using AdMng.Core.Pki;
using AdMng.Testing.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// <c>altSecurityIdentities</c> forms and their KB5014754 strength (docs/dev/p-certificate-services.md 2.4): the strong
/// issuer + serial (serial byte-reversed), SKI and SHA1-PUKEY, the weak issuer + subject, subject only and RFC 822, Kerberos
/// as not a certificate mapping, and everything else – a bare <c>&lt;SR&gt;</c> included – invalid.
/// </summary>
public sealed class AltSecurityIdentityTests
{
    private const string Issuer = "C=PL,O=" + PkiSamples.Organization + ",CN=" + PkiSamples.IssuingCaName;

    public static TheoryData<string, AltSecurityIdentityKind, MappingStrength> Forms() => new()
    {
        { "X509:<I>" + Issuer + "<SR>0063", AltSecurityIdentityKind.X509IssuerSerial, MappingStrength.Strong },
        { "X509:<SKI>8D5E1F0A2B3C4D5E6F708192A3B4C5D6E7F80910", AltSecurityIdentityKind.X509Ski, MappingStrength.Strong },
        { "X509:<SHA1-PUKEY>0123456789abcdef0123456789abcdef01234567", AltSecurityIdentityKind.X509Sha1PublicKey, MappingStrength.Strong },
        { "X509:<I>" + Issuer + "<S>DC=test,DC=contoso,CN=Users,CN=Łukasz Wiśniewski", AltSecurityIdentityKind.X509IssuerSubject, MappingStrength.Weak },
        { "X509:<S>DC=test,DC=contoso,OU=Księgowość,CN=Grażyna Dąbrowska", AltSecurityIdentityKind.X509SubjectOnly, MappingStrength.Weak },
        { "X509:<RFC822>lukasz.wisniewski@contoso.test", AltSecurityIdentityKind.X509Rfc822, MappingStrength.Weak },
        { "Kerberos:pawel.lewandowski@CONTOSO.TEST", AltSecurityIdentityKind.Kerberos, MappingStrength.NotCertificate },
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public void EachForm_HasItsKindAndStrength(string value, AltSecurityIdentityKind kind, MappingStrength strength)
    {
        var mapping = AltSecurityIdentity.Parse(value);

        Assert.Equal(kind, mapping.Kind);
        Assert.Equal(strength, mapping.Strength);
        Assert.Equal(value, mapping.Value);
    }

    [Fact]
    public void IssuerSerial_GivesTheSerialInCertificateOrder()
    {
        // Jan Kowalski's published certificate has the serial 0x6300; <SR> stores it byte-reversed.
        var jan = AltSecurityIdentity.Parse("X509:<I>" + Issuer + "<SR>0063");
        Assert.Equal(Issuer, jan.Issuer);
        Assert.Equal("6300", jan.SerialNumber);
        Assert.Null(jan.Subject);

        // The KB5014754 example: certificate serial 2B0000000011AC0000000012.
        var kb = AltSecurityIdentity.Parse("X509:<I>DC=com,DC=contoso,CN=CONTOSO-DC-CA<SR>1200000000ac11000000002b");
        Assert.Equal(MappingStrength.Strong, kb.Strength);
        Assert.Equal("2B0000000011AC0000000012", kb.SerialNumber);
    }

    [Fact]
    public void TheParts_AreExposedForTheTabs()
    {
        var weak = AltSecurityIdentity.Parse("X509:<I>" + Issuer + "<S>DC=test,DC=contoso,CN=Users,CN=Administrator");
        Assert.Equal(Issuer, weak.Issuer);
        Assert.Equal("DC=test,DC=contoso,CN=Users,CN=Administrator", weak.Subject);

        Assert.Equal("8D5E1F0A2B3C4D5E", AltSecurityIdentity.Parse("X509:<SKI>8d5e1f0a2b3c4d5e").Identifier);
        Assert.Equal("zofia.zurawska@partner.example", AltSecurityIdentity.Parse("X509:<RFC822>zofia.zurawska@partner.example").Identifier);
        Assert.Equal("pawel.lewandowski@CONTOSO.TEST", AltSecurityIdentity.Parse("Kerberos:pawel.lewandowski@CONTOSO.TEST").Identifier);
    }

    [Theory]
    [InlineData("X509:<SR>0063")]
    [InlineData("X509:<SR>0063<I>C=PL,CN=Kontoso Wystawiający CA")]
    [InlineData("X509:<I>C=PL,CN=Kontoso Wystawiający CA")]
    [InlineData("X509:<S>CN=Jan<I>C=PL,CN=Kontoso Wystawiający CA")]
    [InlineData("X509:<I><SR>0063")]
    [InlineData("X509:<I>C=PL,CN=Kontoso Wystawiający CA<SR>")]
    [InlineData("X509:<I>C=PL,CN=Kontoso Wystawiający CA<SR>063")]
    [InlineData("X509:<I>C=PL,CN=Kontoso Wystawiający CA<SR>00GG")]
    [InlineData("X509:<I>C=PL,CN=Kontoso Wystawiający CA<S>CN=Jan<SR>0063")]
    [InlineData("X509:<SKI>xyz0")]
    [InlineData("X509:<SKI>")]
    [InlineData("X509:<SHA1-PUKEY>0123456789abcdef")]
    [InlineData("X509:<RFC822>")]
    [InlineData("X509:<UPN>jan.kowalski@contoso.test")]
    [InlineData("X509:")]
    [InlineData("X509:CN=Jan Kowalski")]
    [InlineData("Kerberos:")]
    [InlineData("SMTP:jan.kowalski@contoso.test")]
    [InlineData("Żółć-Gęślą-Jaźń")]
    [InlineData("")]
    public void EverythingElse_IsInvalid(string value)
    {
        var mapping = AltSecurityIdentity.Parse(value);

        Assert.Equal(AltSecurityIdentityKind.Invalid, mapping.Kind);
        Assert.Equal(MappingStrength.Invalid, mapping.Strength);
        Assert.Equal(value, mapping.Value);
    }

    [Fact]
    public void Null_IsInvalid_NeverAThrow()
    {
        var mapping = AltSecurityIdentity.Parse(null!);

        Assert.Equal(AltSecurityIdentityKind.Invalid, mapping.Kind);
        Assert.Equal(string.Empty, mapping.Value);
    }

    [Fact]
    public void TagsAndPrefixes_AreCaseInsensitive_AndAnEscapedBracketStaysInTheValue()
    {
        Assert.Equal(AltSecurityIdentityKind.X509IssuerSerial, AltSecurityIdentity.Parse("x509:<i>CN=Kontoso<sr>01").Kind);
        Assert.Equal(AltSecurityIdentityKind.X509Sha1PublicKey, AltSecurityIdentity.Parse("X509:<sha1-pukey>0123456789ABCDEF0123456789ABCDEF01234567").Kind);
        Assert.Equal(AltSecurityIdentityKind.Kerberos, AltSecurityIdentity.Parse("kerberos:jan@CONTOSO.TEST").Kind);

        var escaped = AltSecurityIdentity.Parse(@"X509:<I>CN=Kontoso \<Główny\> CA<S>CN=Żaneta");
        Assert.Equal(AltSecurityIdentityKind.X509IssuerSubject, escaped.Kind);
        Assert.Equal(@"CN=Kontoso \<Główny\> CA", escaped.Issuer);
        Assert.Equal("CN=Żaneta", escaped.Subject);
    }

    [Fact]
    public void ToString_CarriesKindAndStrengthOnly()
    {
        var text = AltSecurityIdentity.Parse("X509:<I>" + Issuer + "<S>CN=Łukasz Wiśniewski").ToString();

        Assert.Equal("AltSecurityIdentity(X509IssuerSubject Weak)", text);
        Assert.DoesNotContain(PkiSamples.IssuingCaName, text, StringComparison.Ordinal);
    }
}
