using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using AdMng.Core.ActiveDirectory;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Managed parser of binary self-relative security descriptors (owner + DACL) and their comparison summary
/// (section 9.4): <c>owner:&lt;SID&gt;</c> and one <c>ace:…</c> value per ACE, sorted and de-duplicated, plus
/// <c>sha256:&lt;hex&gt;</c> of the raw descriptor as a cheap pre-check only. A different hash with an identical summary
/// set (ACE re-ordering, canonicalization, a rewrite without a semantic change) is no change.
/// </summary>
public sealed class SecurityDescriptorSummary
{
    public const string OwnerPrefix = "owner:";
    public const string AcePrefix = "ace:";
    public const string HashPrefix = "sha256:";

    private const int HeaderLength = 20;

    /// <summary>Names of the extended rights / properties operators recognise (shown instead of GUIDs).</summary>
    private static readonly Dictionary<Guid, string> KnownRights = new()
    {
        [new("1131f6aa-9c07-11d1-f79f-00c04fc2dcd2")] = "DS-Replication-Get-Changes",
        [new("1131f6ad-9c07-11d1-f79f-00c04fc2dcd2")] = "DS-Replication-Get-Changes-All",
        [new("89e95b76-444d-4c62-991a-0facbeda640c")] = "DS-Replication-Get-Changes-In-Filtered-Set",
        [new("1131f6ab-9c07-11d1-f79f-00c04fc2dcd2")] = "DS-Replication-Synchronize",
        [new("1131f6ac-9c07-11d1-f79f-00c04fc2dcd2")] = "DS-Replication-Manage-Topology",
        [new("00299570-246d-11d0-a768-00aa006e0529")] = "User-Force-Change-Password",
        [new("ab721a53-1e2f-11d0-9819-00aa0040529b")] = "User-Change-Password",
        [new("f3a64788-5306-11d1-a9c5-0000f80367c1")] = "Validated-SPN",
        [new("72e39547-7b18-11d1-adef-00c04fd8d5cd")] = "Validated-DNS-Host-Name",
        [new("bf9679c0-0de6-11d0-a285-00aa003049e2")] = "member",
        [new("5b47d60f-6090-40b2-9f37-2a4de88f3063")] = "msDS-KeyCredentialLink",
        [new("3f78c3e5-f79a-46bd-a0b8-9d18116ddc79")] = "msDS-AllowedToActOnBehalfOfOtherIdentity",
        [new("0e10c968-78fb-11d2-90d4-00c04f79dc55")] = "Certificate-Enrollment",
        [new("a05b8cc2-17bc-4802-a710-e7c15ab866a2")] = "Certificate-AutoEnrollment",
        [new("45ec5156-db7e-47bb-b53f-dbeb2d03c40f")] = "Reanimate-Tombstones",
        [new("4c164200-20c0-11d0-a768-00aa006e0529")] = "User-Account-Restrictions",
        [new("bc0ac240-79a9-11d0-9020-00c04fc2d4cf")] = "Membership",
        [new("f30e3bc1-9ff0-11d1-b603-0000f80367c1")] = "GP-Link",
        [new("bf967a7f-0de6-11d0-a285-00aa003049e2")] = "userCertificate",
        [new("00000000-0000-0000-0000-000000000000")] = "All",
    };

    private SecurityDescriptorSummary(string? owner, IReadOnlyList<SecurityDescriptorAce> aces, string sha256)
    {
        Owner = owner;
        Aces = aces;
        Sha256 = sha256;
    }

    /// <summary>Owner SID, when present.</summary>
    public string? Owner { get; }

    /// <summary>DACL entries in descriptor order.</summary>
    public IReadOnlyList<SecurityDescriptorAce> Aces { get; }

    /// <summary>SHA-256 (lower-case hex) of the raw descriptor.</summary>
    public string Sha256 { get; }

    /// <summary>Parses a binary self-relative descriptor. Never throws; false for malformed input.</summary>
    public static bool TryParse(ReadOnlySpan<byte> descriptor, out SecurityDescriptorSummary? summary)
    {
        summary = null;
        if (descriptor.Length < HeaderLength || descriptor[0] != 1)
        {
            return false;
        }

        var ownerOffset = BinaryPrimitives.ReadInt32LittleEndian(descriptor[4..]);
        var daclOffset = BinaryPrimitives.ReadInt32LittleEndian(descriptor[16..]);
        string? owner = null;
        if (ownerOffset != 0 && !TryReadSid(descriptor, ownerOffset, out owner, out _))
        {
            return false;
        }

        var aces = new List<SecurityDescriptorAce>();
        if (daclOffset != 0 && !TryReadAcl(descriptor, daclOffset, aces))
        {
            return false;
        }

        summary = new SecurityDescriptorSummary(owner, aces, Convert.ToHexStringLower(SHA256.HashData(descriptor)));
        return true;
    }

