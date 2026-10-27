using System.Buffers.Binary;
using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.Security;

/// <summary>Outcome of <see cref="DaclEditor.Apply"/>.</summary>
/// <param name="Descriptor">The new self-relative descriptor holding only the DACL (write it with the SD flags control DACL).</param>
/// <param name="Applied">The part of the delta that changed the DACL (added ACEs that were missing, removed ACEs that were there).</param>
public sealed record DaclEditResult(byte[] Descriptor, AclChange Applied)
{
    /// <summary>The DACL changed (nothing to write otherwise).</summary>
    public bool Changed => !Applied.IsEmpty;
}

/// <summary>
/// Applies an <see cref="AclChange"/> to the DACL of a binary self-relative descriptor (K1 design note 3.1):
/// <list type="bullet">
/// <item>an added ACE that is already present – an explicit ACE with the same qualifier, flags, trustee and object types
/// whose mask covers the added mask – is a no-op; a removed ACE that is absent is a no-op;</item>
/// <item>a removed ACE removes every explicit ACE equal to it (<see cref="AceSpec.SameAce"/>);</item>
/// <item>inherited ACEs are never touched, and ACEs of other types (callback, unknown) are kept byte for byte;</item>
/// <item>the result is in canonical order: explicit deny ACEs, explicit allow ACEs (each group in its original order, new
/// ACEs at its end), then the inherited ACEs unchanged;</item>
/// <item>the result holds only the DACL: control <c>SE_DACL_PRESENT | SE_SELF_RELATIVE</c> plus the DACL protection and
/// auto-inheritance bits as read, no owner, group or SACL.</item>
/// </list>
/// Pure and deterministic; applying the same delta to its own result changes nothing.
/// </summary>
public static class DaclEditor
{
    private const int HeaderLength = 20;

    private const ushort KeptControlBits = SecurityDescriptorWriter.DaclProtected | SecurityDescriptorWriter.DaclAutoInherited
        | SecurityDescriptorWriter.DaclAutoInheritRequired;

    /// <summary>Applies <paramref name="change"/> to the DACL of <paramref name="descriptor"/>.</summary>
    /// <exception cref="FormatException">The descriptor is malformed or has no DACL.</exception>
    /// <exception cref="ArgumentException">An ACE of the change cannot be written (invalid SID, flags, empty mask).</exception>
    /// <exception cref="InvalidOperationException">The result would not fit into one ACL.</exception>
    public static DaclEditResult Apply(ReadOnlySpan<byte> descriptor, AclChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        foreach (var spec in change.Added.Concat(change.Removed))
        {
            spec.Validate();
        }

        var dacl = Read(descriptor);
        var entries = dacl.Entries.ToList();
        var removed = new List<AceSpec>();
        foreach (var spec in change.Removed)
        {
            var count = entries.RemoveAll(e => e.Spec is { } explicitAce && explicitAce.SameAce(spec));
            if (count > 0 && !removed.Any(r => r.SameAce(spec)))
            {
                removed.Add(spec);
            }
        }

        var added = new List<AceSpec>();
        foreach (var spec in change.Added)
        {
            if (entries.Any(e => e.Spec is { } existing && Covers(existing, spec)))
            {
                continue;
            }

            entries.Add(new RawAce(SecurityDescriptorWriter.EncodeAce(spec), spec.AceType, spec.Flags, spec));
            added.Add(spec);
        }

        var ordered = entries.Where(e => !e.IsInherited && e.IsDeny)
            .Concat(entries.Where(e => !e.IsInherited && !e.IsDeny))
            .Concat(entries.Where(e => e.IsInherited))
            .Select(e => (ReadOnlyMemory<byte>)e.Bytes)
            .ToList();
        var control = (ushort)(dacl.Control & KeptControlBits);
        var result = SecurityDescriptorWriter.Build(control, null, null, ordered, dacl.Revision);
        return new DaclEditResult(result, new AclChange(added, removed));
    }

    /// <summary>
    /// The explicit allow/deny ACEs of the DACL as specs (inherited and other ACE types left out), in descriptor order –
    /// what a planner compares a wanted ACE set with. False for a malformed descriptor or one without a DACL.
    /// </summary>
    public static bool TryReadExplicit(ReadOnlySpan<byte> descriptor, out IReadOnlyList<AceSpec> aces)
    {
        try
        {
            aces = [.. Read(descriptor).Entries.Where(e => e.Spec is not null).Select(e => e.Spec!)];
            return true;
        }
        catch (FormatException)
        {
            aces = [];
            return false;
        }
    }

    /// <summary>The DACL is protected (inheritance from the parent disabled). False for a malformed descriptor.</summary>
    public static bool IsProtected(ReadOnlySpan<byte> descriptor) =>
        descriptor.Length >= HeaderLength && descriptor[0] == 1
        && (BinaryPrimitives.ReadUInt16LittleEndian(descriptor[2..]) & SecurityDescriptorWriter.DaclProtected) != 0;

