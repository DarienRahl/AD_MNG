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

    /// <summary>
    /// What AD stores when <paramref name="written"/> is written with the SD flags control <paramref name="flags"/>: the
    /// named parts (owner, group, DACL, SACL – with their control bits) come from <paramref name="written"/>, the others
    /// stay as in <paramref name="stored"/> (null = the object had no descriptor). Null when a descriptor is malformed or
    /// a named part is missing from <paramref name="written"/>.
    /// </summary>
    public static byte[]? Merge(byte[]? stored, byte[] written, SecurityDescriptorRights flags)
    {
        ArgumentNullException.ThrowIfNull(written);
        if (!TrySplit(written, out var newParts) || (stored is not null && !TrySplit(stored, out _)))
        {
            return null;
        }

        var oldParts = stored is not null && TrySplit(stored, out var parsed) ? parsed : new Parts(0, null, null, null, null);
        Parts Pick(SecurityDescriptorRights part) => flags.HasFlag(part) ? newParts : oldParts;
        var owner = Pick(SecurityDescriptorRights.Owner).Owner;
        var group = Pick(SecurityDescriptorRights.Group).Group;
        var daclSource = Pick(SecurityDescriptorRights.Dacl);
        var saclSource = Pick(SecurityDescriptorRights.Sacl);
        if ((flags.HasFlag(SecurityDescriptorRights.Owner) && owner is null) || (flags.HasFlag(SecurityDescriptorRights.Dacl) && daclSource.Dacl is null))
        {
            return null;
        }

        const ushort DaclBits = 0x0004 | 0x0008 | 0x0100 | 0x0400 | 0x1000;
        const ushort SaclBits = 0x0010 | 0x0020 | 0x0200 | 0x0800 | 0x2000;
        var control = (ushort)(SelfRelative | (daclSource.Control & DaclBits) | (saclSource.Control & SaclBits));
        var length = 20 + (owner?.Length ?? 0) + (group?.Length ?? 0) + (saclSource.Sacl?.Length ?? 0) + (daclSource.Dacl?.Length ?? 0);
        var result = new byte[length];
        result[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), control);
        var offset = 20;
        offset = Put(result, owner, 4, offset);
        offset = Put(result, group, 8, offset);
        offset = Put(result, saclSource.Sacl, 12, offset);
        Put(result, daclSource.Dacl, 16, offset);
        return result;
    }

    private static int Put(byte[] target, byte[]? part, int field, int offset)
    {
        if (part is null)
        {
            return offset;
        }

        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(field), offset);
        part.CopyTo(target, offset);
        return offset + part.Length;
    }

    private static byte[]? ReadSid(byte[] descriptor, int field)
    {
        var offset = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(field));
        if (offset <= 0 || offset + 8 > descriptor.Length)
        {
            return null;
        }

        var length = 8 + (descriptor[offset + 1] * 4);
        return offset + length <= descriptor.Length ? descriptor.AsSpan(offset, length).ToArray() : null;
    }

    private static byte[]? ReadAcl(byte[] descriptor, int field, ushort control, ushort presentBit)
    {
        var offset = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(field));
        if ((control & presentBit) == 0 || offset <= 0 || offset + 8 > descriptor.Length)
        {
            return null;
        }

        var size = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(offset + 2));
        return size >= 8 && offset + size <= descriptor.Length ? descriptor.AsSpan(offset, size).ToArray() : null;
    }

    private static bool TrySplit(byte[] descriptor, out Parts parts)
    {
        parts = new Parts(0, null, null, null, null);
        if (descriptor.Length < 20 || descriptor[0] != 1)
        {
            return false;
        }

        var control = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(2));
        parts = new Parts(control, ReadSid(descriptor, 4), ReadSid(descriptor, 8), ReadAcl(descriptor, 12, control, 0x0010), ReadAcl(descriptor, 16, control, DaclPresent));
        return true;
    }

    private sealed record Parts(ushort Control, byte[]? Owner, byte[]? Group, byte[]? Sacl, byte[]? Dacl);

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
