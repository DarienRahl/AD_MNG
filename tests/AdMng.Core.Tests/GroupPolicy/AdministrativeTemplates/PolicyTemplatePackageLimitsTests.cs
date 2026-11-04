using System.IO.Compression;
using System.Text;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// The limits of a package of templates (K3 review: a crafted archive must not exhaust the operator's memory or time) and
/// the reading of its files: one archive read once, entries that cannot be inflated reported as unreadable, cancellation.
/// </summary>
public class PolicyTemplatePackageLimitsTests : GroupPolicyTestBed
{
    private const ushort Stored = 0;
    private const ushort Deflate = 8;
    private const ushort Lzma = 14;
    private const ushort BZip2 = 12;

    [Fact]
    public void Files_WithMoreTemplatesThanTheLimit_AreRefused_AsTooLarge()
    {
        var files = Enumerable.Range(0, PolicyTemplatePackage.MaxTemplates + 1).ToDictionary(i => $"szablony/szablon{i:D5}.admx", _ => new byte[] { 1 });

        var error = Assert.Throws<InvalidDataException>(() => PolicyTemplatePackage.FromFiles("Za dużo szablonów", files));

        Assert.True(PolicyTemplatePackage.IsTooLarge(error));
        files.Remove("szablony/szablon00000.admx");
        Assert.Equal(PolicyTemplatePackage.MaxTemplates, PolicyTemplatePackage.FromFiles("W sam raz", files).AdmxFiles.Count);
    }

    [Fact]
    public void Zip_WhoseTemplatesAreTooLargeInAll_IsRefused_BeforeAnythingIsInflated()
    {
        // The review's zip bomb: entries that each declare the largest file taken, with next to nothing stored.
        var entries = Enumerable.Range(1, 20).Select(i => new RawEntry($"windows/admx/bomba{i:D2}.admx", [], Deflate, PolicyTemplatePackage.MaxEntryBytes)).ToArray();

        var error = Assert.Throws<InvalidDataException>(() => PolicyTemplatePackage.FromZip("bomba.zip", RawZip(entries)));

        Assert.True(PolicyTemplatePackage.IsTooLarge(error));
        Assert.True(16 * PolicyTemplatePackage.MaxEntryBytes <= PolicyTemplatePackage.MaxPackageBytes);
        Assert.Equal(16, PolicyTemplatePackage.FromZip("szesnaście.zip", RawZip(entries[..16])).AdmxFiles.Count);
    }

    [Fact]
    public void Zip_DeclaringMoreEntriesThanTheLimit_IsRefused_BeforeItsDirectoryIsRead()
    {
        var forged = RawZip([new RawEntry("contoso.admx", Encoding.UTF8.GetBytes("<a/>"))], zip64Count: PolicyTemplatePackage.MaxEntries + 1);

        var error = Assert.Throws<InvalidDataException>(() => PolicyTemplatePackage.FromZip("forged.zip", forged));

        Assert.True(PolicyTemplatePackage.IsTooLarge(error));
        Assert.Equal(PolicyTemplatePackage.MaxEntries + 1, PolicyTemplatePackage.DeclaredEntries(forged));
    }