    /// <summary>An existing explicit ACE makes adding <paramref name="wanted"/> a no-op: same ACE, mask a superset.</summary>
    private static bool Covers(AceSpec existing, AceSpec wanted) =>
        existing.Qualifier == wanted.Qualifier && existing.Flags == wanted.Flags
        && string.Equals(existing.TrusteeSid, wanted.TrusteeSid, StringComparison.OrdinalIgnoreCase)
        && existing.ObjectType == wanted.ObjectType && existing.InheritedObjectType == wanted.InheritedObjectType
        && (existing.Mask & wanted.Mask) == wanted.Mask;

    private static ParsedDacl Read(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < HeaderLength || descriptor[0] != 1)
        {
            throw new FormatException("The security descriptor is malformed.");
        }

        var control = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[2..]);
        var daclOffset = BinaryPrimitives.ReadInt32LittleEndian(descriptor[16..]);
        if ((control & SecurityDescriptorWriter.DaclPresent) == 0 || daclOffset == 0)
        {
            throw new FormatException("The security descriptor has no DACL.");
        }

        if (daclOffset < HeaderLength || daclOffset > descriptor.Length - 8)
        {
            throw new FormatException("The DACL offset is outside the descriptor.");
        }

        var acl = descriptor[daclOffset..];
        var revision = acl[0];
        var aclSize = BinaryPrimitives.ReadUInt16LittleEndian(acl[2..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(acl[4..]);
        if (aclSize < 8 || aclSize > acl.Length)
        {
            throw new FormatException("The DACL size is invalid.");
        }

        var entries = new List<RawAce>(count);
        var position = 8;
        for (var i = 0; i < count; i++)
        {
            if (position + 8 > aclSize)
            {
                throw new FormatException("An ACE lies outside the DACL.");
            }

            var type = acl[position];
            var flags = acl[position + 1];
            var size = BinaryPrimitives.ReadUInt16LittleEndian(acl[(position + 2)..]);
            if (size < 8 || position + size > aclSize)
            {
                throw new FormatException("An ACE has an invalid size.");
            }

            var bytes = acl.Slice(position, size).ToArray();
            entries.Add(new RawAce(bytes, type, flags, Decode(bytes, type, flags)));
            position += size;
        }

        return new ParsedDacl(control, revision, entries);
    }

    /// <summary>The spec of an explicit allow/deny ACE (types 0x00, 0x01, 0x05, 0x06), null for anything else.</summary>
    private static AceSpec? Decode(byte[] ace, byte type, byte flags)
    {
        if ((flags & SecurityDescriptorAce.InheritedFlag) != 0 || type is not (0x00 or 0x01 or 0x05 or 0x06))
        {
            return null;
        }

        var span = ace.AsSpan();
        var mask = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        var position = 8;
        Guid? objectType = null;
        Guid? inheritedObjectType = null;
        if (type is 0x05 or 0x06)
        {
            if (span.Length < 12)
            {
                throw new FormatException("An object ACE is too short.");
            }

            var objectFlags = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
            position = 12;
            if ((objectFlags & 1) != 0)
            {
                objectType = ReadGuid(span, ref position);
            }

            if ((objectFlags & 2) != 0)
            {
                inheritedObjectType = ReadGuid(span, ref position);
            }
        }

        if (position + 8 > span.Length || position + 8 + (span[position + 1] * 4) > span.Length)
        {
            throw new FormatException("An ACE has a truncated SID.");
        }

        var sid = AdConvert.SidToString(span[position..]);
        if ((flags & ~AceSpec.AllowedFlags) != 0)
        {
            // Flags a spec cannot express (e.g. audit flags in a DACL): kept byte for byte, never matched.
            return null;
        }

        return new AceSpec
        {
            Qualifier = type is 0x00 or 0x05 ? AceQualifier.Allow : AceQualifier.Deny,
            Flags = flags,
            Mask = mask,
            TrusteeSid = sid,
            ObjectType = objectType,
            InheritedObjectType = inheritedObjectType,
        };
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> span, ref int position)
    {
        if (position + 16 > span.Length)
        {
            throw new FormatException("An object ACE has a truncated GUID.");
        }

        var guid = new Guid(span.Slice(position, 16));
        position += 16;
        return guid;
    }

    private sealed record ParsedDacl(ushort Control, byte Revision, IReadOnlyList<RawAce> Entries);

    /// <summary>One ACE as read (or encoded), with its spec when it is an explicit allow/deny ACE.</summary>
    private sealed record RawAce(byte[] Bytes, byte Type, byte Flags, AceSpec? Spec)
    {
        public bool IsInherited => (Flags & SecurityDescriptorAce.InheritedFlag) != 0;

        /// <summary>Denied types (plain, object, callback, callback object).</summary>
        public bool IsDeny => Type is 0x01 or 0x06 or 0x0A or 0x0C;
    }
}
