using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AdMng.Watchdog.Service.Attribution;

/// <summary>One file in <c>wec-progress.json</c> (stage B design 7.4 step 3, 7.5).</summary>
/// <param name="Client">The client directory.</param>
/// <param name="Name">The file name.</param>
/// <param name="Device"><c>st_dev</c>.</param>
/// <param name="Inode"><c>st_ino</c>.</param>
/// <param name="FirstLineSha256">First-line hash (identity).</param>
/// <param name="Offset">Bytes consumed and committed.</param>
/// <param name="Size">Size when the progress was written.</param>
/// <param name="Complete">A rotated file consumed to its size and committed – <c>admng-wec-rotate</c> may delete it (it
/// still checks that size and identity are unchanged and the file was not modified for 5 minutes).</param>
public sealed record WecProgressEntry(string Client, string Name, ulong Device, ulong Inode, string FirstLineSha256, long Offset, long Size, bool Complete);

/// <summary>
/// <c>/run/admng-watchdog/wec-progress.json</c> (stage B design 7.4, 7.5): rewritten after every commit by an atomic rename;
/// read by the root script <c>admng-wec-rotate</c>, which deletes a rotated file only when it is listed complete. One
/// file entry per line with a fixed key order, so the script can read it with <c>grep</c>/<c>sed</c> (no JSON tool on a
/// minimal Debian) – and it is still JSON. Names come from the directory listing; OpenWEC limits client directories to
/// <c>[A-Za-z0-9.-_@]</c> and the tailer lists only <c>security.jsonl[.stamp]</c>.
/// </summary>
public static class WecProgressFile
{
    /// <summary>The format version.</summary>
    public const int Version = 1;

    /// <summary>The file content of <paramref name="entries"/>.</summary>
    public static string Format(IEnumerable<WecProgressEntry> entries, DateTimeOffset writtenAt)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture,
            $"{{\"version\":{Version},\"writtenAt\":{JsonSerializer.Serialize(writtenAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))},\"files\":[\n");
        var first = true;
        foreach (var e in entries)
        {
            if (!first)
            {
                builder.Append(",\n");
            }

            first = false;
            builder.Append(CultureInfo.InvariantCulture,
                $"{{\"client\":{JsonSerializer.Serialize(e.Client)},\"name\":{JsonSerializer.Serialize(e.Name)},\"device\":{e.Device},\"inode\":{e.Inode},\"firstLineSha256\":\"{e.FirstLineSha256}\",\"offset\":{e.Offset},\"size\":{e.Size},\"complete\":{(e.Complete ? "true" : "false")}}}");
        }

        builder.Append("\n]}\n");
        return builder.ToString();
    }

    /// <summary>Writes <paramref name="path"/> atomically (temporary file in the same folder, then rename). Returns false
    /// when the folder does not exist (the service is not running as the unit – nothing to tell the rotation).</summary>
    public static bool Write(string path, IEnumerable<WecProgressEntry> entries, DateTimeOffset writtenAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
        {
            return false;
        }

        var temporary = Path.Combine(folder, "." + Path.GetFileName(path) + ".tmp-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        File.WriteAllText(temporary, Format(entries, writtenAt), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        return true;
    }

    /// <summary>The entries of a file written by <see cref="Write"/> (tests, diagnostics).</summary>
    public static IReadOnlyList<WecProgressEntry> Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.GetProperty("files").EnumerateArray().Select(e => new WecProgressEntry(
            e.GetProperty("client").GetString()!,
            e.GetProperty("name").GetString()!,
            e.GetProperty("device").GetUInt64(),
            e.GetProperty("inode").GetUInt64(),
            e.GetProperty("firstLineSha256").GetString()!,
            e.GetProperty("offset").GetInt64(),
            e.GetProperty("size").GetInt64(),
            e.GetProperty("complete").GetBoolean()))];
    }
}
