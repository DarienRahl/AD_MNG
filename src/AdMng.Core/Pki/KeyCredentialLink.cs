using System.Buffers.Binary;
using System.Security.Cryptography;
using AdMng.Core.Directory;

namespace AdMng.Core.Pki;

/// <summary>Where a key credential comes from (KEYCREDENTIALLINK_ENTRY KeySource; <c>Cert.KeySource.&lt;KeyCredentialSource&gt;</c>).</summary>
public enum KeyCredentialSource
{
    /// <summary><c>KEY_SOURCE_AD</c> (0x00).</summary>
    ActiveDirectory,

    /// <summary><c>KEY_SOURCE_AZURE</c> (0x01).</summary>
    EntraId,

    /// <summary>Absent or another value.</summary>
    Unknown,
}

/// <summary>What a key credential is for (KEYCREDENTIALLINK_ENTRY KeyUsage; <c>Cert.KeyUsage.&lt;KeyCredentialUsage&gt;</c>).</summary>
public enum KeyCredentialUsage
{
    /// <summary>0x00 – admin key.</summary>
    AdminKey,

    /// <summary>0x01 – <c>KEY_USAGE_NGC</c> (Windows Hello for Business).</summary>
    Ngc,

    /// <summary>0x02 – transport key (STK).</summary>
    Stk,

    /// <summary>0x03 – BitLocker recovery.</summary>
    BitLockerRecovery,

    /// <summary>0x04 – other.</summary>
    Other,

    /// <summary>0x07 – <c>KEY_USAGE_FIDO</c>.</summary>
    Fido,

    /// <summary>0x08 – <c>KEY_USAGE_FEK</c> (file encryption key).</summary>
    Fek,

    /// <summary>0x09 – DPAPI.</summary>
    Dpapi,

    /// <summary>Absent or another value.</summary>
    Unknown,
}

