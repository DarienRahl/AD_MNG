using System.Net;
using System.Text;

namespace AdMng.Core.Correspondence.Fields;

/// <summary>One <c>{{Pole}}</c> or <c>{{Pole|tekst gdy puste}}</c> of a template.</summary>
/// <param name="Field">The field name as written (trimmed; HTML character references decoded, U+00A0 read as a space).</param>
/// <param name="Fallback">The text rendered when the value is empty or unknown (trimmed, decoded like the name), or null.</param>
/// <param name="Start">Index of the opening <c>{{</c>.</param>
/// <param name="Length">Length of the token including the braces.</param>
public sealed record FieldToken(string Field, string? Fallback, int Start, int Length);

/// <summary>How merged values are encoded.</summary>
public enum FieldEncoding
{
    /// <summary>As is.</summary>
    Text,

    /// <summary>HTML-encoded, line breaks become <c>&lt;br&gt;</c>.</summary>
    Html,

    /// <summary>A header (subject): control characters become spaces.</summary>
    Header,
}

/// <summary>
/// The field syntax of Korespondencja (MailerWPiA's <c>{{Pole}}</c>, decision D1, design note 3.2): any column name,
/// spaces and diacritics allowed, plus <c>{{Pole|tekst gdy puste}}</c>; no nesting; "{{" without "}}" is literal.
/// </summary>
/// <remarks>
/// A token is <c>{{</c>, a field name of 1–<see cref="MaxFieldNameLength"/> characters, optionally <c>|</c> and a fallback of
/// at most <see cref="MaxFallbackLength"/> characters, then <c>}}</c>; the content holds no brace and no line break
/// (anything else is literal text, so a value can never open a field). Names are compared with <see cref="FieldKey"/>.
/// Because the body is HTML, character references in a token are decoded (<c>{{R&amp;amp;D}}</c> names the column
/// "R&amp;D"); decoding plain text leaves it unchanged.
/// </remarks>
public static class FieldTemplate
{
    /// <summary>The longest field name of a token.</summary>
    public const int MaxFieldNameLength = 80;

    /// <summary>The longest fallback text of a token.</summary>
    public const int MaxFallbackLength = 200;

    /// <summary>The longest content between the braces that is looked at (name, pipe, fallback and white space).</summary>
    private const int MaxContentLength = MaxFieldNameLength + MaxFallbackLength + 40;

    /// <summary>The tokens of <paramref name="template"/>: <c>{{ Pole }}</c> or <c>{{ Pole | tekst }}</c>, in order.</summary>
    public static IReadOnlyList<FieldToken> Parse(string? template)
    {
        var tokens = new List<FieldToken>();
        if (string.IsNullOrEmpty(template))
        {
            return tokens;
        }

        var i = 0;
        while (true)
        {
            var open = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            if (TryToken(template, open, out var token))
            {
                tokens.Add(token);
                i = open + token.Length;
            }
            else
            {
                i = open + 1;
            }
        }

        return tokens;
    }

    /// <summary>The fields used, distinct by <see cref="FieldKey"/>, in the first spelling.</summary>
    public static IReadOnlyList<string> UsedFields(string? template)
    {
        var result = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Parse(template))
        {
            if (keys.Add(FieldKey.Of(token.Field)))
            {
                result.Add(token.Field);
            }
        }

