using System.Text;
using System.Text.RegularExpressions;

namespace AdMng.Core.Correspondence.Import;

/// <summary>E-mail addresses and names in imported text (MailerWPiA's rules, design note 3.1).</summary>
public static partial class EmailAddressText
{
    /// <summary>Longest name kept by <see cref="CleanName"/>.</summary>
    public const int MaxNameLength = 120;

    private const int MaxLocalPartLength = 64;
    private const int MaxAddressLength = 254;

    /// <summary>Characters <see cref="Normalize"/> trims from both ends (MailerWPiA's list plus brackets and white space).</summary>
    private static readonly char[] AddressTrim = ['.', ',', ';', ':', '<', '>', '(', ')', '[', ']', '"', '\'', ' ', '\t', '\r', '\n', '\u00A0'];

    /// <summary>Characters <see cref="CleanName"/> trims from both ends: bullets, dashes, quotes (straight and typographic).</summary>
    private static readonly char[] NameTrim =
    [
        '-', '•', '*', '·', '–', '—', '‣', '◦', '▪', ' ', '\t', '"', '\'', '„', '”', '“', '‘', '’', '«', '»', ',', ';',
    ];

    /// <summary>MailerWPiA's validity check, made strict where Outlook would refuse the address anyway: no empty or dotted
    /// edges in the local part, no ".." anywhere, domain labels that do not start or end with '-', a top-level domain of
    /// 2–63 letters, not junk (example.*, sentry.io, schemas.*, w3.org).</summary>
    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var value = address.Trim();
        if (value.Length > MaxAddressLength || !ValidPattern().IsMatch(value) || value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);
        var local = value[..at];
        var domain = value[(at + 1)..];
        if (local.Length > MaxLocalPartLength || local.StartsWith('.') || local.EndsWith('.'))
        {
            return false;
        }

        foreach (var label in domain.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63 || label.StartsWith('-') || label.EndsWith('-'))
            {
                return false;
            }
        }

        return !IsJunk(domain.ToLowerInvariant());
    }

    /// <summary>Trims "mailto:", "?…", punctuation, brackets and quotes; lower-invariant ("" for null or blank).</summary>
    public static string Normalize(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var value = address.Trim();
        if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[7..];
        }

        var query = value.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            value = value[..query];
        }

        return value.Trim(AddressTrim).ToLowerInvariant();
    }

    /// <summary>Every address of <paramref name="text"/> in order: normalized, valid, de-duplicated.</summary>
    public static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('@', StringComparison.Ordinal))
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in AnyPattern().Matches(text))
        {
            var address = Normalize(match.Value);
            if (IsValid(address) && seen.Add(address))
            {
                result.Add(address);
            }
        }

        return result;
    }

    /// <summary>
    /// Collapses whitespace, turns "Kontakt: X" into "X", trims bullets, dashes and quotes; "" when the name is an address;
    /// at most 120 characters.
    /// </summary>
    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var value = CollapseWhiteSpace(name);
        var colon = value.LastIndexOf(':');
        if (colon >= 0 && colon < value.Length - 1)
        {
            value = value[(colon + 1)..].Trim();
        }

        value = value.Trim(NameTrim);
        if (value.Contains('@', StringComparison.Ordinal) && AnyPattern().IsMatch(value))
        {
            return string.Empty;
        }

        if (value.Length > MaxNameLength)
        {
            value = value[..MaxNameLength];
            if (char.IsHighSurrogate(value[^1]))
            {
                value = value[..^1];
            }

            value = value.TrimEnd();
        }

        return value;
    }

    /// <summary>The first valid address of a cell such as "a@x.pl; b@y.pl", or null.</summary>
    public static string? FirstValid(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell) || !cell.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }

        foreach (Match match in AnyPattern().Matches(cell))
        {
            var address = Normalize(match.Value);
            if (IsValid(address))
            {
                return address;
            }
        }

        return null;
    }

    /// <summary>MailerWPiA's <c>Any</c> pattern: an address anywhere in text.</summary>
    internal static MatchCollection Matches(string text) => AnyPattern().Matches(text);

    /// <summary>MailerWPiA's <c>Named</c> pattern: <c>Name &lt;addr&gt;</c> / <c>Name (addr)</c> (groups 1 and 2).</summary>
    internal static MatchCollection NamedMatches(string text) => NamedPattern().Matches(text);

    private static bool IsJunk(string domain) =>
        domain.StartsWith("example.", StringComparison.Ordinal)
        || domain.Contains(".example.", StringComparison.Ordinal)
        || domain == "sentry.io" || domain.EndsWith(".sentry.io", StringComparison.Ordinal)
        || domain.StartsWith("schemas.", StringComparison.Ordinal)
        || domain == "w3.org" || domain.EndsWith(".w3.org", StringComparison.Ordinal);

    private static string CollapseWhiteSpace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>MailerWPiA's pattern, not starting inside a word ("Łucja.Żak@…" is no "ak@…").</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}._%+\-])[A-Za-z0-9._%+\-]+@[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,63}", RegexOptions.CultureInvariant)]
    private static partial Regex AnyPattern();

    [GeneratedRegex("\"?([^\"<>()\\r\\n;,\\t]{2,80}?)\"?\\s*[<(]\\s*(?:mailto:)?([A-Za-z0-9._%+\\-]+@[A-Za-z0-9.\\-]+\\.[A-Za-z]{2,63})\\s*[>)]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NamedPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPattern();
}
