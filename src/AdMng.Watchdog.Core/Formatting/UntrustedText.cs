using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// Makes directory strings safe to show in notifications (section 14.1) – every AD-sourced string is untrusted:
/// (a) bidi controls, zero-width and every other Unicode format (Cf), control (Cc), line/paragraph separator and
/// unpaired surrogate character becomes a visible escape such as <c>&lt;U+202E&gt;</c> (multi-line values keep TAB and
/// line breaks, normalized to LF); (b) URL-like tokens are defanged so mail and chat clients never auto-link
/// attacker-controlled text: <c>https://evil.example/x</c> → <c>hxxps://evil[.]example/x</c> (<c>http</c> → <c>hxxp</c>,
/// <c>ftp</c> → <c>fxp</c>, other schemes keep their name with <c>[:]//</c>), and <c>www.</c> → <c>www[.]</c>. Plain
/// host names, UPNs and e-mail addresses stay readable. Encoding for the output format (HTML, JSON) happens afterwards.
/// </summary>
public static partial class UntrustedText
{
    /// <summary>Replaces an unsafe character.</summary>
    public static string Escape(int codePoint) => string.Create(CultureInfo.InvariantCulture, $"<U+{codePoint:X4}>");

    /// <summary>The safe form of <paramref name="value"/> (null → empty).</summary>
    /// <param name="value">A directory string.</param>
    /// <param name="multiLine">Keep TAB and line breaks (descriptions, notes).</param>
    public static string Sanitize(string? value, bool multiLine = false)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return Defang(EscapeControls(value, multiLine));
    }

    /// <summary>The safe single-line form for subjects and headers: every line break, TAB or other white space becomes
    /// a space, then <see cref="Sanitize"/>; runs of spaces collapsed, trimmed.</summary>
    public static string ForHeader(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var safe = Sanitize(string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsWhiteSpace(source[i]) ? ' ' : source[i];
            }
        }));
        var builder = new StringBuilder(safe.Length);
        var lastWasSpace = false;
        foreach (var c in safe)
        {
            var space = char.IsWhiteSpace(c) || char.IsControl(c);
            if (space && lastWasSpace)
            {
                continue;
            }

            builder.Append(space ? ' ' : c);
            lastWasSpace = space;
        }

        return builder.ToString().Trim();
    }

    /// <summary>Escapes format/control characters only (no defanging).</summary>
    public static string EscapeControls(string value, bool multiLine = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder? builder = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (multiLine && c == '\r')
            {
                builder ??= new StringBuilder(value, 0, i, value.Length + 16);
                if (i + 1 >= value.Length || value[i + 1] != '\n')
                {
                    builder.Append('\n');
                }

                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                var rune = new Rune(c, value[i + 1]);
                if (IsUnsafe(Rune.GetUnicodeCategory(rune), multiLine, rune.Value))
                {
                    builder ??= new StringBuilder(value, 0, i, value.Length + 16);
                    builder.Append(Escape(rune.Value));
                }
                else
                {
                    builder?.Append(c).Append(value[i + 1]);
                }

                i++;
                continue;
            }

            var unsafeChar = char.IsSurrogate(c) || IsUnsafe(char.GetUnicodeCategory(c), multiLine, c);
            if (unsafeChar)
            {
                builder ??= new StringBuilder(value, 0, i, value.Length + 16);
                builder.Append(Escape(c));
            }
            else
            {
                builder?.Append(c);
            }
        }

        return builder?.ToString() ?? value;
    }

    /// <summary>Defangs URL-like tokens only.</summary>
    public static string Defang(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains("://", StringComparison.Ordinal))
        {
            value = SchemeUrl().Replace(value, DefangUrl);
        }

        return value.Contains("www.", StringComparison.OrdinalIgnoreCase) ? WwwToken().Replace(value, m => m.Groups[1].Value + "[.]") : value;
    }

    private static bool IsUnsafe(UnicodeCategory category, bool multiLine, int codePoint)
    {
        if (multiLine && codePoint is '\t' or '\n')
        {
            return false;
        }

        return category is UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned;
    }

    private static string DefangUrl(Match match)
    {
        var scheme = match.Groups["scheme"].Value.ToLowerInvariant();
        var replacement = scheme switch
        {
            "http" or "hxxp" => "hxxp://",
            "https" or "hxxps" => "hxxps://",
            "ftp" or "fxp" => "fxp://",
            "ftps" or "fxps" => "fxps://",
            _ => match.Groups["scheme"].Value + "[:]//",
        };

        // Idempotent: dots already defanged ("[.]") stay as they are.
        return replacement + BareDot().Replace(match.Groups["host"].Value, "[.]");
    }

    [GeneratedRegex(@"(?<!\[)\.(?!\])", RegexOptions.CultureInvariant)]
    private static partial Regex BareDot();

    [GeneratedRegex(@"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]{0,31})://(?<host>[^/?#\s\\<>""']*)", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeUrl();

    [GeneratedRegex(@"(?<![A-Za-z0-9.\-])(www)\.", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WwwToken();
}
