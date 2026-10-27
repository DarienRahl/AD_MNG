using System.Buffers.Binary;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Security;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>
/// The DACL of a GPO's SYSVOL folder derived from the DACL of its group policy container (design note
/// docs/dev/k-computers-and-gpo.md 5.4), mapped the way <c>samba-tool gpo</c> does (<c>dsacl2fsacl</c>, which mirrors GPMC):
/// only allow ACEs are mapped (not those of Pre-Windows 2000 Compatible Access); the standard rights are kept; read
/// property + list contents → read &amp; execute; write property → write data / append / attributes / EA; create child →
/// add file / subfolder; delete child → delete child; an ACE without file rights (an object ACE for <c>Apply Group
/// Policy</c>) is dropped; every ACE becomes object and container inherit (Creator Owner inherit only); the DACL is
/// protected. The result is a self-relative descriptor with the DACL only (no owner, group or SACL), in memory.
/// </summary>
public static class GpoFolderAcl
{
    /// <summary>FILE_LIST_DIRECTORY | FILE_READ_EA | FILE_TRAVERSE | FILE_READ_ATTRIBUTES | SYNCHRONIZE.</summary>
    public const int ReadAndExecute = 0x0001 | 0x0008 | 0x0020 | 0x0080 | Synchronize;

    /// <summary>FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY | FILE_WRITE_EA | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE.</summary>
    public const int Write = 0x0002 | 0x0004 | 0x0010 | 0x0100 | Synchronize;

    /// <summary>FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY.</summary>
    public const int AddFileOrFolder = 0x0002 | 0x0004;

    /// <summary>FILE_DELETE_CHILD.</summary>
    public const int DeleteChild = 0x0040;

    private const int Synchronize = 0x0010_0000;
    private const int StandardRightsAll = 0x001F_0000;
    private const string CreatorOwnerSid = "S-1-3-0";
    private const string PreWindows2000Sid = "S-1-5-32-554";
    private const byte AccessAllowedAceType = 0x00;
    private const byte ObjectInherit = 0x01;
    private const byte ContainerInherit = 0x02;
    private const byte InheritOnly = 0x08;
    private const ushort DaclPresent = 0x0004;
    private const ushort DaclProtected = 0x1000;
    private const ushort SelfRelative = 0x8000;

    /// <summary>A file ACE of the folder's DACL.</summary>
    /// <param name="Sid">Trustee.</param>
    /// <param name="Flags">ACE flags (object/container inherit, inherit only).</param>
    /// <param name="Mask">File rights.</param>
    public readonly record struct FileAce(string Sid, byte Flags, int Mask);

    /// <summary>The file rights of a directory-service mask (Samba's <c>ldapmask2filemask</c>).</summary>
    public static int ToFileMask(int directoryMask)
    {
        var mask = DirectoryAccessMask.Normalize(directoryMask);
        var file = mask & StandardRightsAll;
        if ((mask & DirectoryAccessMask.ReadProperty) != 0 && (mask & DirectoryAccessMask.ListChildren) != 0)
        {
            file |= ReadAndExecute;
        }

        if ((mask & DirectoryAccessMask.WriteProperty) != 0)
        {
            file |= Write;
        }

        if ((mask & DirectoryAccessMask.CreateChild) != 0)
        {
            file |= AddFileOrFolder;
        }

        if ((mask & DirectoryAccessMask.DeleteChild) != 0)
        {
            file |= DeleteChild;
        }

        return file;
    }

    /// <summary>The file ACEs for the container's DACL (in DACL order); null when the descriptor cannot be parsed.</summary>
    public static IReadOnlyList<FileAce>? MapAces(byte[]? containerDescriptor)
    {
        if (containerDescriptor is null || !SecurityDescriptorSummary.TryParse(containerDescriptor, out var summary) || summary is null)
        {
            return null;
        }

        var result = new List<FileAce>();
        foreach (var ace in summary.Aces)
        {
            if (!ace.IsAllow || string.Equals(ace.Sid, PreWindows2000Sid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mask = ToFileMask(ace.Mask);
            if (mask == 0)
            {
                continue;
            }

            var flags = (byte)(ObjectInherit | ContainerInherit);
            if (string.Equals(ace.Sid, CreatorOwnerSid, StringComparison.OrdinalIgnoreCase))
            {
                flags |= InheritOnly;
            }

            var entry = new FileAce(ace.Sid, flags, mask);
            if (!result.Contains(entry))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>The folder's descriptor (protected DACL only) for the container's DACL; null when it cannot be parsed.</summary>
    public static byte[]? FromDirectoryDescriptor(byte[]? containerDescriptor) =>
        MapAces(containerDescriptor) is { } aces ? Build(aces) : null;

    /// <summary>A self-relative descriptor with a protected DACL of the given allow ACEs.</summary>
    public static byte[] Build(IReadOnlyList<FileAce> aces)
    {
        ArgumentNullException.ThrowIfNull(aces);
        var aceBytes = aces.Select(a =>
        {
            var sid = AdConvert.SidToBytes(a.Sid);
            var ace = new byte[8 + sid.Length];
            ace[0] = AccessAllowedAceType;
            ace[1] = a.Flags;
            BinaryPrimitives.WriteUInt16LittleEndian(ace.AsSpan(2), (ushort)ace.Length);
            BinaryPrimitives.WriteInt32LittleEndian(ace.AsSpan(4), a.Mask);
            sid.CopyTo(ace, 8);
            return ace;
        }).ToList();
        var aclSize = 8 + aceBytes.Sum(a => a.Length);
        var descriptor = new byte[20 + aclSize];
        descriptor[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(2), DaclPresent | DaclProtected | SelfRelative);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(16), 20);
        var acl = descriptor.AsSpan(20);
        acl[0] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(acl[2..], (ushort)aclSize);
        BinaryPrimitives.WriteUInt16LittleEndian(acl[4..], (ushort)aceBytes.Count);
        var offset = 8;
        foreach (var ace in aceBytes)
        {
            ace.CopyTo(acl[offset..]);
            offset += ace.Length;
        }

        return descriptor;
    }

    /// <summary>
    /// True when the folder's DACL grants exactly the mapped ACEs (order, inherited ACEs and duplicates ignored) - what the
    /// <i>Stan</i> tab checks before offering <i>Napraw uprawnienia folderu</i>.
    /// </summary>
    public static bool IsConsistent(byte[]? folderDescriptor, byte[]? containerDescriptor)
    {
        if (MapAces(containerDescriptor) is not { } expected || folderDescriptor is null
            || !SecurityDescriptorSummary.TryParse(folderDescriptor, out var folder) || folder is null)
        {
            return false;
        }

        // An ACE without rights (Samba copies the Apply Group Policy object ACE with an empty mask) grants nothing.
        var actual = folder.Aces.Where(a => a.IsAllow && !a.IsInherited && a.Mask != 0)
            .Select(a => new FileAce(a.Sid, (byte)(a.Flags & (ObjectInherit | ContainerInherit | InheritOnly)), a.Mask))
            .ToHashSet();
        return actual.SetEquals(expected);
    }
}
