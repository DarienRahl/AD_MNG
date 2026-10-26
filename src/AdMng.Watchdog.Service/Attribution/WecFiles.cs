using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Attribution;

/// <summary>One OpenWEC output file of a client directory (stage B design 7.4, 7.5).</summary>
/// <param name="Path">Full path.</param>
/// <param name="Name"><c>security.jsonl</c> (active) or <c>security.jsonl.&lt;UTC yyyyMMddTHHmmssZ&gt;</c> (rotated).</param>
/// <param name="Device"><c>st_dev</c>.</param>
/// <param name="Inode"><c>st_ino</c>.</param>
/// <param name="Size">Size when listed.</param>
/// <param name="ModifiedAt">Last write.</param>
/// <param name="RotatedAt">The rotation time of the name; null for the active file.</param>
/// <param name="FirstLineSha256">SHA-256 of the first line including its line feed; null while the file has no complete
/// line yet (it cannot be identified, nothing can be consumed).</param>
public sealed record WecFileInfo(string Path, string Name, ulong Device, ulong Inode, long Size, DateTimeOffset ModifiedAt,
    DateTimeOffset? RotatedAt, string? FirstLineSha256)
{
    /// <summary>True for a rotated file (renamed by <c>admng-wec-rotate</c>; OpenWEC stops writing it after the reload).</summary>
    public bool IsRotated => RotatedAt is not null;
}

/// <summary>A complete line read from an output file (without its line feed).</summary>
/// <param name="Start">Offset of the first byte.</param>
/// <param name="End">Offset after the line feed.</param>
/// <param name="Bytes">The line.</param>
public sealed record WecLine(long Start, long End, ReadOnlyMemory<byte> Bytes);

/// <summary>The lines read from one position of one file.</summary>
/// <param name="Lines">Complete lines up to the batch limits.</param>
/// <param name="ConsumedTo">Offset after the last complete line (or skipped long line) – the cursor after this batch.</param>
/// <param name="Length">The file length seen while reading.</param>
/// <param name="SkippedLongLines">Lines longer than <see cref="WecFiles.MaxLineBytes"/> skipped (counted as malformed).</param>
public sealed record WecReadResult(IReadOnlyList<WecLine> Lines, long ConsumedTo, long Length, int SkippedLongLines);

/// <summary>
/// File mechanics of the WEF tailer (stage B design 7.5): the output files of a client directory in consumption order
/// (rotated files by their timestamp suffix, then the active file; symbolic links and other names are ignored), their
/// identity (<c>fstat</c> of the opened handle, first-line hash) and complete lines from an offset. Files are opened with
/// <c>FileShare.ReadWrite | FileShare.Delete</c> and only read.
/// </summary>
[SupportedOSPlatform("linux")]
public static class WecFiles
{
    /// <summary>Lines longer than this are skipped and counted (7.5).</summary>
    public const int MaxLineBytes = 1024 * 1024;

    /// <summary>Lines per batch (7.5).</summary>
    public const int MaxLinesPerBatch = 1000;

    /// <summary>Bytes read per batch at most (a batch also ends there).</summary>
    public const int MaxBytesPerBatch = 8 * 1024 * 1024;

    private const int ChunkSize = 64 * 1024;

