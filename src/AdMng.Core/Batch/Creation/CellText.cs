using System.Globalization;
using System.Text;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;

namespace AdMng.Core.Batch.Creation;

/// <summary>
/// Cell-level text rules of the wizard (docs/dev/phase3-bulk-create.md 2.1, owner B1): normalisation, Excel error values
/// and scientific notation, content tests (e-mail, phone, Polish mobile, all caps), the operator's case and phone
/// choices, splitting of multi-valued cells, group lists and full names. Pure and thread-safe.
/// </summary>
public static class CellText
{
    /// <summary>The casing culture of <see cref="ApplyCase"/> (<em>Jak W Nazwie</em> is a Polish rule).</summary>
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>
    /// Excel's error values in Polish and English (2.1), plus the newer ones (<c>#ROZLANIE!</c> / <c>#SPILL!</c>, the
    /// data-type and connection errors); compared case-insensitively on the trimmed cell.
    /// </summary>
    private static readonly HashSet<string> ErrorValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "#N/D!", "#N/D", "#ARG!", "#NAZWA?", "#DZIEL/0!", "#ADR!", "#LICZBA!", "#ZERO!", "#PUSTE!", "#ROZLANIE!",
        "#N/A", "#VALUE!", "#REF!", "#NAME?", "#DIV/0!", "#NUM!", "#NULL!", "#SPILL!", "#CALC!", "#GETTING_DATA",
        "#BLOCKED!", "#CONNECT!", "#FIELD!", "#UNKNOWN!", "#BUSY!", "#EXTERNAL!",
    };

    /// <summary>The first two digits of Polish mobile numbers (UKE numbering plan, 2.1).</summary>
    private static readonly HashSet<string> PolishMobilePrefixes = new(StringComparer.Ordinal)
    {
        "45", "50", "51", "53", "57", "60", "66", "69", "72", "73", "78", "79", "88",
    };

    /// <summary>
    /// BOM and zero-width characters removed; NBSP, U+2007, U+202F (every Unicode space separator) → space; runs of spaces
    /// → one; trimmed (also every line of a multi-line cell); CR, LF and CRLF (and U+2028 / U+2029) inside the cell → CRLF.
    /// </summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (IsClean(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (IsInvisible(c))
            {
                continue;
            }

            if (c is '\r' or '\n' or '\u2028' or '\u2029')
            {
                if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
                {
                    i++;
                }

                // Spaces before a line break are dropped; so are those after it (below).
                pendingSpace = false;
                builder.Append("\r\n");
                continue;
            }

            if (IsSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0 && builder[^1] != '\n')
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    /// <summary>An Excel error value in Polish or English (<c>#N/D!</c>, <c>#ARG!</c> … <c>#N/A</c>, <c>#VALUE!</c> …).</summary>
    public static bool IsSpreadsheetError(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        return trimmed.Length > 1 && trimmed[0] == '#' && ErrorValues.Contains(trimmed);
    }

    /// <summary>Excel's display of a long number: <c>^[+-]?\d([.,]\d+)?E[+-]?\d+$</c>, case-insensitive (<c>4,86E+11</c>).</summary>
    public static bool IsScientificNotation(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IsScientific(value.AsSpan().Trim());
    }

    /// <summary>The cell looks like an e-mail address (header detection and content suggestions): one mailbox with a dotted domain.</summary>
    public static bool LooksLikeEmail(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!UserMessageRules.TryParseAddress(value, out var address))
        {
            return false;
        }

        var at = address.LastIndexOf('@');
        var dot = address.IndexOf('.', at + 1);
        return dot > at + 1 && dot < address.Length - 1;
    }

    /// <summary>
    /// The cell looks like a phone number: 7 to 15 digits and only phone punctuation (spaces, <c>- . ( ) /</c>, a leading
    /// <c>+</c>); a date such as <c>2026-11-09</c> or <c>09.11.2026</c> is not a phone number.
    /// </summary>
    public static bool LooksLikePhone(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.AsSpan().Trim();
        if (text.Length == 0 || LooksLikeDate(text))
        {
            return false;
        }

        var digits = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c))
            {
                digits++;
            }
            else if (c == '+' ? i != 0 : c is not (' ' or '-' or '.' or '(' or ')' or '/'))
            {
                return false;
            }
        }

        return digits is >= 7 and <= 15;
    }

    /// <summary>
    /// A Polish mobile number: 9 digits, or <c>48</c> / <c>+48</c> / <c>0048</c> + 9 digits, whose first two digits are
    /// 45, 50, 51, 53, 57, 60, 66, 69, 72, 73, 78, 79 or 88.
    /// </summary>
    public static bool LooksLikePolishMobile(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return PolishNationalNumber(value) is { } national && PolishMobilePrefixes.Contains(national[..2]);
    }

    /// <summary>Letters only upper case, at least two letters (<em>KOWALSKI</em>).</summary>
    public static bool IsAllCaps(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var letters = 0;
        foreach (var c in value)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            if (!char.IsUpper(c))
            {
                return false;
            }

            letters++;
        }

        return letters >= 2;
    }

    /// <summary>Applies the operator's case choice (<see cref="TextCase.Title"/>: <em>NOWAK-KOWALSKA</em> → <em>Nowak-Kowalska</em>).</summary>
    public static string ApplyCase(string value, TextCase textCase)
    {
        ArgumentNullException.ThrowIfNull(value);
        return textCase switch
        {
            TextCase.Lower => value.ToLower(Polish),
            TextCase.Upper => value.ToUpper(Polish),
            TextCase.Title => ToTitleCase(value),
            _ => value,
        };
    }

    /// <summary>The values of a multi-valued attribute cell: split on <c>|</c> only (a line break stays inside a value), trimmed, empty parts dropped.</summary>
    public static IReadOnlyList<string> SplitValues(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Split(value, ['|']);
    }

    /// <summary>The parts of a <em>Grupy</em> cell: split on <c>;</c>, <c>|</c> and line breaks, trimmed, empty parts dropped.</summary>
    public static IReadOnlyList<string> SplitList(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Split(value, [';', '|', '\r', '\n']);
    }

    /// <summary>The comma-separated parts of a group cell (the fallback of 2.5), trimmed, empty parts dropped.</summary>
    public static IReadOnlyList<string> SplitCommas(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Split(value, [',']);
    }

    /// <summary>
    /// Splits a full name after removing titles (<c>TitleParser.WithoutTitles</c>): given-name-first – the last token is the
    /// surname; surname-first – the first token is the surname; one token → surname only. A hyphenated surname is one token
    /// (<em>dr hab. Anna Maria Nowak-Kowalska</em> → <em>Anna Maria</em> / <em>Nowak-Kowalska</em>); a comma after the
    /// surname (<em>Kowalski, Jan</em>) is dropped.
    /// </summary>
    public static (string GivenName, string Surname) SplitFullName(string value, NameOrder order)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = Normalize(value).Replace("\r\n", " ", StringComparison.Ordinal);
        if (text.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        var withoutTitles = TitleParser.WithoutTitles(text, TitleTable.Default) is { Length: > 0 } stripped ? stripped : text;
        var tokens = withoutTitles
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim(',', ';'))
            .Where(t => t.Length > 0)
            .ToList();
        switch (tokens.Count)
        {
            case 0:
                return (string.Empty, string.Empty);
            case 1:
                return (string.Empty, tokens[0]);
        }

        return order == NameOrder.SurnameFirst
            ? (string.Join(' ', tokens.Skip(1)), tokens[0])
            : (string.Join(' ', tokens.Take(tokens.Count - 1)), tokens[^1]);
    }

    /// <summary>
    /// <see cref="PhoneFormat.International"/>: spaces, dashes, dots and parentheses removed, <c>00</c> → <c>+</c>, 9 digits →
    /// <c>+48</c>, 11 digits starting <c>48</c> → <c>+48</c>; a Polish number is written <c>+48 600 100 200</c> (mobile) or
    /// <c>+48 22 555 01 01</c> (fixed line, area code first), other countries <c>+</c> and the digits. Text with letters, fewer
    /// than 7 digits or no recognisable country is returned unchanged. <see cref="PhoneFormat.AsIs"/> returns the value.
    /// </summary>
    public static string NormalizePhone(string value, PhoneFormat format)
    {
        ArgumentNullException.ThrowIfNull(value);
        return format == PhoneFormat.International && TryFormatInternational(value, out var formatted) ? formatted : value;
    }

    /// <summary>The international form of <see cref="NormalizePhone"/>; false when the value cannot be formatted (the planner warns, 2.2).</summary>
    internal static bool TryFormatInternational(string value, out string formatted)
    {
        formatted = value;
        var compact = CompactPhone(value);
        if (compact is null)
        {
            return false;
        }

        string international;
        if (compact.StartsWith('+'))
        {
            international = compact[1..];
        }
        else if (compact.StartsWith("00", StringComparison.Ordinal))
        {
            international = compact[2..];
        }
        else if (compact.Length == 9)
        {
            international = "48" + compact;
        }
        else if (compact.Length == 11 && compact.StartsWith("48", StringComparison.Ordinal))
        {
            international = compact;
        }
        else
        {
            return false;
        }

        if (international.Length is < 7 or > 15 || international[0] == '0')
        {
            return false;
        }

        if (international.Length == 11 && international.StartsWith("48", StringComparison.Ordinal))
        {
            var national = international[2..];
            formatted = PolishMobilePrefixes.Contains(national[..2])
                ? $"+48 {national[..3]} {national[3..6]} {national[6..]}"
                : $"+48 {national[..2]} {national[2..5]} {national[5..7]} {national[7..]}";
            return true;
        }

        formatted = "+" + international;
        return true;
    }

    /// <summary>The 9-digit national part of a Polish number (9 digits, or 48 / +48 / 0048 + 9 digits), or null.</summary>
    internal static string? PolishNationalNumber(string value)
    {
        var compact = CompactPhone(value);
        return compact switch
        {
            { Length: 9 } when compact[0] != '+' => compact,
            { Length: 11 } when compact.StartsWith("48", StringComparison.Ordinal) => compact[2..],
            { Length: 12 } when compact.StartsWith("+48", StringComparison.Ordinal) => compact[3..],
            { Length: 13 } when compact.StartsWith("0048", StringComparison.Ordinal) => compact[4..],
            _ => null,
        };
    }

    /// <summary>A phone written as <c>48xxxxxxxxx</c> (11 digits, no <c>+</c>, no separators) – the hint of <see cref="SuggestionNote.PhonesWithoutPlus"/>.</summary>
    internal static bool IsPolishNumberWithoutPlus(string value)
    {
        var text = value.AsSpan().Trim();
        if (text.Length != 11 || !text.StartsWith("48", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An ISO date (<c>2026-11-09</c>, optionally with a time) or a Polish one (<c>9.11.2026</c>, <c>09-11-26</c>).</summary>
    internal static bool LooksLikeDate(ReadOnlySpan<char> text)
    {
        var parts = 0;
        var lengths = new int[3];
        var separator = '\0';
        var i = 0;
        for (; i < text.Length && parts < 3; i++)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c))
            {
                lengths[parts]++;
                continue;
            }

            if (c is '-' or '.' or '/' && parts < 2 && lengths[parts] > 0 && (separator == '\0' || separator == c))
            {
                separator = c;
                parts++;
                continue;
            }

            break;
        }

        if (parts != 2 || lengths[2] == 0)
        {
            return false;
        }

        // The rest may be a time (ISO "T10:00" or " 10:00"); anything else is not a date.
        if (i < text.Length && text[i] is not (' ' or 'T') && !(i == text.Length - 1 && text[i] == '.'))
        {
            return false;
        }

        return (lengths[0] == 4 && lengths[1] is 1 or 2 && lengths[2] is 1 or 2)
            || (lengths[0] is 1 or 2 && lengths[1] is 1 or 2 && lengths[2] is 2 or 4);
    }

    /// <summary>A number as a cell shows it: digits with a sign, spaces, separators or scientific notation (<c>12 345</c>, <c>3,5</c>).</summary>
    internal static bool LooksLikeNumber(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (IsScientific(text))
        {
            return true;
        }

        var digits = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c))
            {
                digits++;
            }
            else if (!(c is '+' or '-' && i == 0) && c is not (' ' or '.' or ',' or '%'))
            {
                return false;
            }
        }

        return digits > 0;
    }

    /// <summary>The scientific-notation test of <see cref="IsScientificNotation"/> on a trimmed span (no allocation).</summary>
    internal static bool IsScientific(ReadOnlySpan<char> text)
    {
        var i = 0;
        if (i < text.Length && text[i] is '+' or '-')
        {
            i++;
        }

        if (i >= text.Length || !char.IsAsciiDigit(text[i]))
        {
            return false;
        }

        i++;
        if (i < text.Length && text[i] is '.' or ',')
        {
            i++;
            var fraction = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }

            if (i == fraction)
            {
                return false;
            }
        }

        if (i >= text.Length || text[i] is not ('E' or 'e'))
        {
            return false;
        }

        i++;
        if (i < text.Length && text[i] is '+' or '-')
        {
            i++;
        }

        var exponent = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        return i > exponent && i == text.Length;
    }

    /// <summary>BOM and the zero-width characters <see cref="Normalize"/> removes.</summary>
    internal static bool IsInvisible(char c) => c is '\uFEFF' or '\u200B' or '\u200C' or '\u200D' or '\u2060';

    /// <summary>A space or any other Unicode space separator (NBSP, U+2007, U+202F, U+3000 …).</summary>
    private static bool IsSpace(char c) => c == ' ' || (c > '~' && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator);

    /// <summary>Nothing for <see cref="Normalize"/> to do (the common case: an ASCII or Polish word without odd spacing).</summary>
    private static bool IsClean(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (value[0] == ' ' || value[^1] == ' ')
        {
            return false;
        }

        var previousSpace = false;
        foreach (var c in value)
        {
            if (c == ' ')
            {
                if (previousSpace)
                {
                    return false;
                }

                previousSpace = true;
                continue;
            }

            previousSpace = false;
            if (c < ' ' || c is '\u2028' or '\u2029' || IsInvisible(c) || IsSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string ToTitleCase(string value)
    {
        var builder = new StringBuilder(value.Length);
        var startOfWord = true;
        foreach (var c in value)
        {
            if (char.IsLetter(c))
            {
                builder.Append(startOfWord ? char.ToUpper(c, Polish) : char.ToLower(c, Polish));
                startOfWord = false;
                continue;
            }

            builder.Append(c);

            // White space, '-', the apostrophe (O'Neill) and '.' start a new part; a digit does not (3d).
            startOfWord = !char.IsDigit(c);
        }

        return builder.ToString();
    }

    private static List<string> Split(string value, char[] separators) =>
        [.. value.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => p.Length > 0)];

    /// <summary>The phone without spaces, dashes, dots and parentheses (a leading <c>+</c> kept), or null when anything else is in it.</summary>
    private static string? CompactPhone(string value)
    {
        var text = value.AsSpan().Trim();
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c))
            {
                builder.Append(c);
            }
            else if (c == '+' && builder.Length == 0 && i == 0)
            {
                builder.Append(c);
            }
            else if (c is not (' ' or '-' or '.' or '(' or ')' or '\u00A0' or '\u202F'))
            {
                return null;
            }
        }

        var digits = builder.Length - (builder.Length > 0 && builder[0] == '+' ? 1 : 0);
        return digits < 7 ? null : builder.ToString();
    }
}
