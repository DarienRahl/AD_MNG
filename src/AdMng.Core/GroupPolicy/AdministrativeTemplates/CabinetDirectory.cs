using System.Text;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>The files of a cabinet as its header lists them.</summary>
/// <param name="Files">How many files it holds.</param>
/// <param name="Bytes">The sum of their sizes, unpacked.</param>
public sealed record CabinetSummary(int Files, long Bytes);

/// <summary>
/// The file list of a Microsoft cabinet (CAB), read from its header before anything is unpacked (K3, "Zaktualizuj
/// szablony"). <c>expand.exe</c> writes every file the header lists, so the sum of their sizes is what unpacking writes to
/// disk, and a name with a drive, a root or a <c>..</c> segment would be written outside the target folder. A CAB may be
/// unsigned (the window says so), so it is checked like any file nobody vouches for: only a single cabinet whose names are
/// plain relative paths passes.
/// </summary>
public static class CabinetDirectory
{
    /// <summary>"MSCF".</summary>
    private const uint Signature = 0x4643534D;

    private const ushort PreviousCabinetFlag = 0x0001;
    private const ushort NextCabinetFlag = 0x0002;

    /// <summary>Folder indexes from this one up mark a file continued from or into another cabinet.</summary>
    private const ushort ContinuedFolder = 0xFFFD;

    /// <summary>The name is UTF-8 (otherwise the code page of the machine that made the cabinet).</summary>
    private const ushort NameIsUtf8 = 0x80;

    /// <summary>Longest file name of a cabinet, its terminating zero included.</summary>
    private const int MaxNameBytes = 256;

    /// <summary>The files of the cabinet (the stream must be seekable; it is read from its start, not disposed).</summary>
    /// <exception cref="InvalidDataException">
    /// Not a cabinet, part of a set of cabinets, damaged, or holding a file whose name is not a plain relative path.
    /// </exception>
    public static CabinetSummary Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        try
        {
            if (reader.ReadUInt32() != Signature)
            {
                throw new InvalidDataException("The file is not a cabinet.");
            }

            reader.ReadUInt32(); // reserved
            reader.ReadUInt32(); // size of the cabinet
            reader.ReadUInt32(); // reserved
            var filesOffset = reader.ReadUInt32();
            reader.ReadUInt32(); // reserved
            reader.ReadUInt16(); // version
            reader.ReadUInt16(); // folders
            var count = reader.ReadUInt16();
            var flags = reader.ReadUInt16();
            if ((flags & (PreviousCabinetFlag | NextCabinetFlag)) != 0)
            {
                throw new InvalidDataException("The cabinet is part of a set of cabinets.");
            }

            if (filesOffset >= stream.Length)
            {
                throw new InvalidDataException("The cabinet is damaged.");
            }

            stream.Position = filesOffset;
            long bytes = 0;
            for (var i = 0; i < count; i++)
            {
                var size = reader.ReadUInt32();
                reader.ReadUInt32(); // offset in its folder
                var folder = reader.ReadUInt16();
                reader.ReadUInt16(); // date
                reader.ReadUInt16(); // time
                var attributes = reader.ReadUInt16();
                var name = ReadName(reader, (attributes & NameIsUtf8) != 0);
                if (folder >= ContinuedFolder)
                {
                    throw new InvalidDataException("The cabinet is part of a set of cabinets.");
                }

                if (!SysvolPaths.TryNormalize(name, out _))
                {
                    throw new InvalidDataException("A file of the cabinet is not a plain relative path.");
                }

                bytes += size;
            }

            return new CabinetSummary(count, bytes);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The cabinet is damaged.", ex);
        }
    }

    private static string ReadName(BinaryReader reader, bool utf8)
    {
        Span<byte> name = stackalloc byte[MaxNameBytes];
        for (var length = 0; length < MaxNameBytes; length++)
        {
            var b = reader.ReadByte();
            if (b == 0)
            {
                return (utf8 ? Encoding.UTF8 : Encoding.Latin1).GetString(name[..length]);
            }

            name[length] = b;
        }

        throw new InvalidDataException("The cabinet is damaged.");
    }
}
