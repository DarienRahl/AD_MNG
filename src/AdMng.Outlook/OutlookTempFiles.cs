using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AdMng.Outlook;

/// <summary>
/// Private temp folders of the Outlook engine (design note docs/dev/u5-outlook-correspondence.md D5 and 4.1):
/// <c>Attachments.Add</c> needs a file, so the inline images of one item (operator content, never directory data) are written
/// to <c>%TEMP%\AdMng-Outlook-{pid}-{random}</c>, created per item and deleted as soon as the item is saved (or rendered).
/// <see cref="CleanupStale"/> removes the folders of processes that no longer run (a crash between write and delete).
/// </summary>
internal sealed partial class OutlookTempFiles
{
    /// <summary>The prefix of every folder.</summary>
    public const string Prefix = "AdMng-Outlook-";

    private readonly Func<int, bool> _isRunning;

    /// <param name="root">The parent folder (tests); null: <see cref="Path.GetTempPath"/>.</param>
    /// <param name="isRunning">Whether a process id is alive (tests); null: <see cref="Process.GetProcessById(int)"/>.</param>
    public OutlookTempFiles(string? root = null, Func<int, bool>? isRunning = null)
    {
        Root = root ?? Path.GetTempPath();
        _isRunning = isRunning ?? IsProcessRunning;
    }

    /// <summary>The parent folder of the item folders.</summary>
    public string Root { get; }

    /// <summary>Creates a new private folder for one item (owner only on Unix; the user's temp ACL on Windows).</summary>
    public OutlookTempFolder Create()
    {
        Directory.CreateDirectory(Root);
        var name = string.Create(
            CultureInfo.InvariantCulture, $"{Prefix}{Environment.ProcessId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");
        var path = Path.Combine(Root, name);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new OutlookTempFolder(path);
    }

    /// <summary>
    /// Deletes the folders <c>AdMng-Outlook-{pid}-*</c> of processes that no longer run (best effort; folders of running
    /// processes and anything else in the temp folder are left alone). Returns the number of folders deleted.
    /// </summary>
    public int CleanupStale()
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(Root))
            {
                return 0;
            }

            foreach (var folder in Directory.EnumerateDirectories(Root, Prefix + "*", new EnumerationOptions { IgnoreInaccessible = true }))
            {
                var match = FolderPattern().Match(Path.GetFileName(folder));
                if (!match.Success
                    || !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                    || pid == Environment.ProcessId
                    || _isRunning(pid))
                {
                    continue;
                }

                if (OutlookTempFolder.TryDelete(folder))
                {
                    deleted++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next start tries again.
        }

        return deleted;
    }

    public override string ToString() => nameof(OutlookTempFiles);

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^AdMng-Outlook-(?<pid>[0-9]{1,10})-[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex FolderPattern();
}

/// <summary>
/// One item's temp folder: files written with unique, safe names (<c>[A-Za-z0-9._-]</c>, never a path); disposing deletes
/// the folder with everything in it.
/// </summary>
internal sealed partial class OutlookTempFolder : IDisposable
{
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public OutlookTempFolder(string path)
    {
        Path = path;
    }

    /// <summary>The folder.</summary>
    public string Path { get; }

    /// <summary>Writes <paramref name="data"/> to a new file named after <paramref name="fileName"/> (made safe and unique)
    /// and returns its full path.</summary>
    public string Write(string? fileName, ReadOnlySpan<byte> data)
    {
        var path = System.IO.Path.Combine(Path, Reserve(fileName));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(data);
        return path;
    }

    /// <summary>A unique, safe file name in this folder for <paramref name="fileName"/> (the file is not created).</summary>
    public string Reserve(string? fileName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var safe = SafeName(fileName);
        var stem = System.IO.Path.GetFileNameWithoutExtension(safe);
        var extension = System.IO.Path.GetExtension(safe);
        var candidate = safe;
        for (var number = 2; !_names.Add(candidate); number++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{stem}-{number}{extension}");
        }

        return candidate;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryDelete(Path);
    }

    public override string ToString() => nameof(OutlookTempFolder);

    /// <summary>Deletes a folder and its files (read-only flags cleared); false when it could not be deleted.</summary>
    internal static bool TryDelete(string folder)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return true;
                }

                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(folder, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Outlook may still hold a file for a moment: one more try, then the next start cleans up.
                Thread.Sleep(50);
            }
        }

        return false;
    }

    /// <summary><c>[A-Za-z0-9._-]{1,64}</c> without a leading dot; anything else becomes "file".</summary>
    internal static string SafeName(string? fileName)
    {
        var name = fileName ?? string.Empty;
        var cut = name.LastIndexOfAny(['/', '\\']);
        if (cut >= 0)
        {
            name = name[(cut + 1)..];
        }

        name = UnsafeCharacters().Replace(name, "_").TrimStart('.');
        if (name.Length > 64)
        {
            var extension = System.IO.Path.GetExtension(name);
            name = extension.Length is > 0 and <= 10 ? name[..(64 - extension.Length)] + extension : name[..64];
        }

        return name.Trim('_').Length == 0 || name.Trim('.').Length == 0 ? "file" : name;
    }

    [GeneratedRegex("[^A-Za-z0-9._-]", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeCharacters();
}
