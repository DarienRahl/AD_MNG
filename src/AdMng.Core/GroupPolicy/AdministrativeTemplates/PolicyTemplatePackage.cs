using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>How a package of templates was checked (shown before the central store is updated).</summary>
public enum PackageTrust
{
    /// <summary>Not checked (a folder or a file the operator chose).</summary>
    NotChecked,

    /// <summary>Authenticode signature valid, signed by <see cref="PolicyTemplatePackage.Signer"/>.</summary>
    Signed,

    /// <summary>Downloaded over HTTPS from the vendor's pinned address (the vendor does not sign the archive).</summary>
    PinnedDownload,

    /// <summary>The file has no valid signature.</summary>
    Unsigned,
}

/// <summary>A problem of a package's layout (the file is skipped, never fatal).</summary>
/// <param name="Path">The path inside the package.</param>
/// <param name="Reason">Why it is skipped (English, value-free).</param>
public sealed record PolicyTemplatePackageIssue(string Path, string Reason);

/// <summary>
/// The ADMX/ADML files of a vendor package (a folder, a ZIP archive, an extracted MSI/CAB/EXE) normalized to the layout of
/// the central store: <c>name.admx</c> and <c>culture/name.adml</c> (K3, "Zaktualizuj szablony"). Every <c>*.admx</c> of the
/// package counts, wherever it is; its ADML are the <c>&lt;culture&gt;/name.adml</c> files of culture folders beside it
/// (Chrome: <c>windows/admx/…</c>, Firefox: <c>windows/…</c>, Edge: <c>windows/admx/…</c>, the Windows MSI:
/// <c>PolicyDefinitions/…</c>). A name found twice keeps its first place in path order (an issue says so). Files are read
/// on demand within <see cref="MaxEntryBytes"/>; templates are vendor files, not directory data.
/// </summary>
/// <remarks>
/// Limits, as a package comes from files nobody vouches for (a crafted archive must not exhaust the operator's memory or
/// time): at most <see cref="MaxEntries"/> entries looked at (a ZIP declaring more is refused before its directory is read),
/// <see cref="MaxEntryBytes"/> per file, <see cref="MaxTemplates"/> templates and <see cref="MaxPackageBytes"/> for all the
/// files a package keeps (the comparison with the central store reads them into memory). A package beyond them is refused
/// with an <see cref="InvalidDataException"/> that <see cref="IsTooLarge"/> recognizes; a ZIP entry that cannot be inflated
/// (an unsupported method, damaged data, more or fewer bytes than declared) reads as null, so it is reported as unreadable.
/// </remarks>
public sealed partial class PolicyTemplatePackage : IPolicyDefinitionSource
{
    /// <summary>Most entries of an archive or folder looked at.</summary>
    public const int MaxEntries = 100_000;

    /// <summary>Largest ADMX/ADML file taken (the largest Windows ADML is about 2 MB).</summary>
    public const long MaxEntryBytes = 32L * 1024 * 1024;

    /// <summary>Largest archive accepted in memory.</summary>
    public const long MaxArchiveBytes = 512L * 1024 * 1024;

    /// <summary>Most templates (ADMX files) of a package: the Windows templates are about 250, a store with every vendor's a few hundred.</summary>
    public const int MaxTemplates = 5_000;

    /// <summary>Largest sum of the files a package keeps (its ADMX and ADML files, as declared): what the comparison may read into memory.</summary>
    public const long MaxPackageBytes = MaxArchiveBytes;

    /// <summary>Marks the <see cref="InvalidDataException"/> of a package beyond the limits (<see cref="IsTooLarge"/>).</summary>
    private const string TooLargeKey = "AdMng.PolicyTemplatePackage.TooLarge";

    /// <summary>Bytes inflated or read between two looks at the cancellation token.</summary>
    private const int ChunkBytes = 81_920;

    private readonly Dictionary<string, PackageFile> _files;

