using System.Buffers.Binary;
using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.Security;

/// <summary>
/// Serializes binary self-relative security descriptors (owner, group, DACL; never a SACL) and single ACEs – the
/// counterpart of <see cref="SecurityDescriptorSummary"/>, which parses them. Used by <see cref="DaclEditor"/> for the
/// DACL-only descriptors of <see cref="Batch.ChangeAction.ModifySecurity"/> (written with the SD flags control
/// <c>DACL_SECURITY_INFORMATION</c>) and by the Group Policy folder permissions (K2).
/// </summary>
public static class SecurityDescriptorWriter
{
    /// <summary>SE_DACL_PRESENT.</summary>
    public const ushort DaclPresent = 0x0004;

    /// <summary>SE_DACL_DEFAULTED.</summary>
    public const ushort DaclDefaulted = 0x0008;

    /// <summary>SE_DACL_AUTO_INHERIT_REQ.</summary>
    public const ushort DaclAutoInheritRequired = 0x0100;

    /// <summary>SE_DACL_AUTO_INHERITED.</summary>
    public const ushort DaclAutoInherited = 0x0400;

    /// <summary>SE_DACL_PROTECTED: the DACL does not inherit ACEs from the parent ("inheritance disabled").</summary>
    public const ushort DaclProtected = 0x1000;

    /// <summary>SE_SELF_RELATIVE.</summary>
    public const ushort SelfRelative = 0x8000;

    /// <summary>ACL_REVISION (plain ACEs only).</summary>
    public const byte AclRevision = 2;

    /// <summary>ACL_REVISION_DS (needed as soon as the ACL holds an object ACE).</summary>
    public const byte AclRevisionDs = 4;

    /// <summary>The largest ACL a descriptor can hold (its size field is 16 bits).</summary>
    public const int MaxAclSize = ushort.MaxValue;

    private const int HeaderLength = 20;
    private const int AclHeaderLength = 8;

    /// <summary>
    /// Builds a self-relative descriptor. <paramref name="daclAces"/> null = no DACL (SE_DACL_PRESENT cleared – which
    /// means "everyone full control"; callers that edit a DACL never pass null), empty = an empty DACL.
    /// <see cref="SelfRelative"/> is always set; <see cref="DaclPresent"/> follows <paramref name="daclAces"/>.
    /// </summary>
    /// <param name="control">Control bits (SACL bits are ignored – no SACL is written).</param>
    /// <param name="ownerSid">Owner SID or null.</param>
    /// <param name="groupSid">Primary group SID or null.</param>
    /// <param name="daclAces">Encoded ACEs (<see cref="EncodeAce(AceSpec)"/> or raw ACEs of a parsed descriptor) in order.</param>
    /// <param name="aclRevision">ACL revision; raised to <see cref="AclRevisionDs"/> when an object ACE is present.</param>
    /// <exception cref="InvalidOperationException">The ACEs do not fit into one ACL.</exception>
    public static byte[] Build(ushort control, string? ownerSid, string? groupSid, IReadOnlyList<ReadOnlyMemory<byte>>? daclAces, byte aclRevision = AclRevision)
    {
        var owner = ownerSid is null ? [] : AdConvert.SidToBytes(ownerSid);
        var group = groupSid is null ? [] : AdConvert.SidToBytes(groupSid);
        var aclSize = 0;
        if (daclAces is not null)
        {
            aclSize = AclHeaderLength + daclAces.Sum(a => a.Length);
            if (aclSize > MaxAclSize)
            {
                throw new InvalidOperationException("The access control list would be larger than 64 KB.");
            }

            if (daclAces.Any(a => a.Length >= 1 && IsObjectAceType(a.Span[0])))
            {
                aclRevision = Math.Max(aclRevision, AclRevisionDs);
            }
        }

        var result = new byte[HeaderLength + owner.Length + group.Length + aclSize];
        var span = result.AsSpan();
        span[0] = 1;
        var effectiveControl = (ushort)((control & ~(DaclPresent | 0x0010 | 0x0020 | 0x0200 | 0x0800 | 0x2000)) | SelfRelative);
        if (daclAces is not null)
        {
            effectiveControl |= DaclPresent;
        }
        else
        {
            effectiveControl &= unchecked((ushort)~(DaclProtected | DaclAutoInherited | DaclAutoInheritRequired | DaclDefaulted));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], effectiveControl);
        var offset = HeaderLength;
        if (owner.Length > 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], offset);
            owner.CopyTo(span[offset..]);
            offset += owner.Length;
        }

        if (group.Length > 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], offset);
            group.CopyTo(span[offset..]);
            offset += group.Length;
        }

        if (daclAces is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[16..], offset);
            var acl = span[offset..];
            acl[0] = aclRevision;
            BinaryPrimitives.WriteUInt16LittleEndian(acl[2..], (ushort)aclSize);
            BinaryPrimitives.WriteUInt16LittleEndian(acl[4..], (ushort)daclAces.Count);
            var position = AclHeaderLength;
            foreach (var ace in daclAces)
            {
                ace.Span.CopyTo(acl[position..]);
                position += ace.Length;
            }
        }

        return result;
    }

    /// <summary>Encodes one ACE of <paramref name="spec"/> (validated first).</summary>
    /// <exception cref="ArgumentException">The spec is invalid (<see cref="AceSpec.Validate"/>).</exception>
    public static byte[] EncodeAce(AceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec.Validate();
        return EncodeAce(spec.AceType, spec.Flags, spec.Mask, spec.TrusteeSid, spec.ObjectType, spec.InheritedObjectType);
    }

    /// <summary>Encodes one allow/deny ACE (object ACE types 0x05–0x08 carry the object type GUIDs).</summary>
    public static byte[] EncodeAce(byte type, byte flags, int mask, string sid, Guid? objectType, Guid? inheritedObjectType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        var sidBytes = AdConvert.SidToBytes(sid);
        var isObject = IsObjectAceType(type);
        var size = 8 + sidBytes.Length;
        if (isObject)
        {
            size += 4 + (objectType is null ? 0 : 16) + (inheritedObjectType is null ? 0 : 16);
        }

        var bytes = new byte[size];
        var span = bytes.AsSpan();
        span[0] = type;
        span[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)size);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], mask);
        var position = 8;
        if (isObject)
        {
            var objectFlags = (objectType is null ? 0 : 1) | (inheritedObjectType is null ? 0 : 2);
            BinaryPrimitives.WriteInt32LittleEndian(span[position..], objectFlags);
            position += 4;
            if (objectType is { } type1)
            {
                type1.TryWriteBytes(span[position..]);
                position += 16;
            }

            if (inheritedObjectType is { } type2)
            {
                type2.TryWriteBytes(span[position..]);
                position += 16;
            }
        }

        sidBytes.CopyTo(span[position..]);
        return bytes;
    }

    /// <summary>Object ACE types (allowed/denied/audit/alarm object and their callback forms).</summary>
    internal static bool IsObjectAceType(byte type) => type is 0x05 or 0x06 or 0x07 or 0x08 or 0x0B or 0x0C or 0x0F or 0x10;
}
