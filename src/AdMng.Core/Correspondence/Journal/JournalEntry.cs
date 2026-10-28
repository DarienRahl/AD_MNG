using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AdMng.Core.Correspondence.Import;

namespace AdMng.Core.Correspondence.Journal;

/// <summary>
/// One line of the Korespondencja journal "już wysłane" (decision D4): the keyed hash of an address, when it was sent and
/// the subject as typed (fields not filled in). Never the address.
/// </summary>
public sealed record JournalEntry(string AddressHash, DateTimeOffset SentAt, string Subject)
{
    public override string ToString() => nameof(JournalEntry);
}

/// <summary>The keyed hashes of the journal.</summary>
public static class JournalHashing
{
    /// <summary>Length of the per-user key.</summary>
    public const int KeyBytes = 32;

    /// <summary>Bytes of the HMAC kept in a hash (128 bits).</summary>
    public const int HashBytes = 16;

    /// <summary>Base64url of the first 16 bytes of HMAC-SHA-256(key, UTF-8(EmailAddressText.Normalize(address))).</summary>
    /// <exception cref="ArgumentException">The key is not <see cref="KeyBytes"/> long, or the address is blank.</exception>
    public static string Hash(ReadOnlySpan<byte> key, string address)
    {
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException($"The journal key must be {KeyBytes} bytes long.", nameof(key));
        }

        var normalized = EmailAddressText.Normalize(address);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("The address is empty.", nameof(address));
        }

        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalized), mac);
        var hash = Base64Url.EncodeToString(mac[..HashBytes]);
        CryptographicOperations.ZeroMemory(mac);
        return hash;
    }

    /// <summary>True for a value of the form <see cref="Hash"/> writes (22 base64url characters).</summary>
    public static bool IsHash(string? value) =>
        value is { Length: 22 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>The journal file: a header line, then one line per sent address.</summary>
/// <remarks>A file whose first line names another version of the format is not read (no entries); a missing header is
/// tolerated. A line is <c>hash TAB time TAB subject</c>: the hash as <see cref="JournalHashing.Hash"/> writes it, the time
/// in UTC to the second; lines that do not parse are skipped. Subjects longer than <see cref="MaxSubjectLength"/> are
/// cut.</remarks>
public static class JournalFile
{
    public const string Header = "AdMng.CorrespondenceJournal\t1";

    /// <summary>Longest subject kept in a line.</summary>
    public const int MaxSubjectLength = 200;

    private const string HeaderPrefix = "AdMng.CorrespondenceJournal\t";
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const int MaxLineLength = 4096;

    /// <summary>The entries of a journal file; malformed lines are skipped.</summary>
    public static IReadOnlyList<JournalEntry> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var entries = new List<JournalEntry>();
        var first = true;
        while (reader.ReadLine() is { } line)
        {
            if (first)
            {
                first = false;
                var trimmed = line.TrimStart('\uFEFF').TrimEnd();
                if (trimmed == Header)
                {
                    continue;
                }

                if (trimmed.StartsWith(HeaderPrefix, StringComparison.Ordinal))
                {
                    return [];   // another version of the format
                }
            }

            if (Parse(line) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary><c>hash \t yyyy-MM-ddTHH:mm:ssZ \t subject</c> (tabs and newlines of the subject become spaces).</summary>
    /// <exception cref="ArgumentException">The hash is not one <see cref="JournalHashing.Hash"/> writes.</exception>
    public static string Line(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!JournalHashing.IsHash(entry.AddressHash))
        {
            throw new ArgumentException("The entry has no valid address hash.", nameof(entry));
        }

        var time = entry.SentAt.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
        return entry.AddressHash + "\t" + time + "\t" + CleanSubject(entry.Subject);
    }

    private static JournalEntry? Parse(string line)
    {
        if (line.Length == 0 || line.Length > MaxLineLength)
        {
            return null;
        }

        var firstTab = line.IndexOf('\t', StringComparison.Ordinal);
        var secondTab = firstTab < 0 ? -1 : line.IndexOf('\t', firstTab + 1);
        if (secondTab < 0)
        {
            return null;
        }

        var hash = line[..firstTab];
        if (!JournalHashing.IsHash(hash)
            || !DateTimeOffset.TryParseExact(line[(firstTab + 1)..secondTab], TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var sentAt))
        {
            return null;
        }

        return new JournalEntry(hash, sentAt, CleanSubject(line[(secondTab + 1)..]));
    }

    /// <summary>Control characters (tabs and line breaks included) → spaces, trimmed, at most <see cref="MaxSubjectLength"/>
    /// characters (a surrogate pair is never cut).</summary>
    private static string CleanSubject(string? subject)
    {
        if (string.IsNullOrEmpty(subject))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(subject.Length, MaxSubjectLength + 1));
        foreach (var c in subject)
        {
            builder.Append(char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c);
        }

        var clean = builder.ToString().Trim();
        if (clean.Length > MaxSubjectLength)
        {
            clean = clean[..MaxSubjectLength];
            if (char.IsHighSurrogate(clean[^1]))
            {
                clean = clean[..^1];
            }

            clean = clean.TrimEnd();
        }

        return clean;
    }
}