    private PolicyTemplatePackage(string description, Dictionary<string, PackageFile> files, IReadOnlyList<PolicyTemplatePackageIssue> issues)
    {
        Description = description;
        _files = files;
        Issues = issues;
        AdmxFiles = [.. files.Keys.Where(PolicyDefinitionLoader.IsAdmx).Order(StringComparer.OrdinalIgnoreCase)];
        Cultures = [.. files.Keys.Where(k => k.Contains('/', StringComparison.Ordinal)).Select(k => k[..k.IndexOf('/', StringComparison.Ordinal)])
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"policy_templates.zip", "Microsoft Edge 131.0.2903.48" …</summary>
    public string Description { get; }

    /// <summary>The ADMX files (<c>name.admx</c>).</summary>
    public IReadOnlyList<string> AdmxFiles { get; }

    /// <summary>The culture folders with ADML files (<c>en-US</c>, <c>pl-PL</c> …).</summary>
    public IReadOnlyList<string> Cultures { get; }

    public IReadOnlyList<PolicyTemplatePackageIssue> Issues { get; }

    /// <summary>How the package was checked (set by the host that verified it).</summary>
    public PackageTrust Trust { get; init; } = PackageTrust.NotChecked;

    /// <summary>The signer or the vendor address (with <see cref="Trust"/>).</summary>
    public string? Signer { get; init; }

    /// <summary>The package with a trust verdict.</summary>
    public PolicyTemplatePackage WithTrust(PackageTrust trust, string? signer) => new(Description, _files, Issues) { Trust = trust, Signer = signer };

    public Task<IReadOnlyList<PolicyDefinitionFile>?> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PolicyDefinitionFile>?>([.. _files.Select(f => new PolicyDefinitionFile(f.Key, f.Value.Length))]);

    public Task<byte[]?> ReadAsync(string relativePath, long maxBytes, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<byte[]?>(cancellationToken);
        }

        return _files.TryGetValue(relativePath, out var file) && file.Length <= maxBytes ? file.Read(cancellationToken) : Task.FromResult<byte[]?>(null);
    }

    /// <summary>
    /// The exception refuses a package beyond the limits (<see cref="MaxArchiveBytes"/>, <see cref="MaxEntries"/>,
    /// <see cref="MaxTemplates"/>, <see cref="MaxPackageBytes"/>), not a damaged one.
    /// </summary>
    public static bool IsTooLarge(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is InvalidDataException && exception.Data[TooLargeKey] is true;
    }

    /// <summary>A package of files in memory (tests, an archive already read).</summary>
    /// <param name="description">The package's name.</param>
    /// <param name="files">Path inside the package ('/'-separated) → content.</param>
    /// <exception cref="InvalidDataException">The files are beyond the limits (<see cref="IsTooLarge"/>).</exception>
    public static PolicyTemplatePackage FromFiles(string description, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Build(description, files.Select(f => new PackageFile(f.Key, f.Value.Length, InMemory(f.Value))));
    }

    /// <summary>
    /// A ZIP archive (Chrome, Firefox, Edge): the archive is kept in memory, its directory read once, and its entries
    /// inflated on demand (one at a time).
    /// </summary>
    /// <exception cref="InvalidDataException">The archive is not a ZIP file, or it is beyond the limits (<see cref="IsTooLarge"/>).</exception>
    public static PolicyTemplatePackage FromZip(string description, byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.LongLength > MaxArchiveBytes)
        {
            throw TooLarge("The archive is too large.");
        }

        if (DeclaredEntries(archive) > MaxEntries)
        {
            throw TooLarge("The archive has too many entries.");
        }

        var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
        try
        {
            var reader = new ZipReader();
            var entries = new List<PackageFile>();
            foreach (var entry in zip.Entries.Take(MaxEntries))
            {
                if (entry.FullName.EndsWith('/') || !IsTemplate(entry.FullName))
                {
                    continue;
                }

                entries.Add(new PackageFile(entry.FullName.Replace('\\', '/'), entry.Length, token => reader.ReadAsync(entry, token)));
            }

            return Build(description, entries);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>A folder of the local file system (an extracted package or the operator's own templates).</summary>
    /// <param name="description">The package's name.</param>
    /// <param name="folder">The folder (searched with its subfolders).</param>
    /// <param name="eager">Read the files the package keeps now (the folder is a temporary one deleted right after).</param>
    /// <exception cref="InvalidDataException">The folder's templates are beyond the limits (<see cref="IsTooLarge"/>).</exception>
    public static PolicyTemplatePackage FromFolder(string description, string folder, bool eager = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = new DirectoryInfo(folder);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException("The folder does not exist.");
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false, MaxRecursionDepth = 8 };
        var entries = new List<PackageFile>();
        foreach (var file in root.EnumerateFiles("*", options).Take(MaxEntries))
        {
            if (!IsTemplate(file.Name))
            {
                continue;
            }

            var path = file.FullName;
            entries.Add(new PackageFile(Path.GetRelativePath(root.FullName, path).Replace('\\', '/'), file.Length, token => ReadFileAsync(path, token)) { Source = path });
        }

        var package = Build(description, entries);
        if (!eager)
        {
            return package;
        }

        // Only the files the package keeps are read, so all of them stay within MaxPackageBytes.
        var loaded = package._files.ToDictionary(f => f.Key, f => f.Value with { Read = InMemory(ReadFile(f.Value.Source!)) }, StringComparer.OrdinalIgnoreCase);
        return new PolicyTemplatePackage(description, loaded, package.Issues);
    }

    /// <summary>
    /// A folder name that is a culture tag (<c>en-US</c>, <c>pl</c>, <c>zh-Hant-TW</c>): a 2–3 letter language, an optional
    /// script and an optional region – checked by form as well (ICU accepts nearly any word as a culture).
    /// </summary>
    public static bool IsCultureName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 16 || !CultureTag().IsMatch(name))
        {
            return false;
        }

        try
        {
            return !string.IsNullOrEmpty(CultureInfo.GetCultureInfo(name).Name);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// The number of entries a ZIP archive declares in its end of central directory record (the larger of the classic and
    /// the ZIP64 count when both are there); -1 when there is no such record. The archive reader stops as soon as the
    /// directory holds more entries than declared, so a declared count within <see cref="MaxEntries"/> bounds the work.
    /// </summary>
    internal static long DeclaredEntries(ReadOnlySpan<byte> archive)
    {
        const uint EndSignature = 0x06054b50;
        const uint Zip64LocatorSignature = 0x07064b50;
        const uint Zip64EndSignature = 0x06064b50;
        const int EndSize = 22;
        const int LocatorSize = 20;
        const int Zip64EndSize = 56;

        // The record is the last thing in the file, followed only by its comment (at most 65 535 bytes).
        var lowest = Math.Max(0, archive.Length - EndSize - ushort.MaxValue);
        for (var at = archive.Length - EndSize; at >= lowest; at--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(archive[at..]) != EndSignature)
            {
                continue;
            }

            long entries = Math.Max(BinaryPrimitives.ReadUInt16LittleEndian(archive[(at + 8)..]), BinaryPrimitives.ReadUInt16LittleEndian(archive[(at + 10)..]));
            var locator = at - LocatorSize;
            if (locator >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(archive[locator..]) == Zip64LocatorSignature)
            {
                var offset = BinaryPrimitives.ReadUInt64LittleEndian(archive[(locator + 8)..]);
                if (archive.Length >= Zip64EndSize && offset <= (ulong)(archive.Length - Zip64EndSize)
                    && BinaryPrimitives.ReadUInt32LittleEndian(archive[(int)offset..]) == Zip64EndSignature)
                {
                    var zip64 = Math.Max(BinaryPrimitives.ReadUInt64LittleEndian(archive[((int)offset + 24)..]), BinaryPrimitives.ReadUInt64LittleEndian(archive[((int)offset + 32)..]));
                    entries = Math.Max(entries, zip64 > long.MaxValue ? long.MaxValue : (long)zip64);
                }
            }

            return entries;
        }

        return -1;
    }

    private static bool IsTemplate(string name) =>
        name.EndsWith(".admx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".adml", StringComparison.OrdinalIgnoreCase);

    private static InvalidDataException TooLarge(string message)
    {
        var exception = new InvalidDataException(message);
        exception.Data[TooLargeKey] = true;
        return exception;
    }

    private static Func<CancellationToken, Task<byte[]?>> InMemory(byte[]? content) =>
        token => token.IsCancellationRequested ? Task.FromCanceled<byte[]?>(token) : Task.FromResult(content);

    /// <summary>A file of a folder, read now (at most <see cref="MaxEntryBytes"/>; null when it has grown beyond that).</summary>
    private static byte[]? ReadFile(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = RandomAccess.GetLength(handle);
        if (length > MaxEntryBytes)
        {
            return null;
        }

        var buffer = new byte[length];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(filled), filled);
            if (read == 0)
            {
                return buffer[..filled]; // shorter than it was a moment ago
            }

            filled += read;
        }

        return buffer;
    }

