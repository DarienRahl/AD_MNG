using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;
using AdMng.Core.Model;

namespace AdMng.Core.Correspondence.Mailboxes;

/// <summary>The name of the person a mailbox is assigned to, as found on the mailbox account.</summary>
/// <param name="GivenName">The given name(s).</param>
/// <param name="Surname">The surname.</param>
/// <param name="Text">"Given Surname" as found (titles removed).</param>
public sealed record OwnerName(string GivenName, string Surname, string Text)
{
    /// <summary>Neither a given name nor a surname.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(GivenName) && string.IsNullOrWhiteSpace(Surname);

    public override string ToString() => nameof(OwnerName);
}

/// <summary>Reads the owner's name from a mailbox account (design note 3.7).</summary>
public static class OwnerNameParser
{
    /// <summary>The longest text read (longer descriptions are cut at a word boundary before parsing).</summary>
    public const int MaxTextLength = 256;

    /// <summary>Separators of a prefix such as "Skrzynka: " or "Projekt UE – " (hyphen, en dash, em dash with spaces).</summary>
    private static readonly string[] DashSeparators = [" - ", " \u2013 ", " \u2014 "];

    /// <summary>Quotes trimmed from the ends of words ("„Jan Kowalski”").</summary>
    private static readonly char[] Quotes = ['"', '\'', '\u201E', '\u201D', '\u201C', '\u201A', '\u2018', '\u2019', '\u00AB', '\u00BB', ';'];

