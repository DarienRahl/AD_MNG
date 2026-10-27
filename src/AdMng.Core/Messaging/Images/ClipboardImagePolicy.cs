using System.Text.RegularExpressions;

namespace AdMng.Core.Messaging.Images;

/// <summary>
/// Which <c>file:</c> image sources of a pasted message may be read (design note section 7.1). Outlook and Word put the
/// images of a copied message into the user's temp folder (<c>…\Temp\msohtmlclip1\01\clip_image001.png</c>) and reference
/// them as <c>file:///C:/…</c>. Only such a local drive path is accepted – a UNC path (<c>\\server\share</c>,
/// <c>file://server/…</c>, <c>file:////server/…</c>) would make Windows authenticate to a foreign server and leak the
/// operator's NTLM credentials, so it is never opened; device paths (<c>\\?\</c>, <c>\\.\</c>), alternate data streams and
/// hosts are refused alike. Only the images of an Office clipboard fragment are read
/// (<c>msohtmlclip&lt;n&gt;\&lt;nn&gt;\clip_image&lt;n&gt;.png|jpg|jpeg|gif</c>) – HTML copied from a web page may name any
/// other picture of the temp folder, which must not end up in a mass mail. The caller resolves the final path (8.3 names
/// expanded, links and reparse points rejected) and checks it with <see cref="IsClipboardImage"/> against the equally
/// resolved temp folder. Pure: no file system access.
/// </summary>
public static partial class ClipboardImagePolicy
{
    /// <summary>
    /// The local path of a <c>file:</c> URI, or null when it must not be opened. Windows: <c>file:///X:/…</c> (or the old
    /// <c>file:///X|/…</c>); elsewhere (tests, never shipped): <c>file:///absolute/path</c>. Percent-escapes are decoded,
    /// separators normalized, <c>.</c> segments removed and <c>..</c> segments applied (never above the root).
    /// </summary>
    public static string? LocalPath(string? uri, bool windows)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        var value = uri.Trim();
        if (!value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = value[5..];
        // file:///path – an empty host. file://host/…, file:////server/… and file:path are refused.
        if (!rest.StartsWith("///", StringComparison.Ordinal) || rest.StartsWith("////", StringComparison.Ordinal))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(rest[3..]);
        }
        catch (UriFormatException)
        {
            return null;
        }

        var cut = decoded.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            decoded = decoded[..cut];
        }

        if (decoded.Length == 0 || decoded.Any(c => char.IsControl(c) || c is '*' or '"' or '<' or '>'))
        {
            return null;
        }

        return windows ? WindowsPath(decoded) : UnixPath("/" + decoded);
    }

    /// <summary>
    /// True when <paramref name="path"/> lies inside <paramref name="folder"/> (both resolved final paths): a prefix up to
    /// a separator, case-insensitive on Windows.
    /// </summary>
    public static bool IsInside(string? path, string? folder, bool windows)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder))
        {
            return false;
        }

        var separator = windows ? '\\' : '/';
        var normalizedFolder = windows ? folder.Replace('/', '\\') : folder;
        var normalizedPath = windows ? path.Replace('/', '\\') : path;
        normalizedFolder = normalizedFolder.TrimEnd(separator) + separator;
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalizedPath.Length > normalizedFolder.Length && normalizedPath.StartsWith(normalizedFolder, comparison);
    }

    /// <summary>True when <paramref name="fileName"/> is the name Office gives a clipboard fragment's image
    /// (<c>clip_image001.png</c>, <c>.jpg</c>, <c>.jpeg</c>, <c>.gif</c>; case-insensitive).</summary>
    public static bool IsClipboardImageName(string? fileName) => fileName is not null && ImageNamePattern().IsMatch(fileName);

    /// <summary>
    /// True when <paramref name="path"/> is an image of an Office clipboard fragment directly in <paramref name="folder"/>
    /// (both resolved final paths): <c>msohtmlclip&lt;n&gt;\&lt;nn&gt;\clip_image&lt;n&gt;.png|jpg|jpeg|gif</c> (names
    /// case-insensitive).
    /// </summary>
    public static bool IsClipboardImage(string? path, string? folder, bool windows)
    {
        if (!IsInside(path, folder, windows))
        {
            return false;
        }

        var separator = windows ? '\\' : '/';
        var root = (windows ? folder!.Replace('/', '\\') : folder!).TrimEnd(separator) + separator;
        var relative = (windows ? path!.Replace('/', '\\') : path!)[root.Length..].Split(separator);
        return relative.Length == 3
               && FolderPattern().IsMatch(relative[0])
               && relative[1].Length > 0 && relative[1].All(char.IsAsciiDigit)
               && IsClipboardImageName(relative[2]);
    }

    /// <summary>The base name of a path ("clip_image001.png") – the only part of a clipboard path ever kept.</summary>
    public static string FileName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var cut = path.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? path[(cut + 1)..] : path;
    }

    private static string? WindowsPath(string decoded)
    {
        var path = decoded.Replace('/', '\\');
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] is not (':' or '|') || path[2] != '\\')
        {
            return null;
        }

        // A colon after the drive is an alternate data stream; "\\?\" and "\\.\" never reach this point (no drive letter).
        if (path.IndexOf(':', 2) >= 0 || path.IndexOf('|', 2) >= 0)
        {
            return null;
        }

        var segments = Normalize(path[3..].Split('\\'));
        return segments is null ? null : char.ToUpperInvariant(path[0]) + ":\\" + string.Join('\\', segments);
    }

    private static string? UnixPath(string decoded)
    {
        if (!decoded.StartsWith('/') || decoded.StartsWith("//", StringComparison.Ordinal) || decoded.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        var segments = Normalize(decoded[1..].Split('/'));
        return segments is null ? null : "/" + string.Join('/', segments);
    }

    /// <summary>Removes empty and "." segments and applies ".."; null when ".." would leave the root or nothing is left.</summary>
    private static List<string>? Normalize(string[] parts)
    {
        var segments = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            if (part.Trim().Length == 0 || part.EndsWith('.') && part.Trim('.').Length == 0)
            {
                return null;
            }

            segments.Add(part);
        }

        return segments.Count == 0 ? null : segments;
    }

    [GeneratedRegex(@"^clip_image\d+\.(?:png|jpe?g|gif)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageNamePattern();

    [GeneratedRegex(@"^msohtmlclip\d*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FolderPattern();
}