/// <summary>
/// One value of <c>msDS-KeyCredentialLink</c> (docs/dev/p-certificate-services.md 2.4): the metadata of the
/// KEYCREDENTIALLINK_BLOB (version <c>0x0200</c>) – never the key material beyond the id (rule 2). <see cref="ToString"/>
/// carries the kinds only.
/// </summary>
/// <param name="KeyId">The KeyID entry (0x01), Base64 as <c>Get-ADUser</c> tools show it, or null.</param>
/// <param name="DeviceId">The DeviceId entry (0x06), or null.</param>
/// <param name="Source">KeySource (0x05).</param>
/// <param name="Usage">KeyUsage (0x04).</param>
/// <param name="Created">KeyCreationTime (0x09), or null.</param>
/// <param name="LastLogon">KeyApproximateLastLogonTimeStamp (0x08), or null.</param>
/// <param name="IsMalformed">The value could not be parsed (the row stays, the fields are null).</param>
public sealed record KeyCredentialLink(
    string? KeyId,
    Guid? DeviceId,
    KeyCredentialSource Source,
    KeyCredentialUsage Usage,
    DateTimeOffset? Created,
    DateTimeOffset? LastLogon,
    bool IsMalformed)
{
    /// <summary>The KEYCREDENTIALLINK_BLOB version this parser reads (<c>KEY_CREDENTIAL_LINK_VERSION_2</c>, MS-ADTS 2.2.20).</summary>
    private const uint Version2 = 0x0200;

    /// <summary>
    /// The length of the KeyMaterial entry (0x03) in bytes (0 when absent) – the only thing exposed about the key itself
    /// (rule 2; the KeyID is its SHA-256 fingerprint).
    /// </summary>
    public int KeyMaterialLength { get; init; }

    /// <summary>
    /// The KeyHash entry (0x02) equals the SHA-256 of every entry after it (MS-ADTS 2.2.20.1); null when the blob has no KeyHash.
    /// A false value means the entries were changed after the hash was computed.
    /// </summary>
    public bool? IsKeyHashValid { get; init; }

    /// <summary>The DN part of the DN-Binary value (the object the credential belongs to), or null when the value is not DN-Binary.</summary>
    public string? OwnerDn { get; init; }

    /// <summary>
    /// Parses the values as <c>GetStrings</c> delivers them (<c>B:&lt;hexlen&gt;:&lt;hex&gt;:&lt;dn&gt;</c>, split with
    /// <c>WellKnownObjects.TryParseValue</c>); one entry per value, malformed ones marked. The blob is version <c>0x0200</c>
    /// followed by entries of a 2-byte little-endian length, a 1-byte identifier and the value; KeyID is shown as Base64 (as
    /// DSInternals), the times are FILETIME for the AD source and <c>DateTime.ToBinary</c> for Entra ID (as DSInternals reads
    /// them), the DeviceId a GUID. A value that is not DN-Binary, has another version, a truncated entry or a malformed known
    /// entry is <see cref="IsMalformed"/> (its fields null); unknown entry identifiers are skipped.
    /// </summary>
    public static IReadOnlyList<KeyCredentialLink> Parse(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new List<KeyCredentialLink>();
        foreach (var value in values)
        {
            result.Add(ParseOne(value));
        }

        return result;
    }

    /// <summary>Source, usage and whether it parsed only (never the key id or device id).</summary>
    public override string ToString() => $"{nameof(KeyCredentialLink)}({Source} {Usage}{(IsMalformed ? ", malformed" : string.Empty)})";

    private static readonly KeyCredentialLink Malformed = new(null, null, KeyCredentialSource.Unknown, KeyCredentialUsage.Unknown, null, null, IsMalformed: true);

    private static KeyCredentialLink ParseOne(string? value)
    {
        if (!WellKnownObjects.TryParseValue(value, out var hex, out var ownerDn))
        {
            return Malformed;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return Malformed;
        }

        return TryParseBlob(blob, out var link) ? link with { OwnerDn = ownerDn } : Malformed with { OwnerDn = ownerDn };
    }

    private static bool TryParseBlob(ReadOnlySpan<byte> blob, out KeyCredentialLink link)
    {
        link = Malformed;
        if (blob.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(blob) != Version2)
        {
            return false;
        }

        string? keyId = null;
        Guid? deviceId = null;
        var source = KeyCredentialSource.Unknown;
        var usage = KeyCredentialUsage.Unknown;
        long? created = null;
        long? lastLogon = null;
        var keyMaterialLength = 0;
        byte[]? keyHash = null;
        var hashedFrom = -1;
        var position = 4;
        while (position < blob.Length)
        {
            if (blob.Length - position < 3)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt16LittleEndian(blob[position..]);
            var identifier = blob[position + 2];
            var start = position + 3;
            if (blob.Length - start < length)
            {
                return false;
            }

            var entry = blob.Slice(start, length);
            position = start + length;
            switch (identifier)
            {
                case 0x01:
                    // KeyID: the SHA-256 of the key material (32 bytes); Base64 as DSInternals shows version 2 ids.
                    if (length == 0)
                    {
                        return false;
                    }

                    keyId = Convert.ToBase64String(entry);
                    break;
                case 0x02:
                    keyHash = entry.ToArray();
                    hashedFrom = position;
                    break;
                case 0x03:
                    keyMaterialLength = length;
                    break;
                case 0x04:
                    // A single byte in version 2 (a string in the legacy versions – then Unknown).
                    usage = length == 1 ? UsageOf(entry[0]) : KeyCredentialUsage.Unknown;
                    break;
                case 0x05:
                    source = length == 1 ? SourceOf(entry[0]) : KeyCredentialSource.Unknown;
                    break;
                case 0x06:
                    if (length != 16)
                    {
                        return false;
                    }

                    deviceId = new Guid(entry);
                    break;
                case 0x08:
                case 0x09:
                    if (length != 8)
                    {
                        return false;
                    }

                    var stamp = BinaryPrimitives.ReadInt64LittleEndian(entry);
                    if (identifier == 0x08)
                    {
                        lastLogon = stamp;
                    }
                    else
                    {
                        created = stamp;
                    }

                    break;
                default:
                    // 0x07 CustomKeyInformation and anything newer: not shown in P1.
                    break;
            }
        }

        bool? hashValid = keyHash is null
            ? null
            : keyHash.Length == SHA256.HashSizeInBytes && SHA256.HashData(blob[hashedFrom..]).AsSpan().SequenceEqual(keyHash);
        link = new KeyCredentialLink(keyId, deviceId, source, usage, TimeOf(created, source), TimeOf(lastLogon, source), IsMalformed: false)
        {
            KeyMaterialLength = keyMaterialLength,
            IsKeyHashValid = hashValid,
        };
        return true;
    }

    private static KeyCredentialUsage UsageOf(byte value) => value switch
    {
        0x00 => KeyCredentialUsage.AdminKey,
        0x01 => KeyCredentialUsage.Ngc,
        0x02 => KeyCredentialUsage.Stk,
        0x03 => KeyCredentialUsage.BitLockerRecovery,
        0x04 => KeyCredentialUsage.Other,
        0x07 => KeyCredentialUsage.Fido,
        0x08 => KeyCredentialUsage.Fek,
        0x09 => KeyCredentialUsage.Dpapi,
        _ => KeyCredentialUsage.Unknown,
    };

    private static KeyCredentialSource SourceOf(byte value) => value switch
    {
        0x00 => KeyCredentialSource.ActiveDirectory,
        0x01 => KeyCredentialSource.EntraId,
        _ => KeyCredentialSource.Unknown,
    };

    /// <summary>
    /// A version-2 timestamp: <c>DateTime.ToBinary</c> for Entra ID keys, FILETIME (UTC) otherwise (as DSInternals); null when
    /// absent, zero or out of range.
    /// </summary>
    private static DateTimeOffset? TimeOf(long? stamp, KeyCredentialSource source)
    {
        if (stamp is not { } value || value == 0)
        {
            return null;
        }

        try
        {
            if (source != KeyCredentialSource.EntraId)
            {
                return value > 0 ? new DateTimeOffset(DateTime.FromFileTimeUtc(value)) : null;
            }

            var time = DateTime.FromBinary(value);
            return new DateTimeOffset(time.Kind switch
            {
                DateTimeKind.Local => time.ToUniversalTime(),
                DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
                _ => time,
            });
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