    /// <summary>
    /// GivenNameAndSurname: givenName + sn of the mailbox (one of them may hold both words – then split);
    /// DisplayName/Description/Attribute: the text without titles (TitleParser.WithoutTitles with TitleTable.Default),
    /// without "(…)" remarks and after a ':' or ' - ' prefix ("Skrzynka: Jan Kowalski"); 2+ words → given = all but the
    /// last, surname = last; a single word → surname only.
    /// </summary>
    /// <param name="mailbox">The mailbox account, read with <see cref="AttributesFor"/>.</param>
    /// <param name="source">Where the name is.</param>
    /// <param name="attribute">The attribute of <see cref="OwnerNameSource.Attribute"/> (ignored otherwise).</param>
    /// <returns>The name, or null when the source holds none.</returns>
    public static OwnerName? Parse(DirectoryObject mailbox, OwnerNameSource source, string? attribute)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        return source switch
        {
            OwnerNameSource.GivenNameAndSurname => FromNames(mailbox.GetString(Attr.GivenName), mailbox.GetString(Attr.Sn)),
            OwnerNameSource.DisplayName => ParseText(mailbox.GetString(Attr.DisplayName)),
            OwnerNameSource.Description => ParseText(mailbox.GetString(Attr.Description)),
            OwnerNameSource.Attribute => IsUsableAttribute(attribute) ? ParseText(mailbox.GetString(attribute!.Trim())) : null,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
        };
    }

    /// <summary>
    /// The owner's name from a given name and a surname: both cleaned of "(…)" remarks and titles; when only one of them is
    /// set and it holds several words ("Jan Kowalski" in sn), it is parsed as a text. Null when both are empty.
    /// </summary>
    public static OwnerName? FromNames(string? givenName, string? surname)
    {
        var given = CleanPart(givenName);
        var family = CleanPart(surname);
        if (given.Length > 0 && family.Length > 0)
        {
            return new OwnerName(given, family, $"{given} {family}");
        }

        var single = given.Length > 0 ? given : family;
        if (single.Length == 0)
        {
            return null;
        }

        if (Words(single).Count > 1)
        {
            return ParseText(single);
        }

        // One word in one attribute keeps the meaning of that attribute.
        return given.Length > 0 ? new OwnerName(given, string.Empty, given) : new OwnerName(string.Empty, family, family);
    }

    /// <summary>
    /// The owner's name in a text (display name, description …): "(…)" remarks removed, the part after the last ':' or
    /// " - " / " – " ("Skrzynka: dr Jan Kowalski" → "dr Jan Kowalski"), titles removed ("Jan Kowalski"), words split on
    /// white space and commas; 2+ words → given = all but the last, surname = the last; one word → surname only. Null
    /// when nothing is left.
    /// </summary>
    public static OwnerName? ParseText(string? text)
    {
        var value = Clean(text);
        if (value.Length > MaxTextLength)
        {
            // Cut at a word boundary: a cut word would become the surname.
            var cut = value.LastIndexOf(' ', MaxTextLength);
            value = value[..(cut > 0 ? cut : MaxTextLength)];
        }

        value = WithoutRemarks(value);
        value = AfterPrefix(value);
        value = WithoutTitles(value);
        var words = Words(value);
        return words.Count switch
        {
            0 => null,
            1 => new OwnerName(string.Empty, words[0], words[0]),
            _ => new OwnerName(string.Join(' ', words.Take(words.Count - 1)), words[^1], string.Join(' ', words)),
        };
    }

    /// <summary>The attributes <see cref="Parse"/> reads for <paramref name="source"/>.</summary>
    public static IReadOnlyList<string> AttributesFor(OwnerNameSource source, string? attribute) => source switch
    {
        OwnerNameSource.GivenNameAndSurname => [Attr.GivenName, Attr.Sn],
        OwnerNameSource.DisplayName => [Attr.DisplayName],
        OwnerNameSource.Description => [Attr.Description],
        OwnerNameSource.Attribute => IsUsableAttribute(attribute) ? [attribute!.Trim()] : [],
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    /// <summary>
    /// An attribute the owner's name may be read from: an LDAP attribute name (a letter, then letters, digits and hyphens, at
    /// most 256 characters) that is no secret (<see cref="UserMessageRules.IsForbiddenAttribute"/>).
    /// </summary>
    public static bool IsUsableAttribute(string? attribute)
    {
        var name = attribute?.Trim();
        return !string.IsNullOrEmpty(name) && name.Length <= 256 && char.IsAsciiLetter(name[0])
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            && !UserMessageRules.IsForbiddenAttribute(name);
    }

    /// <summary>The text without "(…)" remarks (nested ones included, an unclosed "(" cuts the rest), white space collapsed.</summary>
    internal static string WithoutRemarks(string? text)
    {
        var value = Clean(text);
        if (value.IndexOfAny(['(', ')']) < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var depth = 0;
        foreach (var c in value)
        {
            if (c == '(')
            {
                depth++;
                builder.Append(' ');
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
                builder.Append(' ');
            }
            else if (depth == 0)
            {
                builder.Append(c);
            }
        }

        return Clean(builder.ToString());
    }

    /// <summary>Trimmed, Unicode composed, control characters as spaces, white space collapsed; "" for null.</summary>
    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The part after the last ':' and then after the last dash separator, when something follows it.</summary>
    private static string AfterPrefix(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon >= 0 && text[(colon + 1)..].Trim() is { Length: > 0 } afterColon)
        {
            text = afterColon;
        }
        else if (colon >= 0)
        {
            text = text[..colon].Trim();
        }

        var dash = DashSeparators.Select(s => (Separator: s, Index: text.LastIndexOf(s, StringComparison.Ordinal))).MaxBy(p => p.Index);
        if (dash.Index >= 0 && text[(dash.Index + dash.Separator.Length)..].Trim() is { Length: > 0 } afterDash)
        {
            text = afterDash;
        }

        return text;
    }

    /// <summary>The text without a title prefix or suffix ("dr hab. Ewa Zielińska, prof. UW" → "Ewa Zielińska").</summary>
    private static string WithoutTitles(string text) =>
        text.Length == 0 ? text : TitleParser.WithoutTitles(text, TitleTable.Default) ?? text;

    /// <summary>A given name or a surname: remarks and titles removed.</summary>
    private static string CleanPart(string? value) => WithoutTitles(WithoutRemarks(value));

    /// <summary>Words split on white space and commas, quotes trimmed from their ends.</summary>
    private static List<string> Words(string text) =>
    [
        .. text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(Quotes))
            .Where(w => w.Any(char.IsLetterOrDigit)),
    ];
}
