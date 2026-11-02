using System.Text;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// The header of a CAB read before <c>expand.exe</c> unpacks it (K3 review: an unsigned CAB could fill the disk or name
/// files outside the target folder). Cabinets are written byte by byte (header, one folder, the file entries; no data).
/// </summary>
public class CabinetDirectoryTests
{
    private const ushort Utf8Name = 0x80;

    [Fact]
    public void Read_ListsTheFiles_AndTheirSizeInAll()
    {
        var cabinet = Cabinet(reserve: true,
            ("MicrosoftEdgePolicyTemplates.zip", 15_234_567, 0, 0),
            (@"szablony\pl-PL\żółw.adml", 2_048, 0, Utf8Name));

        Assert.Equal(new CabinetSummary(2, 15_236_615), Read(cabinet));
        Assert.Equal(new CabinetSummary(3, 3L * uint.MaxValue), Read(Cabinet(false, ("a.admx", uint.MaxValue, 0, 0), ("b.admx", uint.MaxValue, 0, 0), ("c.admx", uint.MaxValue, 0, 0))));
    }

    [Theory]
    [InlineData(@"..\..\Windows\System32\evil.dll")]
    [InlineData(@"szablony\..\..\evil.dll")]
    [InlineData(@"C:\Windows\evil.dll")]
    [InlineData(@"\Windows\evil.dll")]
    [InlineData("/etc/evil")]
    [InlineData("plik.admx:strumień")]
    [InlineData("")]
    public void Read_RefusesAFileOutsideTheTargetFolder(string name)
    {
        var error = Assert.Throws<InvalidDataException>(() => Read(Cabinet(false, ("dobry.admx", 10, 0, 0), (name, 10, 0, Utf8Name))));

        Assert.Contains("relative path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RefusesASetOfCabinets()
    {
        Assert.Throws<InvalidDataException>(() => Read(Cabinet(false, flags: 0x0002, ("a.admx", 10, 0, 0))));
        Assert.Throws<InvalidDataException>(() => Read(Cabinet(false, flags: 0x0001, ("a.admx", 10, 0, 0))));
        Assert.Throws<InvalidDataException>(() => Read(Cabinet(false, ("a.admx", 10, 0xFFFE, 0))));
    }

    [Fact]
    public void Read_RefusesWhatIsNotAnIntactCabinet()
    {
        var cabinet = Cabinet(false, ("a.admx", 10, 0, 0));
        var unterminated = Cabinet(false, (new string('x', 300), 10, 0, 0));
        var pointingOutside = (byte[])cabinet.Clone();
        BitConverter.TryWriteBytes(pointingOutside.AsSpan(16), (uint)cabinet.Length);

        Assert.Throws<InvalidDataException>(() => Read("PK\u0003\u0004 to jest ZIP"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => Read(cabinet[..20]));
        Assert.Throws<InvalidDataException>(() => Read(cabinet[..^3]));
        Assert.Throws<InvalidDataException>(() => Read(unterminated));
        Assert.Throws<InvalidDataException>(() => Read(pointingOutside));
        Assert.Throws<InvalidDataException>(() => Read([]));
    }

    private static CabinetSummary Read(byte[] cabinet)
    {
        using var stream = new MemoryStream(cabinet, writable: false);
        return CabinetDirectory.Read(stream);
    }

    private static byte[] Cabinet(bool reserve, params (string Name, uint Size, ushort Folder, ushort Attributes)[] files) => Cabinet(reserve, 0, files);

    /// <summary>CFHEADER (with a reserved area like a signed cabinet's when asked), one CFFOLDER and the CFFILE entries.</summary>
    private static byte[] Cabinet(bool reserve, ushort flags, params (string Name, uint Size, ushort Folder, ushort Attributes)[] files)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var reserved = reserve ? 4 + 20 : 0;
        var filesOffset = 36 + reserved + 8;
        writer.Write(0x4643534Du); // "MSCF"
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((uint)filesOffset);
        writer.Write(0u);
        writer.Write((byte)3);
        writer.Write((byte)1);
        writer.Write((ushort)1);
        writer.Write((ushort)files.Length);
        writer.Write((ushort)(flags | (reserve ? 0x0004 : 0)));
        writer.Write((ushort)0x2026);
        writer.Write((ushort)0);
        if (reserve)
        {
            writer.Write((ushort)20); // header reserve (where a signature would be)
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write(new byte[20]);
        }

        writer.Write(0u); // the folder's data (none: only the header is read)
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        foreach (var (name, size, folder, attributes) in files)
        {
            writer.Write(size);
            writer.Write(0u);
            writer.Write(folder);
            writer.Write((ushort)0x5A21);
            writer.Write((ushort)0x6000);
            writer.Write(attributes);
            writer.Write((attributes & Utf8Name) != 0 ? Encoding.UTF8.GetBytes(name) : Encoding.Latin1.GetBytes(name));
            writer.Write((byte)0);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