    /// <summary>The files of <paramref name="directory"/> named <paramref name="fileName"/> or <c>&lt;fileName&gt;.&lt;stamp&gt;</c>,
    /// in consumption order.</summary>
    public static IReadOnlyList<WecFileInfo> List(string directory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var files = new List<WecFileInfo>();
        IEnumerable<string> paths;
        try
        {
            paths = System.IO.Directory.EnumerateFiles(directory, fileName + "*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return files;
        }

        foreach (var path in paths)
        {
            var name = System.IO.Path.GetFileName(path);
            DateTimeOffset? rotatedAt = null;
            if (!string.Equals(name, fileName, StringComparison.Ordinal))
            {
                if (name.Length != fileName.Length + 17 || !name.StartsWith(fileName + ".", StringComparison.Ordinal)
                    || !DateTimeOffset.TryParseExact(name[(fileName.Length + 1)..], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp))
                {
                    continue;
                }

                rotatedAt = stamp;
            }

            if (Describe(path, name, rotatedAt) is { } file)
            {
                files.Add(file);
            }
        }

        return [.. files.OrderBy(f => f.RotatedAt is null ? 1 : 0).ThenBy(f => f.RotatedAt).ThenBy(f => f.Name, StringComparer.Ordinal)];
    }

    /// <summary>The identity of a regular file (null for a symbolic link, another type or a file that vanished).</summary>
    public static WecFileInfo? Describe(string path, string name, DateTimeOffset? rotatedAt)
    {
        try
        {
            if (PosixPermissionCheck.Stat(path) is not { IsRegularFile: true, IsSymbolicLink: false })
            {
                return null;
            }

            using var stream = Open(path);
            var status = PosixPermissionCheck.FStat(stream.SafeFileHandle);
            return new WecFileInfo(path, name, status.Device, status.Inode, status.Size, status.ModifiedAt, rotatedAt, FirstLineHash(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads complete lines of <paramref name="file"/> from <paramref name="offset"/>; null when the path now
    /// holds another file (renamed or replaced since it was listed) or vanished.</summary>
    public static WecReadResult? Read(WecFileInfo file, long offset, int maxLines = MaxLinesPerBatch, int maxBytes = MaxBytesPerBatch)
    {
        ArgumentNullException.ThrowIfNull(file);
        FileStream stream;
        try
        {
            stream = Open(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using (stream)
        {
            var status = PosixPermissionCheck.FStat(stream.SafeFileHandle);
            if (status.Device != file.Device || status.Inode != file.Inode)
            {
                return null;
            }

            return ReadLines(stream, offset, maxLines, maxBytes);
        }
    }

    /// <summary>Complete lines of <paramref name="stream"/> from <paramref name="offset"/> (see <see cref="Read"/>).</summary>
    public static WecReadResult ReadLines(Stream stream, long offset, int maxLines = MaxLinesPerBatch, int maxBytes = MaxBytesPerBatch)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = stream.Length;
        var lines = new List<WecLine>();
        var consumed = Math.Min(offset, length);
        var skipped = 0;
        stream.Position = consumed;
        var pending = new MemoryStream();          // bytes of the current line
        var lineStart = consumed;
        var skipping = false;                      // inside a line longer than MaxLineBytes
        var position = consumed;
        var budget = 0L;
        var buffer = new byte[ChunkSize];
        while (lines.Count < maxLines && (budget < maxBytes || skipping))   // a skipped line is read to its end (no memory)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, Math.Max(0, length - position)));
            if (read <= 0)
            {
                break;
            }

            var start = 0;
            for (var i = 0; i < read && lines.Count < maxLines; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                var end = position + i + 1;
                if (!skipping)
                {
                    pending.Write(buffer, start, i - start);
                    if (pending.Length > MaxLineBytes)
                    {
                        skipped++;
                    }
                    else
                    {
                        lines.Add(new WecLine(lineStart, end, pending.ToArray()));
                    }
                }
                else
                {
                    skipped++;
                    skipping = false;
                }

                pending.SetLength(0);
                consumed = end;
                lineStart = end;
                start = i + 1;
                if (lines.Count >= maxLines)
                {
                    break;
                }
            }

            budget += read;
            if (lines.Count >= maxLines)
            {
                break;
            }

            if (!skipping)
            {
                pending.Write(buffer, start, read - start);
                if (pending.Length > MaxLineBytes)
                {
                    skipping = true;          // the rest of this line is discarded until its line feed
                    pending.SetLength(0);
                }
            }

            position += read;
        }

        return new WecReadResult(lines, consumed, length, skipped);
    }

    /// <summary>SHA-256 (hex, lower case) of the first line including its line feed; null without a complete line.</summary>
    public static string? FirstLineHash(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                hash.AppendData(buffer, 0, newline + 1);
                return Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            hash.AppendData(buffer, 0, read);
        }

        return null;
    }

    private static FileStream Open(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.SequentialScan,
        BufferSize = 0,
    });
}