        return result;
    }

    /// <summary>
    /// Joins tokens split by tags or <c>&amp;nbsp;</c> (MailerWPiA's RepairTokens); never inside attribute values.
    /// </summary>
    /// <remarks>Within one block (never across <c>&lt;br&gt;</c>, a block element, a comment or a raw-text element) the
    /// text is read without its inline tags, <c>&amp;nbsp;</c> and U+00A0 as spaces, soft hyphens and zero-width characters
    /// removed; every valid token that spans several text pieces or contains such characters is written whole into the
    /// piece holding most of its name (that formatting wins), the inline tags in between kept in their order. Tags are
    /// atomic: a <c>{{</c> in an attribute value is never touched or joined. Other text is unchanged.</remarks>
    public static string RepairHtml(string? html) => string.IsNullOrEmpty(html) ? string.Empty : TokenRepair.Repair(html);

    /// <summary>
    /// Replaces every token with its value. <paramref name="lookup"/> returns null for an unknown field, "" for a known
    /// empty one; unknown and empty field names are added to <paramref name="unknown"/> / <paramref name="empty"/>.
    /// </summary>
    /// <remarks>A token with a fallback renders the fallback when the value is empty (white space only) or unknown; an
    /// empty value with a fallback is not reported as empty, an unknown field always is reported as unknown. Each name is
    /// reported once (by <see cref="FieldKey"/>). Values and fallbacks are encoded with <paramref name="encoding"/>; the
    /// template's own text is never changed.</remarks>
    public static string Merge(string? template, Func<string, string?> lookup, FieldEncoding encoding,
        ICollection<string>? unknown = null, ICollection<string>? empty = null) =>
        MergeCore(template, lookup, encoding, unknown, empty, raw: null);

    /// <summary><see cref="Merge"/> where the values of fields for which <paramref name="raw"/> is true are inserted without
    /// encoding (the renderer's <c>{{lista}}</c>, already HTML).</summary>
    internal static string MergeCore(string? template, Func<string, string?> lookup, FieldEncoding encoding,
        ICollection<string>? unknown, ICollection<string>? empty, Func<string, bool>? raw)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var tokens = Parse(template);
        if (tokens.Count == 0)
        {
            return template;
        }

        var builder = new StringBuilder(template.Length + 64);
        var position = 0;
        foreach (var token in tokens)
        {
            builder.Append(template, position, token.Start - position);
            position = token.Start + token.Length;
            var value = lookup(token.Field);
            if (value is null)
            {
                Report(unknown, token.Field);
                builder.Append(Encode(token.Fallback ?? string.Empty, encoding));
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                if (token.Fallback is not null)
                {
                    builder.Append(Encode(token.Fallback, encoding));
                    continue;
                }

                Report(empty, token.Field);
            }

            builder.Append(raw is not null && encoding == FieldEncoding.Html && raw(token.Field) ? value : Encode(value, encoding));
        }

        builder.Append(template, position, template.Length - position);
        return builder.ToString();
    }

    /// <summary>A value as it is written into a template of the given encoding.</summary>
    public static string Encode(string value, FieldEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(value);
        return encoding switch
        {
            FieldEncoding.Html => HtmlEncode(value),
            FieldEncoding.Header => HeaderSafe(value),
            _ => value,
        };
    }

    /// <summary>The token starting at <paramref name="open"/> (the index of "{{"), when it is a valid one.</summary>
    internal static bool TryToken(string text, int open, out FieldToken token)
    {
        token = null!;
        var limit = Math.Min(text.Length, open + 2 + MaxContentLength + 2);
        var close = -1;
        for (var j = open + 2; j + 1 < limit; j++)
        {
            var c = text[j];
            if (c is '{' or '\r' or '\n')
            {
                return false;
            }

            if (c == '}')
            {
                if (text[j + 1] != '}')
                {
                    return false;
                }

                close = j;
                break;
            }
        }

        if (close < 0)
        {
            return false;
        }

        var content = text[(open + 2)..close];
        var pipe = content.IndexOf('|', StringComparison.Ordinal);
        var field = Clean(pipe >= 0 ? content[..pipe] : content);
        var fallback = pipe >= 0 ? Clean(content[(pipe + 1)..]) : null;
        if (field.Length == 0 || field.Length > MaxFieldNameLength || fallback?.Length > MaxFallbackLength)
        {
            return false;
        }

        token = new FieldToken(field, fallback, open, close + 2 - open);
        return true;
    }

    private static string Clean(string value)
    {
        var decoded = value.Contains('&', StringComparison.Ordinal) ? WebUtility.HtmlDecode(value) : value;
        return decoded.Replace('\u00A0', ' ').Trim();
    }

    private static void Report(ICollection<string>? names, string field)
    {
        if (names is null)
        {
            return;
        }

        var key = FieldKey.Of(field);
        foreach (var name in names)
        {
            if (string.Equals(FieldKey.Of(name), key, StringComparison.Ordinal))
            {
                return;
            }
        }

        names.Add(field);
    }

    /// <summary>The five markup characters encoded (letters, Polish ones included, stay literal); line breaks →
    /// <c>&lt;br&gt;</c>.</summary>
    private static string HtmlEncode(string value)
    {
        var builder = new StringBuilder(value.Length + 16);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\'':
                    builder.Append("&#39;");
                    break;
                case '\r':
                    builder.Append("<br>");
                    if (i + 1 < value.Length && value[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;
                case '\n':
                    builder.Append("<br>");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string HeaderSafe(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c);
        }

        return builder.ToString();
    }
}
