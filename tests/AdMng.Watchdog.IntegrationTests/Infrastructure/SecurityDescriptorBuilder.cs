using System.Buffers.Binary;
using System.Globalization;

namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// Builds binary self-relative security descriptors ([MS-DTYP] 2.4.6) for the hardened-object scenario – Samba ignores
/// SDDL strings written to nTSecurityDescriptor (Appendix B), so tests write the binary form.
/// </summary>
internal static class SecurityDescriptorBuilder
{
    /// <summary>Full control on a directory object (STANDARD_RIGHTS_REQUIRED | all DS rights).</summary>
    public const int FullControl = 0x000F01FF;

    /// <summary>Generic read of a directory object: READ_CONTROL | LIST_CHILDREN | READ_PROPERTY | LIST_OBJECT.</summary>
    public const int GenericRead = 0x00020094;

    /// <summary>ADS_RIGHT_DS_READ_PROP.</summary>
    public const int ReadProperty = 0x00000010;

    private const byte AccessAllowedAceType = 0x00;
    private const byte AccessDeniedAceType = 0x01;
    private const ushort SaclPresent = 0x0010;
    private const byte AccessDeniedObjectAceType = 0x06;
    private const int AceObjectTypePresent = 0x1;
    private const byte AclRevisionDs = 4;
    private const ushort SelfRelative = 0x8000;
    private const ushort DaclPresent = 0x0004;
    private const ushort DaclProtected = 0x1000;

    /// <summary>A descriptor with owner, group and a protected DACL made of <paramref name="aces"/> (in this order).</summary>
    public static byte[] Build(string ownerSid, string groupSid, params byte[][] aces)
    {
        var owner = Sid(ownerSid);
        var group = Sid(groupSid);
        var aclSize = 8 + aces.Sum(a => a.Length);
        var buffer = new byte[20 + owner.Length + group.Length + aclSize];
        buffer[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), SelfRelative | DaclPresent | DaclProtected);
        var offset = 20;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), offset);
        owner.CopyTo(buffer, offset);
        offset += owner.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), offset);
        group.CopyTo(buffer, offset);
        offset += group.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), offset);
        buffer[offset] = AclRevisionDs;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 2), (ushort)aclSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 4), (ushort)aces.Length);
        offset += 8;
        foreach (var ace in aces)
        {
            ace.CopyTo(buffer, offset);
            offset += ace.Length;
        }

        return buffer;
    }

    /// <summary>ACCESS_ALLOWED_ACE for <paramref name="sid"/>.</summary>
    public static byte[] Allow(string sid, int mask)
    {
        var trustee = Sid(sid);
        var ace = new byte[8 + trustee.Length];
        Header(ace, AccessAllowedAceType);
        BinaryPrimitives.WriteInt32LittleEndian(ace.AsSpan(4), mask);
        trustee.CopyTo(ace, 8);
        return ace;
    }

    /// <summary>ACCESS_DENIED_OBJECT_ACE for one property (attribute schemaIDGUID) of <paramref name="sid"/>.</summary>
    public static byte[] DenyProperty(string sid, int mask, Guid propertyGuid)
    {
        var trustee = Sid(sid);
        var ace = new byte[8 + 4 + 16 + trustee.Length];
        Header(ace, AccessDeniedObjectAceType);
        BinaryPrimitives.WriteInt32LittleEndian(ace.AsSpan(4), mask);
        BinaryPrimitives.WriteInt32LittleEndian(ace.AsSpan(8), AceObjectTypePresent);
        propertyGuid.TryWriteBytes(ace.AsSpan(12));
        trustee.CopyTo(ace, 28);
        return ace;
    }

    /// <summary>ACCESS_DENIED_ACE (whole object) for <paramref name="sid"/>.</summary>
    public static byte[] Deny(string sid, int mask)
    {
        var trustee = Sid(sid);
        var ace = new byte[8 + trustee.Length];
        Header(ace, AccessDeniedAceType);
        BinaryPrimitives.WriteInt32LittleEndian(ace.AsSpan(4), mask);
        trustee.CopyTo(ace, 8);
        return ace;
    }

    /// <summary>
    /// The descriptor (owner, group, DACL as read with SD flags 7) with <paramref name="ace"/> as the first DACL entry – an
    /// explicit deny goes before every other ACE (canonical order). The SACL, if any, is dropped.
    /// </summary>
    public static byte[] PrependAce(byte[] descriptor, byte[] ace)
    {
        var control = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(2)) & ~SaclPresent);
        var ownerOffset = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(4));
        var groupOffset = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(8));
        var daclOffset = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(16));
        var owner = descriptor.AsSpan(ownerOffset, 8 + (4 * descriptor[ownerOffset + 1])).ToArray();
        var group = descriptor.AsSpan(groupOffset, 8 + (4 * descriptor[groupOffset + 1])).ToArray();
        var aclSize = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(daclOffset + 2));
        var aceCount = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(daclOffset + 4));
        var aces = descriptor.AsSpan(daclOffset + 8, aclSize - 8).ToArray();

        var newAclSize = aclSize + ace.Length;
        var buffer = new byte[20 + owner.Length + group.Length + newAclSize];
        buffer[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), control);
        var offset = 20;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), offset);
        owner.CopyTo(buffer, offset);
        offset += owner.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), offset);
        group.CopyTo(buffer, offset);
        offset += group.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), offset);
        buffer[offset] = Math.Max(descriptor[daclOffset], AclRevisionDs);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 2), (ushort)newAclSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 4), (ushort)(aceCount + 1));
        ace.CopyTo(buffer, offset + 8);
        aces.CopyTo(buffer, offset + 8 + ace.Length);
        return buffer;
    }

    /// <summary>The binary form of a SID string (S-1-&lt;authority&gt;-&lt;sub&gt;…).</summary>
    public static byte[] Sid(string sid)
    {
        var parts = sid.Split('-');
        if (parts.Length < 3 || parts[0] != "S" || parts[1] != "1")
        {
            throw new FormatException("Not a SID string.");
        }

        var authority = long.Parse(parts[2], CultureInfo.InvariantCulture);
        var subAuthorities = parts.Skip(3).Select(p => uint.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var bytes = new byte[8 + (4 * subAuthorities.Length)];
        bytes[0] = 1;
        bytes[1] = (byte)subAuthorities.Length;
        for (var i = 0; i < 6; i++)
        {
            bytes[2 + i] = (byte)(authority >> (8 * (5 - i)));
        }

        for (var i = 0; i < subAuthorities.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + (4 * i)), subAuthorities[i]);
        }

        return bytes;
    }

    private static void Header(byte[] ace, byte type)
    {
        ace[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(ace.AsSpan(2), (ushort)ace.Length);
    }
}
