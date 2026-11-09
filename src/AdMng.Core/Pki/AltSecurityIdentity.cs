namespace AdMng.Core.Pki;

/// <summary>The form of an <c>altSecurityIdentities</c> value (KB5014754; <c>Cert.MappingKind.&lt;AltSecurityIdentityKind&gt;</c>).</summary>
public enum AltSecurityIdentityKind
{
    /// <summary><c>X509:&lt;I&gt;…&lt;SR&gt;…</c> – issuer and serial number (reversed byte order): strong.</summary>
    X509IssuerSerial,

    /// <summary><c>X509:&lt;SKI&gt;…</c> – subject key identifier: strong.</summary>
    X509Ski,

    /// <summary><c>X509:&lt;SHA1-PUKEY&gt;…</c> – SHA-1 of the public key: strong.</summary>
    X509Sha1PublicKey,

    /// <summary><c>X509:&lt;I&gt;…&lt;S&gt;…</c> – issuer and subject: weak.</summary>
    X509IssuerSubject,

    /// <summary><c>X509:&lt;S&gt;…</c> – subject only: weak.</summary>
    X509SubjectOnly,

    /// <summary><c>X509:&lt;RFC822&gt;…</c> – e-mail address: weak.</summary>
    X509Rfc822,

    /// <summary><c>Kerberos:…</c> – not a certificate mapping (shown separately).</summary>
    Kerberos,

    /// <summary>Anything else (including <c>&lt;SR&gt;</c> without <c>&lt;I&gt;</c>).</summary>
    Invalid,
}

/// <summary>The KB5014754 strength of a mapping (<c>Cert.Mapping.&lt;MappingStrength&gt;</c>).</summary>
public enum MappingStrength
{
    Strong,
    Weak,
    NotCertificate,
    Invalid,
}

/// <summary>One parsed <c>altSecurityIdentities</c> value (docs/dev/p-certificate-services.md 2.4).</summary>
/// <param name="Value">The value as stored (shown on the <i>Mapowania nazw</i> tab).</param>
/// <param name="Kind">Its form.</param>
/// <param name="Strength">Its strength.</param>
public sealed record AltSecurityIdentity(string Value, AltSecurityIdentityKind Kind, MappingStrength Strength)
{
    /// <summary>Parses one value (never throws: anything unknown is <see cref="AltSecurityIdentityKind.Invalid"/>).</summary>
    public static AltSecurityIdentity Parse(string value) => throw new NotImplementedException("P1 C4: AltSecurityIdentity.Parse");

    /// <summary>Kind and strength only (never the value).</summary>
    public override string ToString() => $"{nameof(AltSecurityIdentity)}({Kind} {Strength})";
}