    /// <summary>The stored values: <c>sha256:…</c> first, then <c>owner:…</c>, then the sorted distinct ACE values.</summary>
    public IReadOnlyList<string> ToValues()
    {
        var values = new List<string> { HashPrefix + Sha256 };
        if (Owner is not null)
        {
            values.Add(OwnerPrefix + Owner);
        }

        values.AddRange(Aces.Select(a => a.ToSummaryValue()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return values;
    }

    /// <summary>The comparison set of stored values: everything except the hash pre-check.</summary>
    public static IReadOnlySet<string> ComparisonSet(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Where(v => !v.StartsWith(HashPrefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The <c>sha256:…</c> value of stored values, or null.</summary>
    public static string? HashOf(IEnumerable<string> values) =>
        values.FirstOrDefault(v => v.StartsWith(HashPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Human-readable (English, language-neutral notation) form of a stored value for change lists:
    /// <c>owner S-1-…</c>, <c>allow DS-Replication-Get-Changes-All S-1-…</c>, <c>deny write property member S-1-…</c>.
    /// </summary>
    public static string Describe(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.StartsWith(OwnerPrefix, StringComparison.Ordinal))
        {
            return "owner " + value[OwnerPrefix.Length..];
        }

        return SecurityDescriptorAce.FromSummaryValue(value) is { } ace ? Describe(ace) : value;
    }

    /// <summary>Describes one ACE: allow/deny, rights (extended rights by name), trustee SID.</summary>
    public static string Describe(SecurityDescriptorAce ace)
    {
        ArgumentNullException.ThrowIfNull(ace);
        var rights = RightsText(ace);
        return string.Create(CultureInfo.InvariantCulture, $"{(ace.IsAllow ? "allow" : "deny")} {rights} {ace.Sid}");
    }

    /// <summary>Known extended right or property name of a GUID, or null.</summary>
    public static string? KnownRightName(Guid guid) => KnownRights.TryGetValue(guid, out var name) ? name : null;

    private static string RightsText(SecurityDescriptorAce ace)
    {
        var mask = ace.Mask;
        if ((mask & 0x000F01FF) == 0x000F01FF || (mask & 0x10000000) != 0)
        {
            return "full control";
        }

        var parts = new List<string>();
        var target = ace.ObjectType is { } objectType ? KnownRightName(objectType) ?? objectType.ToString("D") : null;
        if ((mask & 0x100) != 0)
        {
            parts.Add(target ?? "all extended rights");
        }

        if ((mask & 0x20) != 0)
        {
            parts.Add(target is null ? "write property" : $"write property {target}");
        }

        if ((mask & 0x08) != 0)
        {
            parts.Add(target is null ? "validated write" : $"validated write {target}");
        }

        if ((mask & 0x40000) != 0)
        {
            parts.Add("write DACL");
        }

        if ((mask & 0x80000) != 0)
        {
            parts.Add("write owner");
        }

        if ((mask & 0x01) != 0)
        {
            parts.Add("create child");
        }

        if ((mask & 0x02) != 0 || (mask & 0x10000) != 0)
        {
            parts.Add("delete");
        }

        if (parts.Count == 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"0x{mask:X8}"));
        }

        return string.Join(", ", parts);
    }

    private static bool TryReadAcl(ReadOnlySpan<byte> descriptor, int offset, List<SecurityDescriptorAce> aces)
    {
        if (offset < HeaderLength || offset + 8 > descriptor.Length)
        {
            return false;
        }

        var acl = descriptor[offset..];
        var aclSize = BinaryPrimitives.ReadUInt16LittleEndian(acl[2..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(acl[4..]);
        if (aclSize < 8 || aclSize > acl.Length)
        {
            return false;
        }

        var position = 8;
        for (var i = 0; i < count; i++)
        {
            if (position + 8 > aclSize)
            {
                return false;
            }

            var header = acl[position..];
            var type = header[0];
            var flags = header[1];
            var size = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
            if (size < 8 || position + size > aclSize)
            {
                return false;
            }

            var body = acl.Slice(position, size);
            var mask = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
            var sidOffset = 8;
            Guid? objectType = null;
            Guid? inheritedObjectType = null;
            if (IsObjectAce(type))
            {
                if (size < 12)
                {
                    return false;
                }

                var objectFlags = BinaryPrimitives.ReadInt32LittleEndian(body[8..]);
                sidOffset = 12;
                if ((objectFlags & 1) != 0)
                {
                    if (sidOffset + 16 > size)
                    {
                        return false;
                    }

                    objectType = new Guid(body.Slice(sidOffset, 16));
                    sidOffset += 16;
                }

                if ((objectFlags & 2) != 0)
                {
                    if (sidOffset + 16 > size)
                    {
                        return false;
                    }

                    inheritedObjectType = new Guid(body.Slice(sidOffset, 16));
                    sidOffset += 16;
                }
            }

            if (!TryReadSid(body, sidOffset, out var sid, out _))
            {
                return false;
            }

            aces.Add(new SecurityDescriptorAce
            {
                Type = type,
                Flags = flags,
                Mask = mask,
                Sid = sid!,
                ObjectType = objectType,
                InheritedObjectType = inheritedObjectType,
            });
            position += size;
        }

        return true;
    }

    private static bool IsObjectAce(byte type) => type is 0x05 or 0x06 or 0x07 or 0x08 or 0x0B or 0x0C or 0x0F or 0x10;

    private static bool TryReadSid(ReadOnlySpan<byte> data, int offset, out string? sid, out int length)
    {
        sid = null;
        length = 0;
        if (offset < 0 || offset + 8 > data.Length)
        {
            return false;
        }

        var subAuthorities = data[offset + 1];
        length = 8 + (subAuthorities * 4);
        if (offset + length > data.Length)
        {
            return false;
        }

        sid = AdConvert.SidToString(data.Slice(offset, length));
        return true;
    }
}
