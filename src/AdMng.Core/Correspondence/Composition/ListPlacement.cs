using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Messaging.Html;
using AngleSharp.Dom;

namespace AdMng.Core.Correspondence.Composition;

/// <summary>
/// Moves every block field – <c>{{lista}}</c> (a <c>&lt;ul&gt;</c>) and <c>{{podpis}}</c> (the signature's paragraphs and
/// tables) – out of its paragraph before it is merged in: a block inside a <c>&lt;p&gt;</c> is not valid HTML (a browser
/// closes the paragraph and leaves an empty one behind, which Outlook shows as a blank line). The paragraph is split at
/// the token – inline formatting around it is split too – and empty halves are dropped, so <c>&lt;p&gt;{{lista}}&lt;/p&gt;</c>
/// becomes the list alone and <c>&lt;p&gt;Skrzynki: {{lista}} – proszę…&lt;/p&gt;</c> two paragraphs around it.
/// </summary>
internal static class ListPlacement
{
    private const int MaxSplits = 1000;

    /// <summary>The body with every token of <c>{{lista}}</c> moved out of its paragraph.</summary>
    public static string Prepare(string bodyHtml) => Prepare(bodyHtml, new HashSet<string>(StringComparer.Ordinal) { BuiltInFields.List });

    /// <summary>The body with every token of the fields <paramref name="fieldKeys"/> (<see cref="FieldKey"/>) moved out of
    /// its paragraph; the body unchanged when it has none.</summary>
    public static string Prepare(string bodyHtml, IReadOnlySet<string> fieldKeys)
    {
        ArgumentNullException.ThrowIfNull(fieldKeys);
        if (fieldKeys.Count == 0 || !FieldTemplate.Parse(bodyHtml).Any(t => fieldKeys.Contains(FieldKey.Of(t.Field))))
        {
            return bodyHtml;
        }

        if (HtmlNesting.ParseDocument("<!DOCTYPE html><html><head></head><body>" + bodyHtml + "</body></html>") is not { Body: { } body })
        {
            return bodyHtml;   // refused by the sanitizer later anyway
        }

        var changed = false;
        for (var guard = 0; guard < MaxSplits && NextToken(body, fieldKeys) is { } found; guard++)
        {
            var (text, start, length) = found;
            var token = start == 0 ? text : text.Split(start);
            if (token.Data.Length > length)
            {
                token.Split(length);
            }

            Split(token);
            changed = true;
        }

        return changed ? body.InnerHtml : bodyHtml;
    }

    /// <summary>The first text node with a token of <paramref name="fieldKeys"/> that lies in a paragraph (a token already
    /// moved out stands outside any paragraph).</summary>
    private static (IText Text, int Start, int Length)? NextToken(IElement body, IReadOnlySet<string> fieldKeys)
    {
        foreach (var text in body.Descendants<IText>())
        {
            if (Paragraph(text) is null)
            {
                continue;
            }

            foreach (var token in FieldTemplate.Parse(text.Data))
            {
                if (fieldKeys.Contains(FieldKey.Of(token.Field)))
                {
                    return (text, token.Start, token.Length);
                }
            }
        }

        return null;
    }

    private static IElement? Paragraph(INode node)
    {
        for (var parent = node.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            if (parent.LocalName == "p")
            {
                return parent;
            }

            if (HtmlPolicy.BlockElements.Contains(parent.LocalName))
            {
                return null;   // a list may stand in a div, a cell or a list item
            }
        }

        return null;
    }

    /// <summary>Splits the paragraph around <paramref name="token"/> (a text node holding exactly the token).</summary>
    private static void Split(IText token)
    {
        var paragraph = Paragraph(token)!;
        var path = new List<INode>();
        for (INode node = token; node != paragraph; node = node.Parent!)
        {
            path.Insert(0, node);
        }

        var left = (IElement)paragraph.Clone(deep: false);
        var right = (IElement)paragraph.Clone(deep: false);
        INode leftParent = left;
        INode rightParent = right;
        INode container = paragraph;
        foreach (var step in path)
        {
            var children = container.ChildNodes.ToList();
            var index = children.IndexOf(step);
            for (var i = 0; i < index; i++)
            {
                leftParent.AppendChild(children[i]);
            }

            if (step != token)
            {
                var leftClone = step.Clone(deep: false);
                var rightClone = step.Clone(deep: false);
                leftParent.AppendChild(leftClone);
                rightParent.AppendChild(rightClone);
                for (var i = index + 1; i < children.Count; i++)
                {
                    rightClone.AppendChild(children[i]);
                }

                leftParent = leftClone;
                rightParent = rightClone;
                container = step;
                continue;
            }

            for (var i = index + 1; i < children.Count; i++)
            {
                rightParent.AppendChild(children[i]);
            }
        }

        var owner = paragraph.Parent!;
        if (HasContent(left))
        {
            owner.InsertBefore(left, paragraph);
        }

        owner.InsertBefore(token, paragraph);
        if (HasContent(right))
        {
            owner.InsertBefore(right, paragraph);
        }

        owner.RemoveChild(paragraph);
    }

    /// <summary>Visible text (not only white space or non-breaking spaces) or an image.</summary>
    private static bool HasContent(IElement element) =>
        element.Descendants<IText>().Any(t => t.Data.Any(c => !char.IsWhiteSpace(c)))
        || element.Descendants<IElement>().Any(e => e.LocalName is "img" or "hr" or "table");
}