    [Fact]
    public void DeclaredEntries_ReadsTheClassicAndTheZip64Record_AsTheArchiveWriterWritesThem()
    {
        using var small = new MemoryStream();
        using (var zip = new ZipArchive(small, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("a.admx");
            zip.CreateEntry("en-US/a.adml");
        }

        using var large = new MemoryStream();
        using (var zip = new ZipArchive(large, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < 66_000; i++)
            {
                zip.CreateEntry($"e{i}", CompressionLevel.NoCompression);
            }
        }

        Assert.Equal(2, PolicyTemplatePackage.DeclaredEntries(small.ToArray()));
        Assert.Equal(66_000, PolicyTemplatePackage.DeclaredEntries(large.ToArray()));
        Assert.Equal(-1, PolicyTemplatePackage.DeclaredEntries("to nie jest archiwum ZIP"u8));
        Assert.Equal(-1, PolicyTemplatePackage.DeclaredEntries([]));
        Assert.Empty(PolicyTemplatePackage.FromZip("duże.zip", large.ToArray()).AdmxFiles); // within the limit: read (no templates in it)
    }

    [Fact]
    public async Task Zip_EntriesThatCannotBeInflated_ReadAsNull_AndTheGoodOnesAsTheyAre()
    {
        var good = Encoding.UTF8.GetBytes("<policyDefinitions/>");
        var archive = RawZip(
        [
            new RawEntry("windows/admx/dobry.admx", good),
            new RawEntry("windows/admx/lzma.admx", [1, 2, 3, 4], Lzma, 4),
            new RawEntry("windows/admx/bzip2.admx", [1, 2, 3, 4], BZip2, 4),
            new RawEntry("windows/admx/uszkodzony.admx", [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], Deflate, 100),
            new RawEntry("windows/admx/dluzszy.admx", Encoding.UTF8.GetBytes("<więcej niż zadeklarowano/>"), Stored, 5),
            new RawEntry("windows/admx/krotszy.admx", Deflated(good), Deflate, good.Length + 10),
        ]);

        var package = PolicyTemplatePackage.FromZip("mieszany.zip", archive);

        Assert.Equal(["bzip2.admx", "dluzszy.admx", "dobry.admx", "krotszy.admx", "lzma.admx", "uszkodzony.admx"], package.AdmxFiles);
        Assert.Equal(good, await package.ReadAsync("dobry.admx", PolicyTemplatePackage.MaxEntryBytes, Ct));
        foreach (var name in new[] { "lzma.admx", "bzip2.admx", "uszkodzony.admx", "dluzszy.admx", "krotszy.admx" })
        {
            Assert.Null(await package.ReadAsync(name, PolicyTemplatePackage.MaxEntryBytes, Ct));
        }
    }

    [Fact]
    public async Task Compare_ReportsAZipEntryThatCannotBeInflated_AsUnreadable()
    {
        await using var sysvol = await OpenSysvolAsync();
        var package = PolicyTemplatePackage.FromZip("policy_templates.zip", RawZip(
        [
            new RawEntry("windows/admx/contoso.admx", SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx)),
            new RawEntry("windows/admx/en-US/contoso.adml", SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlEn)),
            new RawEntry("windows/admx/spakowany7zip.admx", [5, 6, 7, 8, 9], Lzma, 4096),
        ]));

        var comparison = await GroupPolicyPlanner.CompareCentralStoreAsync(sysvol, package, new CentralStoreUpdateOptions { Cultures = ["en-US"] }, Ct);

