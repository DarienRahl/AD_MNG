using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// LDAP values → canonical comparison values (section 9.4): DN values by GUID (Extended DN), SIDs as text, FILETIME and
/// integers as invariant decimals, GeneralizedTime as ISO 8601 UTC, security descriptors as owner/ACE summaries,
/// msDS-KeyCredentialLink as the hash of its binary part only (the DN part changes on every rename), certificates and
/// other binary values as SHA-256. Multi-valued attributes are stored sorted (comparison as sets).
/// </summary>
public sealed class ValueCanonicalizer
{
    public const string GuidPrefix = "guid:";
    public const string SidPrefix = "sid:";
    public const string DnPrefix = "dn:";
    public const string HashPrefix = "sha256:";

    /// <summary>Flag attributes (rendered as flag names by the formatters).</summary>
    private static readonly HashSet<string> FlagAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.UserAccountControl, Attr.GroupType, WatchdogAttributes.GpOptions, WatchdogAttributes.TrustAttributes,
        WatchdogAttributes.CertificateNameFlag, WatchdogAttributes.EnrollmentFlag, WatchdogAttributes.PwdProperties,
        Attr.SupportedEncryptionTypes,
    };

    /// <summary>Security descriptor attributes summarised as owner/ACE values.</summary>
    private static readonly HashSet<string> DescriptorAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.NtSecurityDescriptor, WatchdogAttributes.AllowedToActOnBehalfOfOtherIdentity, WatchdogAttributes.GroupMsaMembership,
    };

    /// <summary>Certificates: SHA-256 comparison value, SHA-1 thumbprint as display value.</summary>
    private static readonly HashSet<string> CertificateAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        WatchdogAttributes.UserCertificate, WatchdogAttributes.CaCertificate,
    };

    private static readonly HashSet<string> FileTimeAttributes = new(Attr.FileTimeAttributes, StringComparer.OrdinalIgnoreCase)
    {
        Attr.PwdLastSet, Attr.AccountExpires, Attr.LockoutTime,
    };

    private readonly ISchemaCatalog? _schema;

    public ValueCanonicalizer(ISchemaCatalog? schema)
    {
        _schema = schema;
    }

    /// <summary>Value kind of an attribute for <see cref="AttributeChange.ValueKind"/>.</summary>
    public AttributeValueKind KindOf(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        if (FlagAttributes.Contains(attribute))
        {
            return AttributeValueKind.Flags;
        }

        if (FileTimeAttributes.Contains(attribute))
        {
            return AttributeValueKind.FileTime;
        }

        if (DescriptorAttributes.Contains(attribute) || CertificateAttributes.Contains(attribute)
            || attribute.Equals(WatchdogAttributes.KeyCredentialLink, StringComparison.OrdinalIgnoreCase))
        {
            return AttributeValueKind.Hash;
        }

        if (attribute.Equals(Attr.LogonHours, StringComparison.OrdinalIgnoreCase))
        {
            return AttributeValueKind.Binary;
        }

        return _schema?.FindAttribute(attribute)?.Syntax switch
        {
            AttributeSyntax.DistinguishedName => AttributeValueKind.Dn,
            AttributeSyntax.Sid => AttributeValueKind.Sid,
            AttributeSyntax.Boolean => AttributeValueKind.Boolean,
            AttributeSyntax.Integer or AttributeSyntax.LargeInteger or AttributeSyntax.Enumeration => AttributeValueKind.Number,
            AttributeSyntax.GeneralizedTime or AttributeSyntax.UtcTime => AttributeValueKind.GeneralizedTime,
            AttributeSyntax.OctetString or AttributeSyntax.ReplicaLink or AttributeSyntax.SecurityDescriptor => AttributeValueKind.Binary,
            _ => AttributeValueKind.Text,
        };
    }

    /// <summary>Canonical snapshot attribute of the returned values; null when there are none.</summary>
    public SnapshotAttribute? Canonicalize(string attribute, AttributeValues? values)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var kind = KindOf(attribute);
        var pairs = new List<(string Value, string Display)>(values.Count);
        foreach (var raw in values)
        {
            pairs.AddRange(CanonicalPairs(attribute, kind, raw));
        }

        if (pairs.Count == 0)
        {
            return null;
        }

        if (!DescriptorAttributes.Contains(attribute))
        {
            pairs = pairs.DistinctBy(p => p.Value, StringComparer.Ordinal).OrderBy(p => p.Value, StringComparer.Ordinal).ToList();
        }

        var display = pairs.Select(p => p.Display).ToList();
        var canonical = pairs.Select(p => p.Value).ToList();
        return new SnapshotAttribute
        {
            Values = canonical,
            DisplayValues = display.SequenceEqual(canonical, StringComparer.Ordinal) ? [] : display,
            Kind = kind,
        };
    }

    /// <summary>
    /// The member key of a DN value (section 9.5): GUID "N" when the Extended DN carries a GUID, else <c>sid:…</c>, else
    /// <c>dn:&lt;normalized dn&gt;</c>.
    /// </summary>
    public static MemberReference ToMemberReference(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (ExtendedDistinguishedName.TryParse(value, out var extended))
        {
            var key = extended.Guid is { } guid ? guid.ToString("N")
                : extended.Sid is { } sid ? SidPrefix + sid
                : DnPrefix + NormalizeDn(extended.Dn);
            return new MemberReference { Key = key, ObjectGuid = extended.Guid, Sid = extended.Sid, Dn = extended.Dn };
        }

        return new MemberReference { Key = DnPrefix + NormalizeDn(value), Dn = value };
    }

    /// <summary>The normalized (parsed, upper-case) DN used in <c>dn:</c> keys.</summary>
    public static string NormalizeDn(string dn) =>
        DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString().ToUpperInvariant() : dn.ToUpperInvariant();

    /// <summary>The comparison value of a DN value (<c>guid:</c>, <c>sid:</c> or <c>dn:</c>) and its display DN.</summary>
    public static (string Value, string Display) CanonicalDn(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ExtendedDistinguishedName.TryParse(value, out var extended))
        {
            return (DnPrefix + NormalizeDn(value), value);
        }

        var canonical = extended.Guid is { } guid ? GuidPrefix + guid.ToString("D")
            : extended.Sid is { } sid ? SidPrefix + sid
            : DnPrefix + NormalizeDn(extended.Dn);
        return (canonical, extended.Dn);
    }

    /// <summary>The GUID of a canonical DN value (<c>guid:…</c>), or null.</summary>
    public static Guid? GuidOf(string canonicalValue) =>
        canonicalValue.StartsWith(GuidPrefix, StringComparison.Ordinal) && Guid.TryParse(canonicalValue.AsSpan(GuidPrefix.Length), out var guid)
            ? guid
            : null;

    /// <summary>SHA-256 hash value (<c>sha256:&lt;hex&gt;</c>).</summary>
    public static string Hash(ReadOnlySpan<byte> data) => HashPrefix + Convert.ToHexStringLower(SHA256.HashData(data));

    private IEnumerable<(string Value, string Display)> CanonicalPairs(string attribute, AttributeValueKind kind, object raw)
    {
        if (DescriptorAttributes.Contains(attribute))
        {
            var bytes = AsBytes(raw);
            if (SecurityDescriptorSummary.TryParse(bytes, out var summary))
            {
                return summary!.ToValues().Select(v => (v, v));
            }

            var hash = Hash(bytes);
            return [(hash, hash)];
        }

        if (attribute.Equals(WatchdogAttributes.KeyCredentialLink, StringComparison.OrdinalIgnoreCase))
        {
            var binary = DnBinaryPart(AsString(raw));
            var hash = Hash(Encoding.UTF8.GetBytes(binary.ToUpperInvariant()));
            return [(hash, hash)];
        }

        if (CertificateAttributes.Contains(attribute))
        {
            var bytes = AsBytes(raw);
            return [(Hash(bytes), Convert.ToHexString(SHA1.HashData(bytes)))];
        }

        if (attribute.Equals(Attr.LogonHours, StringComparison.OrdinalIgnoreCase))
        {
            var hex = Convert.ToHexStringLower(AsBytes(raw));
            return [(hex, hex)];
        }

        var syntax = _schema?.FindAttribute(attribute)?.Syntax;
        if (syntax == AttributeSyntax.Sid || (raw is byte[] && IsSidAttribute(attribute)))
        {
            var sid = raw is byte[] sidBytes && sidBytes.Length >= 8 ? AdConvert.SidToString(sidBytes) : AsString(raw);
            return [(sid, sid)];
        }

        if (raw is byte[] guidBytes && guidBytes.Length == 16 && IsGuidAttribute(attribute))
        {
            var guid = new Guid(guidBytes).ToString("D");
            return [(guid, guid)];
        }

        if (syntax == AttributeSyntax.DistinguishedName || kind == AttributeValueKind.Dn)
        {
            return [CanonicalDn(AsString(raw))];
        }

        switch (kind)
        {
            case AttributeValueKind.Number or AttributeValueKind.Flags or AttributeValueKind.FileTime:
                var text = AsString(raw);
                var number = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed.ToString(CultureInfo.InvariantCulture)
                    : text;
                return [(number, number)];
            case AttributeValueKind.Boolean:
                var flag = AsString(raw).ToUpperInvariant();
                return [(flag, flag)];
            case AttributeValueKind.GeneralizedTime:
                var time = AdConvert.FromGeneralizedTime(AsString(raw)) is { } timestamp
                    ? timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                    : AsString(raw);
                return [(time, time)];
        }

        if (raw is byte[] || syntax is AttributeSyntax.OctetString or AttributeSyntax.ReplicaLink)
        {
            var hash = Hash(AsBytes(raw));
            return [(hash, hash)];
        }

        var value = AsString(raw);
        return [(value, value)];
    }

    /// <summary>The binary part of a DN-Binary value <c>B:&lt;len&gt;:&lt;hex&gt;:&lt;dn&gt;</c>; the whole value when it is not one.</summary>
    public static string DnBinaryPart(string value) => TrySplitDnBinary(value, out var binary, out _) ? binary : value;

    /// <summary>Splits a DN-Binary value <c>B:&lt;len&gt;:&lt;hex&gt;:&lt;dn&gt;</c> into the hex part and the DN part.</summary>
    public static bool TrySplitDnBinary(string value, out string binary, out string dn)
    {
        ArgumentNullException.ThrowIfNull(value);
        binary = dn = string.Empty;
        if (!value.StartsWith("B:", StringComparison.Ordinal))
        {
            return false;
        }

        var lengthEnd = value.IndexOf(':', 2);
        if (lengthEnd < 0 || !int.TryParse(value.AsSpan(2, lengthEnd - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || lengthEnd + 1 + length >= value.Length || value[lengthEnd + 1 + length] != ':')
        {
            return false;
        }

        binary = value.Substring(lengthEnd + 1, length);
        dn = value[(lengthEnd + 2 + length)..];
        return true;
    }

    private static bool IsSidAttribute(string attribute) =>
        attribute.Equals(Attr.ObjectSid, StringComparison.OrdinalIgnoreCase) || attribute.Equals(Attr.SidHistory, StringComparison.OrdinalIgnoreCase)
        || attribute.Equals(WatchdogAttributes.CreatorSid, StringComparison.OrdinalIgnoreCase);

    private static bool IsGuidAttribute(string attribute) =>
        attribute.EndsWith("guid", StringComparison.OrdinalIgnoreCase)
        || attribute.Equals(WatchdogAttributes.InvocationId, StringComparison.OrdinalIgnoreCase);

    private static byte[] AsBytes(object value) => value as byte[] ?? Encoding.UTF8.GetBytes((string)value);

    private static string AsString(object value) => value as string ?? Encoding.UTF8.GetString((byte[])value);
}
