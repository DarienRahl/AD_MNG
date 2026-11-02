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
public sealed partial class PolicyTemplatePackage : IPolicyDefinitionSource
{
    /// <summary>Most entries of an archive or folder looked at.</summary>
    public const int MaxEntries = 100_000;

    /// <summary>Largest ADMX/ADML file taken (the largest Windows ADML is about 2 MB).</summary>
    public const long MaxEntryBytes = 32L * 1024 * 1024;

    /// <summary>Largest archive accepted in memory.</summary>
    public const long MaxArchiveBytes = 512L * 1024 * 1024;

    private readonly Dictionary<string, Func<CancellationToken, Task<byte[]?>>> _readers;
    private readonly Dictionary<string, long> _lengths;

    private PolicyTemplatePackage(string description, Dictionary<string, (long Length, Func<CancellationToken, Task<byte[]?>> Read)> files,
        IReadOnlyList<PolicyTemplatePackageIssue> issues)
    {
        Description = description;
        _readers = files.ToDictionary(f => f.Key, f => f.Value.Read, StringComparer.OrdinalIgnoreCase);
        _lengths = files.ToDictionary(f => f.Key, f => f.Value.Length, StringComparer.OrdinalIgnoreCase);
        Issues = issues;
        AdmxFiles = [.. _lengths.Keys.Where(PolicyDefinitionLoader.IsAdmx).Order(StringComparer.OrdinalIgnoreCase)];
        Cultures = [.. _lengths.Keys.Where(k => k.Contains('/', StringComparison.Ordinal)).Select(k => k[..k.IndexOf('/', StringComparison.Ordinal)])
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
    public PolicyTemplatePackage WithTrust(PackageTrust trust, string? signer) => new(Description, Files(), Issues) { Trust = trust, Signer = signer };

    public Task<IReadOnlyList<PolicyDefinitionFile>?> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PolicyDefinitionFile>?>([.. _lengths.Select(f => new PolicyDefinitionFile(f.Key, f.Value))]);

    public Task<byte[]?> ReadAsync(string relativePath, long maxBytes, CancellationToken cancellationToken) =>
        _readers.TryGetValue(relativePath, out var read) && _lengths[relativePath] <= maxBytes ? read(cancellationToken) : Task.FromResult<byte[]?>(null);

    /// <summary>A package of files in memory (tests, an archive already read).</summary>
    /// <param name="description">The package's name.</param>
    /// <param name="files">Path inside the package ('/'-separated) → content.</param>
    public static PolicyTemplatePackage FromFiles(string description, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Build(description, files.Select(f => (f.Key, (long)f.Value.Length, (Func<CancellationToken, Task<byte[]?>>)(_ => Task.FromResult<byte[]?>(f.Value)))));
    }

    /// <summary>A ZIP archive (Chrome, Firefox, Edge): the archive is kept in memory and its entries read on demand.</summary>
    /// <exception cref="InvalidDataException">The archive is not a ZIP file or is too large.</exception>
    public static PolicyTemplatePackage FromZip(string description, byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.LongLength > MaxArchiveBytes)
        {
            throw new InvalidDataException("The archive is too large.");
        }

        using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
        var entries = new List<(string, long, Func<CancellationToken, Task<byte[]?>>)>();
        foreach (var entry in zip.Entries.Take(MaxEntries))
        {
            if (entry.FullName.EndsWith('/') || !IsTemplate(entry.FullName))
            {
                continue;
            }

            var name = entry.FullName;
            var length = entry.Length;
            entries.Add((name.Replace('\\', '/'), length, _ => Task.FromResult(ReadEntry(archive, name))));
        }

        return Build(description, entries);
    }

    /// <summary>A folder of the local file system (an extracted package or the operator's own templates).</summary>
    /// <param name="description">The package's name.</param>
    /// <param name="folder">The folder (searched with its subfolders).</param>
    /// <param name="eager">Read the files now (the folder is a temporary one deleted right after).</param>
    public static PolicyTemplatePackage FromFolder(string description, string folder, bool eager = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = new DirectoryInfo(folder);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException("The folder does not exist.");
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false, MaxRecursionDepth = 8 };
        var entries = new List<(string, long, Func<CancellationToken, Task<byte[]?>>)>();
        foreach (var file in root.EnumerateFiles("*", options).Take(MaxEntries))
        {
            if (!IsTemplate(file.Name))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
            var path = file.FullName;
            var length = file.Length;
            if (eager)
            {
                var content = length <= MaxEntryBytes ? File.ReadAllBytes(path) : null;
                entries.Add((relative, length, _ => Task.FromResult(content)));
            }
            else
            {
                entries.Add((relative, length, async token => length <= MaxEntryBytes ? await File.ReadAllBytesAsync(path, token).ConfigureAwait(false) : null));
            }
        }

        return Build(description, entries);
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

    private Dictionary<string, (long, Func<CancellationToken, Task<byte[]?>>)> Files() =>
        _lengths.ToDictionary(f => f.Key, f => (f.Value, _readers[f.Key]), StringComparer.OrdinalIgnoreCase);

    private static bool IsTemplate(string name) =>
        name.EndsWith(".admx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".adml", StringComparison.OrdinalIgnoreCase);

    private static byte[]? ReadEntry(byte[] archive, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name);
        if (entry is null || entry.Length > MaxEntryBytes)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)Math.Max(0, entry.Length));
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxEntryBytes)
            {
                return null; // more than the archive declared: not taken
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Normalizes the package's paths to <c>name.admx</c> and <c>culture/name.adml</c>.</summary>
    private static PolicyTemplatePackage Build(string description, IEnumerable<(string Path, long Length, Func<CancellationToken, Task<byte[]?>> Read)> entries)
    {
        var all = entries.Where(e => SysvolPaths.TryNormalize(e.Path, out _)).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var issues = new List<PolicyTemplatePackageIssue>();
        var files = new Dictionary<string, (long, Func<CancellationToken, Task<byte[]?>>)>(StringComparer.OrdinalIgnoreCase);
        var admxFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, length, read) in all.Where(e => e.Path.EndsWith(".admx", StringComparison.OrdinalIgnoreCase)))
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (length > MaxEntryBytes)
            {
                issues.Add(new PolicyTemplatePackageIssue(path, "The file is too large."));
            }
            else if (files.TryAdd(name, (length, read)))
            {
                admxFolders[Path.GetFileNameWithoutExtension(name)] = path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;
            }
            else
            {
                issues.Add(new PolicyTemplatePackageIssue(path, "A template of this name was found earlier in the package."));
            }
        }

        foreach (var (path, length, read) in all.Where(e => e.Path.EndsWith(".adml", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = path.Split('/');
            var baseName = Path.GetFileNameWithoutExtension(parts[^1]);
            if (parts.Length < 2 || !IsCultureName(parts[^2]) || !admxFolders.TryGetValue(baseName, out var admxFolder)
                || !string.Equals(string.Join('/', parts[..^2]), admxFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue; // not the language file of a template of this package
            }

            var normalized = parts[^2] + "/" + parts[^1];
            if (length > MaxEntryBytes)
            {
                issues.Add(new PolicyTemplatePackageIssue(path, "The file is too large."));
            }
            else if (!files.TryAdd(normalized, (length, read)))
            {
                issues.Add(new PolicyTemplatePackageIssue(path, "A language file of this name was found earlier in the package."));
            }
        }

        return new PolicyTemplatePackage(description, files, issues);
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z]{4})?(-([A-Za-z]{2}|[0-9]{3}))?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex CultureTag();
}