    /// <summary>A file of a folder, read on demand (at most <see cref="MaxEntryBytes"/>; null when it has grown beyond that).</summary>
    private static async Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        var length = RandomAccess.GetLength(handle);
        if (length > MaxEntryBytes)
        {
            return null;
        }

        var buffer = new byte[length];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(filled, Math.Min(ChunkBytes, buffer.Length - filled)), filled, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer[..filled];
            }

            filled += read;
        }

        return buffer;
    }

    /// <summary>
    /// Normalizes the package's paths to <c>name.admx</c> and <c>culture/name.adml</c>, within <see cref="MaxTemplates"/>
    /// and <see cref="MaxPackageBytes"/>.
    /// </summary>
    private static PolicyTemplatePackage Build(string description, IEnumerable<PackageFile> entries)
    {
        var all = entries.Where(e => SysvolPaths.TryNormalize(e.Path, out _)).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var issues = new List<PolicyTemplatePackageIssue>();
        var files = new Dictionary<string, PackageFile>(StringComparer.OrdinalIgnoreCase);
        var admxFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long kept = 0;
        foreach (var file in all.Where(e => e.Path.EndsWith(".admx", StringComparison.OrdinalIgnoreCase)))
        {
            var path = file.Path;
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (file.Length is < 0 or > MaxEntryBytes)
            {
                issues.Add(new PolicyTemplatePackageIssue(path, "The file is too large."));
            }
            else if (files.TryAdd(name, file))
            {
                if (files.Count > MaxTemplates)
                {
                    throw TooLarge("The package holds too many templates.");
                }

                kept = Keep(kept, file.Length);
                admxFolders[Path.GetFileNameWithoutExtension(name)] = path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;
            }
            else
            {
                issues.Add(new PolicyTemplatePackageIssue(path, "A template of this name was found earlier in the package."));
            }
        }

        foreach (var file in all.Where(e => e.Path.EndsWith(".adml", StringComparison.OrdinalIgnoreCase)))
        {
            var path = file.Path;
            var parts = path.Split('/');
            var baseName = Path.GetFileNameWithoutExtension(parts[^1]);
            if (parts.Length < 2 || !IsCultureName(parts[^2]) || !admxFolders.TryGetValue(baseName, out var admxFolder)
                || !string.Equals(string.Join('/', parts[..^2]), admxFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue; // not the language file of a template of this package
            }

            var normalized = parts[^2] + "/" + parts[^1];
            if (file.Length is < 0 or > MaxEntryBytes)
            {
                issues.Add(new PolicyTemplatePackageIssue(path, "The file is too large."));
            }
            else if (files.TryAdd(normalized, file))
            {
                kept = Keep(kept, file.Length);
            }
            else
            {
                issues.Add(new PolicyTemplatePackageIssue(path, "A language file of this name was found earlier in the package."));
            }
        }

        return new PolicyTemplatePackage(description, files, issues);
    }

    private static long Keep(long kept, long length) => kept + length <= MaxPackageBytes
        ? kept + length
        : throw TooLarge("The templates of the package are too large in all.");

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z]{4})?(-([A-Za-z]{2}|[0-9]{3}))?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex CultureTag();

    /// <summary>A file of the package: its path inside the package, declared length and reader (and, for a folder, its file).</summary>
    private sealed record PackageFile(string Path, long Length, Func<CancellationToken, Task<byte[]?>> Read)
    {
        /// <summary>The file of a folder package (null for archives and files in memory).</summary>
        public string? Source { get; init; }
    }

    /// <summary>
    /// The entries of one archive, read one at a time (a <see cref="ZipArchive"/> shares its stream between entries): an entry
    /// whose bytes cannot be inflated, or whose size is not the declared one, reads as null (unreadable).
    /// </summary>
    private sealed class ZipReader
    {
        private readonly Lock _gate = new();

        public Task<byte[]?> ReadAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<byte[]?>(cancellationToken);
            }

            try
            {
                return Task.FromResult(Read(entry, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<byte[]?>(cancellationToken);
            }
        }

        private byte[]? Read(ZipArchiveEntry entry, CancellationToken cancellationToken)
        {
            var declared = entry.Length;
            if (declared is < 0 or > MaxEntryBytes)
            {
                return null;
            }

            lock (_gate)
            {
                try
                {
                    using var stream = entry.Open();
                    var buffer = new byte[declared];
                    var filled = 0;
                    while (filled < buffer.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = stream.Read(buffer, filled, Math.Min(ChunkBytes, buffer.Length - filled));
                        if (read == 0)
                        {
                            return null; // fewer bytes than the archive declared: damaged
                        }

                        filled += read;
                    }

                    return stream.ReadByte() < 0 ? buffer : null; // more bytes than declared: not taken
                }
                catch (InvalidDataException)
                {
                    return null; // an unsupported compression method (BZip2, LZMA, PPMd …) or damaged data
                }
            }
        }
    }
}
