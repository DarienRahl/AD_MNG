using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// Reads the <c>file:</c> images of a pasted message (Outlook's and Word's <c>…\Temp\msohtmlclip1\01\clip_image001.png</c>)
/// under <see cref="ClipboardImagePolicy"/> (design note u1c-u1d section 7.1): only a local drive path whose final path –
/// 8.3 names expanded, no link or reparse point on the way – is an Office clipboard image
/// (<c>msohtmlclip&lt;n&gt;\&lt;nn&gt;\clip_image&lt;n&gt;.png|jpg|jpeg|gif</c>) of the equally resolved temp folder is opened, once,
/// asynchronously, for at most <c>maxBytes + 1</c> bytes. A network path is never touched (it would leak the operator's
/// NTLM credentials to a foreign server). Only the base name of the file is kept; the path (it contains the Windows user
/// name) is never stored or logged.
/// </summary>
public sealed class PastedImageFileSource : IPastedImageSource
{
    private readonly string _tempFolder;
    private readonly bool _windows;
    private readonly Func<string, string?>? _expandShortNames;

    /// <param name="tempFolder">The user's temp folder (default: <see cref="Path.GetTempPath"/>).</param>
    /// <param name="windows">Windows path rules (default: the current platform; tests run the Unix rules).</param>
    /// <param name="expandShortNames">Windows: expands 8.3 short names of an existing local path (the App's
    /// <c>GetLongPathNameW</c>; null when the path does not exist). Never called for a path outside a local fixed drive.</param>
    public PastedImageFileSource(string? tempFolder = null, bool? windows = null, Func<string, string?>? expandShortNames = null)
    {
        _tempFolder = string.IsNullOrWhiteSpace(tempFolder) ? Path.GetTempPath() : tempFolder;
        _windows = windows ?? OperatingSystem.IsWindows();
        _expandShortNames = expandShortNames;
    }

    public async Task<PastedImageRead> ReadAsync(string fileUri, int maxBytes, CancellationToken cancellationToken)
    {
        var local = ClipboardImagePolicy.LocalPath(fileUri, _windows);
        if (local is null)
        {
            return new PastedImageRead(null, string.Empty, PastedImageFailure.NotAllowed);
        }

        var name = ClipboardImagePolicy.FileName(local);
        if (!ClipboardImagePolicy.IsClipboardImageName(name))
        {
            // Another program's picture (HTML copied from a web page may name any file): not even its path is resolved.
            return new PastedImageRead(null, name, PastedImageFailure.NotAllowed);
        }

        var tempFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_tempFolder));

        // Nothing outside the temp folder's local drive is touched – not even to resolve its name (a mapped network drive
        // would authenticate to its server).
        if (!SameLocalDrive(local, tempFolder))
        {
            return new PastedImageRead(null, name, PastedImageFailure.NotAllowed);
        }

        var folder = Resolve(tempFolder);
        if (folder.Failure is not null)
        {
            return new PastedImageRead(null, name, PastedImageFailure.NotAllowed);
        }

        var file = Resolve(local);
        if (file.Failure is { } failure)
        {
            return new PastedImageRead(null, name, failure);
        }

        if (!ClipboardImagePolicy.IsClipboardImage(file.Path, folder.Path, _windows))
        {
            return new PastedImageRead(null, name, PastedImageFailure.NotAllowed);
        }

        try
        {
            await using var stream = new FileStream(file.Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > maxBytes)
            {
                return new PastedImageRead(null, name, PastedImageFailure.TooLarge);
            }

            var buffer = new byte[(int)Math.Min(maxBytes + 1L, stream.Length + 1)];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return total > maxBytes
                ? new PastedImageRead(null, name, PastedImageFailure.TooLarge)
                : new PastedImageRead(buffer[..total], name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new PastedImageRead(null, name, PastedImageFailure.NotFound);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new PastedImageRead(null, name, PastedImageFailure.ReadError);
        }
    }

    /// <summary>Windows: both paths on the same drive letter, and that drive is not a network drive. Elsewhere: true.</summary>
    private bool SameLocalDrive(string path, string folder)
    {
        if (!_windows)
        {
            return true;
        }

        if (path.Length < 3 || folder.Length < 3 || char.ToUpperInvariant(path[0]) != char.ToUpperInvariant(folder[0]) || folder[1] != ':')
        {
            return false;
        }

        try
        {
            return OperatingSystem.IsWindows() && new DriveInfo(path[..1]).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The final path of an existing local path: 8.3 names expanded (Windows, through the App), then every segment below
    /// the root checked – it must exist and must not be a reparse point (symbolic link, junction, mount point).
    /// </summary>
    private (string? Path, PastedImageFailure? Failure) Resolve(string path)
    {
        try
        {
            if (_windows && _expandShortNames is not null && path.Contains('~', StringComparison.Ordinal))
            {
                if (_expandShortNames(path) is not { Length: > 0 } expanded)
                {
                    return (null, PastedImageFailure.NotFound);
                }

                path = expanded;
            }

            var root = System.IO.Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal))
            {
                return (null, PastedImageFailure.NotAllowed);
            }

            var current = root;
            var separators = _windows ? new[] { '\\', '/' } : ['/'];
            foreach (var segment in path[root.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = System.IO.Path.Combine(current, segment);
                FileSystemInfo info = File.Exists(next) ? new FileInfo(next) : new DirectoryInfo(next);
                if (!info.Exists)
                {
                    return (null, PastedImageFailure.NotFound);
                }

                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                {
                    return (null, PastedImageFailure.NotAllowed);
                }

                current = next;
            }

            return (current, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return (null, PastedImageFailure.ReadError);
        }
    }

    public override string ToString() => nameof(PastedImageFileSource);
}
