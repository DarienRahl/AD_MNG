using System.Text;

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
    private const string X509Prefix = "X509:";
    private const string KerberosPrefix = "Kerberos:";

    // The tags of KB5014754 (matched case-insensitively, as the KDC does).
    private const string IssuerTag = "I";
    private const string SubjectTag = "S";
    private const string SerialTag = "SR";
    private const string SkiTag = "SKI";
    private const string PublicKeyTag = "SHA1-PUKEY";
    private const string Rfc822Tag = "RFC822";

    private static readonly string[] Tags = [IssuerTag, SubjectTag, SerialTag, SkiTag, PublicKeyTag, Rfc822Tag];

    /// <summary>The issuer of <c>&lt;I&gt;</c> (in the reversed RDN order <c>altSecurityIdentities</c> stores), else null.</summary>
    public string? Issuer { get; init; }

    /// <summary>The subject of <c>&lt;S&gt;</c> (reversed RDN order, as stored), else null.</summary>
    public string? Subject { get; init; }

    /// <summary>
    /// The certificate serial number of <c>&lt;I&gt;…&lt;SR&gt;…</c> in the certificate's byte order (big-endian, upper-case hex,
    /// as <see cref="CertificateSummary.SerialNumber"/>) – <c>&lt;SR&gt;</c> stores it byte-reversed – else null.
    /// </summary>
    public string? SerialNumber { get; init; }

    /// <summary>
    /// The identifier of the single-tag forms: the subject key identifier of <c>&lt;SKI&gt;</c> or the SHA-1 of the public key of
    /// <c>&lt;SHA1-PUKEY&gt;</c> (upper-case hex), the address of <c>&lt;RFC822&gt;</c>, the principal of <c>Kerberos:</c>; else null.
    /// </summary>
    public string? Identifier { get; init; }

    /// <summary>
    /// Parses one value (never throws: anything unknown is <see cref="AltSecurityIdentityKind.Invalid"/>). Strong:
    /// <c>X509:&lt;I&gt;…&lt;SR&gt;…</c> (the serial in reversed byte order), <c>X509:&lt;SKI&gt;…</c>, <c>X509:&lt;SHA1-PUKEY&gt;…</c>
    /// (40 hex digits); weak: <c>X509:&lt;I&gt;…&lt;S&gt;…</c>, <c>X509:&lt;S&gt;…</c>, <c>X509:&lt;RFC822&gt;…</c>; <c>Kerberos:…</c> is not
    /// a certificate mapping; <c>&lt;SR&gt;</c> without <c>&lt;I&gt;</c>, an issuer alone, empty parts, unknown tags and malformed hex
    /// are invalid.
    /// </summary>
    public static AltSecurityIdentity Parse(string value)
    {
        if (value is null)
        {
            return new AltSecurityIdentity(string.Empty, AltSecurityIdentityKind.Invalid, MappingStrength.Invalid);
        }

        var text = value.Trim();
        if (text.StartsWith(KerberosPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var principal = text[KerberosPrefix.Length..].Trim();
            return principal.Length == 0
                ? Invalid(value)
                : new AltSecurityIdentity(value, AltSecurityIdentityKind.Kerberos, MappingStrength.NotCertificate) { Identifier = principal };
        }

        if (!text.StartsWith(X509Prefix, StringComparison.OrdinalIgnoreCase) || !TryTokenize(text.AsSpan(X509Prefix.Length), out var parts))
        {
            return Invalid(value);
        }

        return parts switch
        {
            [(IssuerTag, var issuer), (SubjectTag, var subject)] =>
                new AltSecurityIdentity(value, AltSecurityIdentityKind.X509IssuerSubject, MappingStrength.Weak) { Issuer = issuer, Subject = subject },
            [(SubjectTag, var subject)] =>
                new AltSecurityIdentity(value, AltSecurityIdentityKind.X509SubjectOnly, MappingStrength.Weak) { Subject = subject },
            [(Rfc822Tag, var address)] =>
                new AltSecurityIdentity(value, AltSecurityIdentityKind.X509Rfc822, MappingStrength.Weak) { Identifier = address },
            [(IssuerTag, var issuer), (SerialTag, var serial)] when ReversedSerial(serial) is { } number =>
                new AltSecurityIdentity(value, AltSecurityIdentityKind.X509IssuerSerial, MappingStrength.Strong) { Issuer = issuer, SerialNumber = number },
            [(SkiTag, var ski)] when Hex(ski, requiredLength: null) is { } identifier =>
                new AltSecurityIdentity(value, AltSecurityIdentityKind.X509Ski, MappingStrength.Strong) { Identifier = identifier },
            [(PublicKeyTag, var hash)] when Hex(hash, requiredLength: 40) is { } identifier =>
                new AltSecurityIdentity(value, AltSecurityIdentityKind.X509Sha1PublicKey, MappingStrength.Strong) { Identifier = identifier },
            _ => Invalid(value),
        };
    }

    /// <summary>Kind and strength only (never the value).</summary>
    public override string ToString() => $"{nameof(AltSecurityIdentity)}({Kind} {Strength})";

    private static AltSecurityIdentity Invalid(string value) => new(value, AltSecurityIdentityKind.Invalid, MappingStrength.Invalid);

    /// <summary>
    /// Splits <c>&lt;TAG&gt;value&lt;TAG&gt;value…</c> into (canonical tag, trimmed value) pairs; false when the text does not start
    /// with a known tag or a value is empty. A <c>&lt;</c> escaped with a backslash belongs to the value.
    /// </summary>
    private static bool TryTokenize(ReadOnlySpan<char> text, out (string Tag, string Value)[] parts)
    {
        parts = [];
        if (!TryReadTag(text, 0, out var tag, out var valueStart))
        {
            return false;
        }

        var list = new List<(string, string)>();
        while (true)
        {
            string? nextTag = null;
            var nextValueStart = 0;
            var valueEnd = text.Length;
            for (var i = valueStart; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (text[i] == '<' && TryReadTag(text, i, out var found, out var after))
                {
                    nextTag = found;
                    nextValueStart = after;
                    valueEnd = i;
                    break;
                }
            }

            var value = text[valueStart..valueEnd].Trim().ToString();
            if (value.Length == 0)
            {
                return false;
            }

            list.Add((tag, value));
            if (nextTag is null)
            {
                parts = [.. list];
                return true;
            }

            tag = nextTag;
            valueStart = nextValueStart;
        }
    }

    private static bool TryReadTag(ReadOnlySpan<char> text, int position, out string tag, out int after)
    {
        tag = string.Empty;
        after = position;
        if (position >= text.Length || text[position] != '<')
        {
            return false;
        }

        var close = text[position..].IndexOf('>');
        if (close < 0)
        {
            return false;
        }

        var name = text.Slice(position + 1, close - 1);
        foreach (var known in Tags)
        {
            if (name.Equals(known, StringComparison.OrdinalIgnoreCase))
            {
                tag = known;
                after = position + close + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>The upper-case hex of an even-length hex string (of <paramref name="requiredLength"/> digits when given), else null.</summary>
    private static string? Hex(string value, int? requiredLength)
    {
        if (value.Length == 0 || value.Length % 2 != 0 || (requiredLength is { } length && value.Length != length))
        {
            return null;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return null;
            }
        }

        return value.ToUpperInvariant();
    }

    /// <summary>The certificate-order serial of a byte-reversed <c>&lt;SR&gt;</c> value (upper-case hex), else null.</summary>
    private static string? ReversedSerial(string value)
    {
        if (Hex(value, requiredLength: null) is not { } hex)
        {
            return null;
        }

        var builder = new StringBuilder(hex.Length);
        for (var i = hex.Length - 2; i >= 0; i -= 2)
        {
            builder.Append(hex, i, 2);
        }

        return builder.ToString();
    }
}
