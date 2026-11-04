using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook;

/// <summary>
/// Outlook signatures from <c>%APPDATA%\Microsoft\Signatures</c> (design note 4.1): the file listing and reading of the
/// <c>.htm</c> files and their <c>Name_files</c> images (relative paths only, no "..", no rooted paths, inside the folder),
/// at most <see cref="MaxFileBytes"/> per file, parsed with <see cref="OutlookSignatureParser"/>. No COM; the constructor
/// never touches the file system.
/// </summary>
public sealed class OutlookSignatureFiles : IOutlookSignatureSource
{
    /// <summary>The largest signature or image file read (2 MiB).</summary>
    public const int MaxFileBytes = 2 * 1024 * 1024;

    private static readonly string[] SignatureExtensions = [".htm", ".html"];

    private readonly string? _folder;

    /// <param name="folder">The signatures folder (tests); null: <c>%APPDATA%\Microsoft\Signatures</c> on Windows, none elsewhere.</param>
    public OutlookSignatureFiles(string? folder = null)
    {
        _folder = folder;
    }

    public string? Folder => _folder ?? DefaultFolder();

    /// <summary>
    /// The signature names: the <c>.htm</c> / <c>.html</c> files directly in <see cref="Folder"/> without the extension, one
    /// name per signature (case-insensitive), in ordinal order. Empty when there is no folder or it cannot be read.
    /// </summary>
    public IReadOnlyList<string> List()
    {
        var folder = Folder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(folder, "*", Enumeration()))
            {
                if (!IsSignatureFile(file))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length > 0 && seen.Add(name))
                {
                    names.Add(name);
                }
            }

            return [.. names];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads and parses one signature (on the thread pool). Its images are read through a resolver that accepts only relative
    /// paths inside <see cref="Folder"/> (see <see cref="ReadRelative"/>).
    /// </summary>
    /// <returns>The parsed signature, or null when <paramref name="name"/> is not a signature of the folder.</returns>
    /// <exception cref="InvalidDataException">The signature file is larger than <see cref="MaxFileBytes"/>.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public async Task<SignatureParseResult?> ReadAsync(string name, int ansiCodePage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = Folder;
        if (string.IsNullOrEmpty(folder) || !IsSafeName(name) || FindFile(folder, name) is not { } file)
        {
            return null;
        }

        var html = await ReadLimitedAsync(file, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The signature file is too large.");
        var signatureName = Path.GetFileNameWithoutExtension(file);
        return await Task.Run(
            () => OutlookSignatureParser.Parse(signatureName, html, relative => ReadRelative(folder, relative), ansiCodePage),
            cancellationToken).ConfigureAwait(false);
    }

    public override string ToString() => nameof(OutlookSignatureFiles);

    /// <summary>
    /// The bytes of a file referenced by a signature, or null when the reference is refused or unreadable: only a relative
    /// path (no drive, no UNC, no scheme, no ':'), no ".." segment (also not URL-encoded), resolved inside
    /// <paramref name="folder"/> (also through symbolic links) and at most <see cref="MaxFileBytes"/>. The path is tried as
    /// written and URL-decoded ("M%C3%B3j%20podpis_files/image001.png").
    /// </summary>
    internal static byte[]? ReadRelative(string folder, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        var root = Path.GetFullPath(folder);
        var candidates = new List<string> { relative.Trim() };
        try
        {
            var decoded = Uri.UnescapeDataString(relative.Trim());
            if (!string.Equals(decoded, candidates[0], StringComparison.Ordinal))
            {
                candidates.Add(decoded);
            }
        }
        catch (UriFormatException)
        {
            // Keep the path as written.
        }

        // Every spelling must be safe: an encoded ".." must not slip through as the decoded one.
        if (!candidates.All(IsSafeRelative))
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            var path = Resolve(root, candidate);
            if (path is not null && File.Exists(path))
            {
                try
                {
                    return ReadLimited(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>A relative reference without a rooted form, drive, UNC, scheme, ':' or "." / ".." segments.</summary>
    internal static bool IsSafeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':', StringComparison.Ordinal) || relative.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(relative) || Path.IsPathRooted(normalized))
        {
            return false;
        }

        var segments = normalized.Split('/');
        return segments.All(segment => segment.Length > 0 && segment != "." && segment != ".." && segment.Trim().Length > 0)
            && segments.All(segment => segment.Trim('.').Length > 0);
    }

    /// <summary>A name of a signature: no path, no "..", no invalid file name character.</summary>
    internal static bool IsSafeName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Trim('.').Length > 0
        && name.IndexOfAny(['/', '\\', ':', '\0']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>The full path of <paramref name="relative"/> under <paramref name="root"/> when it (and the final target of
    /// a symbolic link) stays inside; else null.</summary>
    private static string? Resolve(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(root, path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !IsInside(root, Path.GetFullPath(target.FullName)))
                {
                    return null;
                }
            }

            // A directory link on the way (e.g. Name_files → elsewhere) must not lead out either.
            var directory = info.Directory;
            while (directory is not null && IsInside(root, directory.FullName))
            {
                if (directory.LinkTarget is not null)
                {
                    var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is null || !IsInside(root, Path.GetFullPath(target.FullName)))
                    {
                        return null;
                    }
                }

                directory = directory.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return path;
    }

    private static bool IsInside(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    private static string? FindFile(string folder, string name)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        try
        {
            string? match = null;
            foreach (var file in Directory.EnumerateFiles(folder, "*", Enumeration()))
            {
                if (!IsSignatureFile(file) || !string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // An exact spelling wins over another case; ".htm" (Outlook's own) over ".html".
                if (match is null || Rank(file, name) < Rank(match, name))
                {
                    match = file;
                }
            }

            return match is null ? null : Resolve(Path.GetFullPath(folder), Path.GetFileName(match));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        static int Rank(string file, string name) =>
            (string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.Ordinal) ? 0 : 2)
            + (Path.GetExtension(file).Equals(".htm", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
    }

    private static bool IsSignatureFile(string file) =>
        SignatureExtensions.Any(extension => Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase));

    private static EnumerationOptions Enumeration() => new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.Directory,
    };

    /// <summary>The file's bytes, or null when it is larger than <see cref="MaxFileBytes"/> (read with a bound, not trusting
    /// the length).</summary>
    private static byte[]? ReadLimited(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[MaxFileBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        return total > MaxFileBytes ? null : buffer[..total];
    }

    private static Task<byte[]?> ReadLimitedAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => ReadLimited(path), cancellationToken);

    private static string? DefaultFolder() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Signatures")
        : null;
}
