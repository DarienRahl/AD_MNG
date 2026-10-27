using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace AdMng.Core.Messaging.Html;

/// <summary>
/// Joins template fields that Outlook / Word split with inline tags (design note section 5): the spell checker
/// (<c>span.SpellE</c>, <c>span.GramE</c>), a language change (<c>span lang=EN-US</c>), bookmarks or formatting applied to
/// part of a field. Within one block (never across <c>&lt;br&gt;</c> or a block boundary) the text is concatenated, every
/// <c>{…}</c> that is a valid field (U+00A0 read as a space, soft hyphens and zero-width characters removed) and spans
/// several text nodes is moved into the node holding its longest piece (that formatting wins). <c>%7B…%7D</c> in a link
/// target that decodes to a valid field is decoded (Outlook's auto-link of a typed URL with a field). Text that only looks
/// like braces is left alone.
/// </summary>
public static partial class PlaceholderRepair
{
    private static readonly char[] Invisible = ['\u00AD', '\u200B', '\u200C', '\u200D', '\u2060', '\uFEFF'];

    /// <summary>Repairs the fields under <paramref name="root"/>; returns the repaired fields (in order, one per repair).</summary>
    /// <param name="root">The sanitized content.</param>
    /// <param name="isField">True for a valid field including its braces (<c>{givenName}</c>).</param>
    public static IReadOnlyList<string> Repair(IElement root, Func<string, bool> isField)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(isField);
        var repaired = new List<string>();
        foreach (var block in new[] { root }.Concat(root.Descendants<IElement>().Where(HtmlSanitizerRun.IsBlock)).ToList())
        {
            foreach (var run in Runs(block))
            {
                RepairRun(run, isField, repaired);
            }
        }

        foreach (var anchor in root.Descendants<IElement>().Where(e => e.LocalName == "a" && e.HasAttribute("href")).ToList())
        {
            var href = anchor.GetAttribute("href")!;
            var decoded = EncodedFieldPattern().Replace(href, match =>
            {
                string candidate;
                try
                {
                    candidate = Uri.UnescapeDataString(match.Value);
                }
                catch (UriFormatException)
                {
                    return match.Value;
                }

                if (isField(candidate))
                {
                    repaired.Add(candidate);
                    return candidate;
                }

                return match.Value;
            });
            if (!string.Equals(decoded, href, StringComparison.Ordinal))
            {
                anchor.SetAttribute("href", decoded);
            }
        }

        return repaired;
    }

    /// <summary>The text nodes of a block in document order, split at <c>&lt;br&gt;</c>; nested blocks are runs of their own.</summary>
    private static List<List<IText>> Runs(IElement block)
    {
        var runs = new List<List<IText>> { new() };
        void Walk(INode node)
        {
            foreach (var child in node.ChildNodes)
            {
                switch (child)
                {
                    case IText text:
                        runs[^1].Add(text);
                        break;
                    case IElement { LocalName: "br" }:
                        runs.Add([]);
                        break;
                    case IElement element when HtmlSanitizerRun.IsBlock(element):
                        runs.Add([]);
                        break;
                    case IElement element:
                        Walk(element);
                        break;
                }
            }
        }

        Walk(block);
        return [.. runs.Where(r => r.Count > 0)];
    }

    private static void RepairRun(List<IText> run, Func<string, bool> isField, List<string> repaired)
    {
        // Each repair changes the node texts: start again until nothing changes (bounded by the number of braces).
        for (var guard = 0; guard < 256; guard++)
        {
            var text = new StringBuilder();
            var starts = new List<int>(run.Count);
            foreach (var node in run)
            {
                starts.Add(text.Length);
                text.Append(node.Data);
            }

            var all = text.ToString();
            var changed = false;
            for (var open = all.IndexOf('{', StringComparison.Ordinal); open >= 0; open = all.IndexOf('{', open + 1))
            {
                if (open + 1 < all.Length && all[open + 1] == '{')
                {
                    open++;
                    continue;
                }

                var close = all.IndexOfAny(['}', '{', '\n'], open + 1);
                if (close < 0 || all[close] != '}')
                {
                    continue;
                }

                var raw = all[open..(close + 1)];
                var field = Normalize(raw);
                if (!isField(field))
                {
                    continue;
                }

                var pieces = new List<(int Node, int From, int To)>();
                for (var i = 0; i < run.Count; i++)
                {
                    var from = Math.Max(open, starts[i]);
                    var to = Math.Min(close + 1, starts[i] + run[i].Data.Length);
                    if (from < to)
                    {
                        pieces.Add((i, from - starts[i], to - starts[i]));
                    }
                }

                if (pieces.Count <= 1 && field == raw)
                {
                    continue;
                }

                var keeper = pieces.OrderByDescending(p => p.To - p.From).ThenBy(p => p.Node).First();
                foreach (var piece in pieces.OrderByDescending(p => p.Node))
                {
                    var data = run[piece.Node].Data;
                    run[piece.Node].Data = piece == keeper
                        ? data[..piece.From] + field + data[piece.To..]
                        : data[..piece.From] + data[piece.To..];
                }

                repaired.Add(field);
                changed = true;
                break;
            }

            if (!changed)
            {
                return;
            }
        }
    }

    /// <summary>U+00A0 → space, soft hyphens and zero-width characters removed.</summary>
    private static string Normalize(string candidate)
    {
        var builder = new StringBuilder(candidate.Length);
        foreach (var c in candidate)
        {
            if (Array.IndexOf(Invisible, c) >= 0)
            {
                continue;
            }

            builder.Append(c == '\u00A0' ? ' ' : c);
        }

        return builder.ToString();
    }

    [GeneratedRegex("%7[Bb](?:(?!%7[Dd]).){1,200}?%7[Dd]", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedFieldPattern();
}
