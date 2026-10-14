using System.Buffers.Binary;
using AdMng.Core.ActiveDirectory;

namespace AdMng.Testing;

/// <summary>
/// Builds binary self-relative security descriptors (owner, group, DACL) like the ones AD returns for
/// nTSecurityDescriptor, msDS-AllowedToActOnBehalfOfOtherIdentity and msDS-GroupMSAMembership. Pure managed code, so
/// tests run on Linux (Samba also ignores SDDL strings written to nTSecurityDescriptor – tests write binary SDs).
/// </summary>
public static class FakeSecurityDescriptor
{
    private const ushort SelfRelative = 0x8000;
    private const ushort DaclPresent = 0x0004;

    /// <summary>Builds the binary SD.</summary>
    public static byte[] Build(string ownerSid, string? groupSid, params IEnumerable<FakeAce> aces)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);
        ArgumentNullException.ThrowIfNull(aces);
        var owner = AdConvert.SidToBytes(ownerSid);
        var group = groupSid is null ? [] : AdConvert.SidToBytes(groupSid);
        var aceBytes = aces.Select(EncodeAce).ToList();
        var aclSize = 8 + aceBytes.Sum(a => a.Length);
        var result = new byte[20 + owner.Length + group.Length + aclSize];
        var span = result.AsSpan();
        span[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], SelfRelative | DaclPresent);
        var offset = 20;
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], offset);
        owner.CopyTo(span[offset..]);
        offset += owner.Length;
        if (group.Length > 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], offset);
            group.CopyTo(span[offset..]);
            offset += group.Length;
        }

        BinaryPrimitives.WriteInt32LittleEndian(span[16..], offset);
        var acl = span[offset..];
        acl[0] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(acl[2..], (ushort)aclSize);
        BinaryPrimitives.WriteUInt16LittleEndian(acl[4..], (ushort)aceBytes.Count);
        var position = 8;
        foreach (var ace in aceBytes)
        {
            ace.CopyTo(acl[position..]);
            position += ace.Length;
        }

        return result;
    }

    private static byte[] EncodeAce(FakeAce ace)
    {
        var sid = AdConvert.SidToBytes(ace.Sid);
        var isObject = ace.Type is FakeAce.AccessAllowedObject or FakeAce.AccessDeniedObject;
        var size = 8 + sid.Length;
        if (isObject)
        {
            size += 4 + (ace.ObjectType is null ? 0 : 16) + (ace.InheritedObjectType is null ? 0 : 16);
        }

        var bytes = new byte[size];
        var span = bytes.AsSpan();
        span[0] = ace.Type;
        span[1] = ace.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)size);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], ace.Mask);
        var position = 8;
        if (isObject)
        {
            var flags = (ace.ObjectType is null ? 0 : 1) | (ace.InheritedObjectType is null ? 0 : 2);
            BinaryPrimitives.WriteInt32LittleEndian(span[position..], flags);
            position += 4;
            if (ace.ObjectType is { } objectType)
            {
                objectType.ToByteArray().CopyTo(span[position..]);
                position += 16;
            }

            if (ace.InheritedObjectType is { } inherited)
            {
                inherited.ToByteArray().CopyTo(span[position..]);
                position += 16;
            }
        }

        sid.CopyTo(span[position..]);
        return bytes;
    }
}
