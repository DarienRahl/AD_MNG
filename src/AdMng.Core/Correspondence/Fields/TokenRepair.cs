using System.Text;
using AdMng.Core.Messaging.Html;

namespace AdMng.Core.Correspondence.Fields;

/// <summary>
/// <see cref="FieldTemplate.RepairHtml"/>: joins <c>{{fields}}</c> that an editor split with inline tags or non-breaking
/// spaces. Works on the HTML text with a small lexer (tags with quoted attribute values, comments, raw-text elements are
/// atomic), so nothing outside the repaired tokens changes.
/// </summary>
internal static class TokenRepair
{
    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title", "xmp", "iframe", "noembed", "noframes", "noscript", "plaintext",
    };

    private enum PieceKind
    {
        Text,
        InlineTag,
        Boundary,
    }

    private readonly record struct Piece(PieceKind Kind, int Start, int End);

    /// <summary>One character of the text without tags: its source range and the piece it belongs to.</summary>
    private readonly record struct Logical(char Value, int Start, int End, int Piece);

    public static string Repair(string html)
    {
        if (!html.Contains('{', StringComparison.Ordinal))
        {
            return html;
        }

        var pieces = Lex(html);
        var output = new StringBuilder(html.Length);
        var copied = 0;
        var segment = new List<int>();
        for (var p = 0; p <= pieces.Count; p++)
        {
            if (p < pieces.Count && pieces[p].Kind != PieceKind.Boundary)
            {
                segment.Add(p);
                continue;
            }

            copied = RepairSegment(html, pieces, segment, output, copied);
            segment.Clear();
        }

        output.Append(html, copied, html.Length - copied);
        return output.ToString();
    }

    /// <summary>Repairs the tokens of one run of text and inline tags; returns the source index copied up to.</summary>
    private static int RepairSegment(string html, List<Piece> pieces, List<int> segment, StringBuilder output, int copied)
    {
        if (segment.Count == 0)
        {
            return copied;
        }

        var logical = new List<Logical>();
        foreach (var p in segment)
        {
            if (pieces[p].Kind == PieceKind.Text)
            {
                Decode(html, pieces[p], p, logical);
            }
        }

        if (logical.Count < 5)
        {
            return copied;
        }

        var text = new string([.. logical.Select(l => l.Value)]);
        var i = 0;
        while (true)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            if (!FieldTemplate.TryToken(text, open, out var token))
            {
                i = open + 1;
                continue;
            }

            i = open + token.Length;
            var first = logical[open];
            var last = logical[open + token.Length - 1];
            var sourceStart = first.Start;
            var sourceEnd = last.End;
            var source = html[sourceStart..sourceEnd];
            var tokenText = text.Substring(open, token.Length);
            if (string.Equals(source, tokenText, StringComparison.Ordinal))
            {
                continue;   // one text piece, nothing to join
            }

            // The token goes into the text piece holding most of its name (ties: the first), the tags stay in order.
            var nameCounts = new Dictionary<int, int>();
            for (var k = open + 2; k < open + token.Length - 2; k++)
            {
                nameCounts[logical[k].Piece] = nameCounts.GetValueOrDefault(logical[k].Piece) + 1;
            }

            var keeper = nameCounts.Count == 0 ? first.Piece : nameCounts.OrderByDescending(e => e.Value).ThenBy(e => e.Key).First().Key;
            output.Append(html, copied, sourceStart - copied);
            var written = false;
            for (var p = first.Piece; p <= last.Piece; p++)
            {
                var piece = pieces[p];
                if (piece.Kind == PieceKind.InlineTag)
                {
                    output.Append(html, piece.Start, piece.End - piece.Start);
                }
                else if (p == keeper && !written)
                {
                    output.Append(tokenText);
                    written = true;
                }
            }

            copied = sourceEnd;
        }

        return copied;
    }

    /// <summary>The logical characters of a text piece: <c>&amp;nbsp;</c>, <c>&amp;#160;</c>, <c>&amp;#xA0;</c>, U+00A0 and
    /// white space → space; soft hyphens and zero-width characters dropped; other references kept as written.</summary>
    private static void Decode(string html, Piece piece, int index, List<Logical> logical)
    {
        var i = piece.Start;
        while (i < piece.End)
        {
            var c = html[i];
            if (c == '&' && NbspLength(html, i, piece.End) is var length and > 0)
            {
                logical.Add(new Logical(' ', i, i + length, index));
                i += length;
                continue;
            }

            if (c is '\u00AD' or '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF')
            {
                // Invisible: belongs to the neighbouring logical character's range (dropped when a token is rewritten).
                if (logical.Count > 0 && logical[^1].Piece == index && logical[^1].End == i)
                {
                    logical[^1] = logical[^1] with { End = i + 1 };
                }

                i++;
                continue;
            }

            logical.Add(new Logical(char.IsWhiteSpace(c) ? ' ' : c, i, i + 1, index));
            i++;
        }
    }

    private static int NbspLength(string html, int at, int end)
    {
        foreach (var entity in (ReadOnlySpan<string>)["&nbsp;", "&#160;", "&#xa0;", "&#xA0;", "&#Xa0;", "&#XA0;"])
        {
            if (at + entity.Length <= end && string.CompareOrdinal(html, at, entity, 0, entity.Length) == 0)
            {
                return entity.Length;
            }
        }

        return 0;
    }

    /// <summary>Splits the HTML into text, inline tags and boundaries (block and <c>br</c> tags, comments, raw-text
    /// elements with their content, declarations).</summary>
    private static List<Piece> Lex(string html)
    {
        var pieces = new List<Piece>();
        var i = 0;
        var textStart = 0;
        while (i < html.Length)
        {
            if (html[i] != '<' || i + 1 >= html.Length || !(char.IsAsciiLetter(html[i + 1]) || html[i + 1] is '/' or '!' or '?'))
            {
                i++;
                continue;
            }

            if (i > textStart)
            {
                pieces.Add(new Piece(PieceKind.Text, textStart, i));
            }

            int end;
            var kind = PieceKind.Boundary;
            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                end = close < 0 ? html.Length : close + 3;
            }
            else if (html[i + 1] is '!' or '?')
            {
                var close = html.IndexOf('>', i + 2);
                end = close < 0 ? html.Length : close + 1;
            }
            else
            {
                end = TagEnd(html, i);
                var name = TagName(html, i, out var closing);
                if (!closing && RawTextElements.Contains(name))
                {
                    var closeTag = html.IndexOf("</" + name, end, StringComparison.OrdinalIgnoreCase);
                    end = closeTag < 0 ? html.Length : TagEnd(html, closeTag);
                }
                else if (!HtmlPolicy.BlockElements.Contains(name.ToLowerInvariant()) && !name.Equals("br", StringComparison.OrdinalIgnoreCase))
                {
                    kind = PieceKind.InlineTag;
                }
            }

            pieces.Add(new Piece(kind, i, end));
            i = end;
            textStart = end;
        }

        if (textStart < html.Length)
        {
            pieces.Add(new Piece(PieceKind.Text, textStart, html.Length));
        }

        return pieces;
    }

    /// <summary>The index after the <c>&gt;</c> that ends the tag at <paramref name="start"/>; a quote opens a value only
    /// after <c>=</c>, so <c>&gt;</c> and braces inside quoted values belong to the tag.</summary>
    private static int TagEnd(string html, int start)
    {
        char quote = '\0';
        var previous = '\0';
        for (var i = start + 1; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'' && previous == '=')
            {
                quote = c;
                continue;
            }

            if (c == '>')
            {
                return i + 1;
            }

            if (!char.IsWhiteSpace(c))
            {
                previous = c;
            }
        }

        return html.Length;
    }

    private static string TagName(string html, int start, out bool closing)
    {
        var i = start + 1;
        closing = i < html.Length && html[i] == '/';
        if (closing)
        {
            i++;
        }

        var nameStart = i;
        while (i < html.Length && (char.IsAsciiLetterOrDigit(html[i]) || html[i] is ':' or '-'))
        {
            i++;
        }

        return html[nameStart..i];
    }
}
