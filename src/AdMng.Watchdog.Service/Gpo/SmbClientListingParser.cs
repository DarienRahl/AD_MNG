using System.Globalization;
using System.Text.RegularExpressions;
using AdMng.Watchdog.Core.Gpo;

namespace AdMng.Watchdog.Service.Gpo;

/// <summary>
/// Parses the output of <c>smbclient -D &lt;domain&gt;/Policies/{GUID} -c 'recurse ON; ls'</c> (stage B design 8.3; format of
/// Samba's <c>display_finfo</c>: <c>"  %-30s%7.7s %8.0f  %s"</c> – name, attributes, size, asctime of the modification time,
/// printed in UTC because the reader starts smbclient with <c>TZ=UTC</c>), with the folder headers
/// <c>\&lt;domain&gt;\Policies\{GUID}\&lt;sub folder&gt;</c> of the recursion. The output is untrusted: a name may contain
/// spaces, look like a size or a date, or carry line breaks that forge lines. Entries are therefore parsed from the
/// right (size and date must end the line), only headers naming a folder already listed below the GPO folder are
/// followed, and every line that is neither an entry, a known header, the free-space trailer nor an error is counted in
/// <see cref="SysvolListing.RejectedEntries"/> (entries below an unknown header too). Paths are validated again by the
/// planner (<see cref="GpoRelativePath"/>). Lines with <c>NT_STATUS_*</c> codes are returned as errors, never logged.
/// </summary>
public static partial class SmbClientListingParser
{
    /// <summary>Upper bound of the parsed output (2 000 entries need ~200 KiB).</summary>
    public const int MaxOutputChars = 16 * 1024 * 1024;

    /// <summary>The parsed listing and the <c>NT_STATUS_*</c> codes of its error lines (in order).</summary>
    public sealed record Result(SysvolListing Listing, IReadOnlyList<string> Errors);

    public static Result Parse(string output, SysvolGpoFolder folder)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(folder);
        var root = "\\" + folder.Directory.Replace('/', '\\');
        var entries = new List<SysvolEntry>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        var rejected = 0;
        string? current = string.Empty; // the GPO folder itself comes first, without a header
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                ParseLine(line);
            }
            catch (RegexMatchTimeoutException)
            {
                rejected++;
            }
        }

        return new Result(new SysvolListing { Entries = entries, RejectedEntries = rejected }, errors);

        void ParseLine(string line)
        {

            if (NtStatus().Match(line) is { Success: true } status && !EntryLine().IsMatch(line))
            {
                errors.Add(status.Value);
                return;
            }

            if (line[0] == '\\')
            {
                current = Header(line, root, directories);
                if (current is null)
                {
                    rejected++;
                }

                return;
            }

            if (Trailer().IsMatch(line))
            {
                return;
            }

            var match = EntryLine().Match(line);
            if (!match.Success || current is null)
            {
                rejected++;
                return;
            }

            var name = match.Groups["name"].Value;
            if (name is "." or "..")
            {
                return;
            }

            if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal)
                || !long.TryParse(match.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || !TryParseTime(match.Groups["date"].Value, out var modified))
            {
                rejected++;
                return;
            }

            var path = current.Length == 0 ? name : current + "/" + name;
            var isDirectory = match.Groups["attr"].Value.Contains('D', StringComparison.Ordinal);
            if (isDirectory)
            {
                directories.Add(path);
            }

            entries.Add(new SysvolEntry { RelativePath = path, IsDirectory = isDirectory, Size = isDirectory ? 0 : size, ModifiedAt = modified });
        }
    }

    /// <summary>The relative folder of a header line, or null when it is not a folder already listed below the GPO folder.</summary>
    private static string? Header(string line, string root, HashSet<string> directories)
    {
        if (!line.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = line[(root.Length + 1)..].Replace('\\', '/');
        return directories.Contains(relative) ? relative : null;
    }

    private static bool TryParseTime(string value, out DateTimeOffset time)
    {
        // asctime: "Tue Oct  6 08:01:15 2026" (day padded with a space).
        var normalized = MultipleSpaces().Replace(value, " ");
        if (DateTime.TryParseExact(normalized, "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            time = new DateTimeOffset(parsed, TimeSpan.Zero);
            return true;
        }

        time = default;
        return false;
    }

    [GeneratedRegex(@"^  (?<name>.+?)\s+(?<attr>[A-Za-z]{0,8})\s+(?<size>\d{1,19})  (?<date>[A-Z][a-z]{2} [A-Z][a-z]{2} [ \d]\d \d{2}:\d{2}:\d{2} \d{4})$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"^\s+\d+ blocks of size \d+\. \d+ blocks available$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Trailer();

    [GeneratedRegex(@"NT_STATUS_[A-Z0-9_]{1,64}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    internal static partial Regex NtStatus();

    [GeneratedRegex(" {2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MultipleSpaces();
}