        var unreadable = Assert.Single(comparison.Files, f => f.RelativePath == "spakowany7zip.admx");
        Assert.Equal(CentralStoreFileStatus.Unreadable, unreadable.Status);
        Assert.Null(unreadable.NewContent);
        Assert.Equal(CentralStoreFileStatus.New, Assert.Single(comparison.Files, f => f.RelativePath == "contoso.admx").Status);
    }

    [Fact]
    public async Task Zip_IsReadOnce_AndItsEntriesCanBeReadAtTheSameTime()
    {
        var contents = Enumerable.Range(0, 64).ToDictionary(i => $"szablon{i:D2}.admx", i => Encoding.UTF8.GetBytes(new string((char)('a' + (i % 26)), 20_000 + i)));
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in contents)
            {
                using var entry = zip.CreateEntry("windows/admx/" + name).Open();
                entry.Write(content);
            }
        }

        var package = PolicyTemplatePackage.FromZip("równolegle.zip", stream.ToArray()).WithTrust(PackageTrust.PinnedDownload, "dl.google.com");
        var read = await Task.WhenAll(contents.Keys.Select(name => Task.Run(() => package.ReadAsync(name, PolicyTemplatePackage.MaxEntryBytes, Ct), Ct)));

        Assert.Equal<byte[]?>(contents.Values, read);
    }

    [Fact]
    public async Task Readers_HonourTheCancellationToken()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-limits-");
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder.FullName, "contoso.admx"), [1, 2, 3], Ct);
            var packages = new[]
            {
                PolicyTemplatePackage.FromZip("x.zip", RawZip([new RawEntry("contoso.admx", [1, 2, 3])])),
                PolicyTemplatePackage.FromFolder("Szablony", folder.FullName),
                PolicyTemplatePackage.FromFolder("Szablony", folder.FullName, eager: true),
                PolicyTemplatePackage.FromFiles("x", new Dictionary<string, byte[]> { ["contoso.admx"] = [1, 2, 3] }),
            };

            foreach (var package in packages)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => package.ReadAsync("contoso.admx", 100, cancelled.Token));
                Assert.Equal(new byte[] { 1, 2, 3 }, await package.ReadAsync("contoso.admx", 100, Ct));
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Folder_ReadEagerly_ReadsOnlyTheFilesThePackageKeeps()
    {
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-eager-");
        try
        {
            Write(folder, "a/contoso.admx", SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmx));
            Write(folder, "a/pl/contoso.adml", SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlPl));
            for (var i = 0; i < 10; i++)
            {
                Sparse(folder, $"archiwum{i}/contoso.admx", 16L * 1024 * 1024); // later copies of the same template: not kept
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var package = PolicyTemplatePackage.FromFolder("Szablony", folder.FullName, eager: true);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated < 16L * 1024 * 1024, $"{allocated} bytes allocated: the copies that are not kept were read.");
            Assert.Equal(10, package.Issues.Count);
            Assert.Equal(SampleAdministrativeTemplates.Utf8(SampleAdministrativeTemplates.ContosoAdmlPl), await package.ReadAsync("pl/contoso.adml", PolicyTemplatePackage.MaxEntryBytes, Ct));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Folder_WhoseTemplatesAreTooLargeInAll_IsRefused_WithoutReadingThem()
    {
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-budget-");
        try
        {
            var count = (int)(PolicyTemplatePackage.MaxPackageBytes / (PolicyTemplatePackage.MaxEntryBytes - 1024)) + 1;
            for (var i = 0; i < count; i++)
            {
                Sparse(folder, $"szablon{i:D2}.admx", PolicyTemplatePackage.MaxEntryBytes - 1024);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var eager = Assert.Throws<InvalidDataException>(() => PolicyTemplatePackage.FromFolder("Za duże", folder.FullName, eager: true));
            var lazy = Assert.Throws<InvalidDataException>(() => PolicyTemplatePackage.FromFolder("Za duże", folder.FullName));

            Assert.True(PolicyTemplatePackage.IsTooLarge(eager));
            Assert.True(PolicyTemplatePackage.IsTooLarge(lazy));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < PolicyTemplatePackage.MaxEntryBytes);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Folder_FileThatGrewBeyondTheLimitAfterLoading_ReadsAsNull()
    {
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-k3-grown-");
        try
        {
            Write(folder, "contoso.admx", [1, 2, 3]);
            var package = PolicyTemplatePackage.FromFolder("Szablony", folder.FullName);
            Sparse(folder, "contoso.admx", PolicyTemplatePackage.MaxEntryBytes + 1);

            Assert.Null(await package.ReadAsync("contoso.admx", PolicyTemplatePackage.MaxEntryBytes, Ct));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void IsTooLarge_IsFalse_ForADamagedArchive_AndOtherExceptions()
    {
        var damaged = Assert.Throws<InvalidDataException>(() => PolicyTemplatePackage.FromZip("x.zip", [1, 2, 3]));

        Assert.False(PolicyTemplatePackage.IsTooLarge(damaged));
        Assert.False(PolicyTemplatePackage.IsTooLarge(new IOException("Dysk niedostępny")));
        Assert.False(PolicyTemplatePackage.IsTooLarge(new InvalidDataException("Uszkodzony nagłówek")));
    }

    private static void Write(DirectoryInfo folder, string path, byte[] content)
    {
        var file = Path.Combine([folder.FullName, .. path.Split('/')]);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, content);
    }

    /// <summary>A file of the given length that takes (next to) no disk space and no time to write.</summary>
    private static void Sparse(DirectoryInfo folder, string path, long length)
    {
        var file = Path.Combine([folder.FullName, .. path.Split('/')]);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
        stream.SetLength(length);
    }

    private static byte[] Deflated(byte[] content)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(content);
        }

        return output.ToArray();
    }

    /// <summary>An entry written byte by byte: its method, stored bytes and declared size are whatever a crafted archive says.</summary>
    /// <param name="Name">The path inside the archive.</param>
    /// <param name="Data">The bytes stored (compressed with <paramref name="Method"/>).</param>
    /// <param name="Method">The compression method (0 stored, 8 deflate, 12 BZip2, 14 LZMA …).</param>
    /// <param name="DeclaredSize">The uncompressed size declared (default: the stored bytes' length).</param>
    private sealed record RawEntry(string Name, byte[] Data, ushort Method = Stored, long? DeclaredSize = null);

    /// <summary>A ZIP archive with the entries as given; <paramref name="zip64Count"/> adds a ZIP64 record declaring that many entries.</summary>
    private static byte[] RawZip(IReadOnlyList<RawEntry> entries, long? zip64Count = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var offsets = new List<uint>();
        foreach (var entry in entries)
        {
            offsets.Add((uint)stream.Position);
            var name = Encoding.UTF8.GetBytes(entry.Name);
            writer.Write(0x04034b50u);
            writer.Write((ushort)20);
            writer.Write((ushort)0x0800); // UTF-8 names
            writer.Write(entry.Method);
            writer.Write((ushort)0);
            writer.Write((ushort)0x21);
            writer.Write(entry.Method == Stored ? Crc32(entry.Data) : 0u);
            writer.Write((uint)entry.Data.Length);
            writer.Write((uint)(entry.DeclaredSize ?? entry.Data.Length));
            writer.Write((ushort)name.Length);
            writer.Write((ushort)0);
            writer.Write(name);
            writer.Write(entry.Data);
        }

        var directory = stream.Position;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var name = Encoding.UTF8.GetBytes(entry.Name);
            writer.Write(0x02014b50u);
            writer.Write((ushort)20);
            writer.Write((ushort)20);
            writer.Write((ushort)0x0800);
            writer.Write(entry.Method);
            writer.Write((ushort)0);
            writer.Write((ushort)0x21);
            writer.Write(entry.Method == Stored ? Crc32(entry.Data) : 0u);
            writer.Write((uint)entry.Data.Length);
            writer.Write((uint)(entry.DeclaredSize ?? entry.Data.Length));
            writer.Write((ushort)name.Length);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(offsets[i]);
            writer.Write(name);
        }

        var directorySize = stream.Position - directory;
        var count = (ushort)entries.Count;
        if (zip64Count is { } declared)
        {
            var zip64 = stream.Position;
            writer.Write(0x06064b50u);
            writer.Write(44UL);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((ulong)declared);
            writer.Write((ulong)declared);
            writer.Write((ulong)directorySize);
            writer.Write((ulong)directory);
            writer.Write(0x07064b50u);
            writer.Write(0u);
            writer.Write((ulong)zip64);
            writer.Write(1u);
            count = ushort.MaxValue;
        }

        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(count);
        writer.Write(count);
        writer.Write((uint)directorySize);
        writer.Write((uint)directory);
        writer.Write((ushort)0);
        writer.Flush();
        return stream.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
