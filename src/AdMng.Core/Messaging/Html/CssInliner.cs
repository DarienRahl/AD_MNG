using AngleSharp.Dom;

namespace AdMng.Core.Messaging.Html;

/// <summary>
/// Cascades style rules into <c>style</c> attributes (design note section 4.3): per element the matching rules by
/// specificity and source order, the element's own <c>style</c> over normal rule declarations, an <c>!important</c> rule
/// declaration over a normal inline one, an important inline declaration over everything. A shorthand that wins removes
/// the longhands it covers from lower-ranked sources (<c>margin</c> over a rule's <c>margin-left</c>). The written
/// declarations are raw (unfiltered) – the sanitizer filters and orders them afterwards.
/// </summary>
public static class CssInliner
{
    private static readonly Dictionary<string, string[]> Shorthands = BuildShorthands();

    /// <summary>Writes the cascaded declarations of every element under <paramref name="root"/> (and the root itself)
    /// into its <c>style</c> attribute.</summary>
    public static void Inline(IElement root, IReadOnlyList<CssRule> rules)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var element in new[] { root }.Concat(root.Descendants<IElement>()).ToList())
        {
            var cascaded = Cascade(element, rules);
            if (cascaded is null)
            {
                continue;
            }

            if (cascaded.Count == 0)
            {
                element.RemoveAttribute("style");
            }
            else
            {
                element.SetAttribute("style", string.Join(";", cascaded.Select(d => d.Property + ":" + d.Value)));
            }
        }
    }

    /// <summary>The cascaded declarations of one element in rank order (null: no rule matches and no style attribute).</summary>
    public static IReadOnlyList<CssDeclaration>? Cascade(IElement element, IReadOnlyList<CssRule> rules)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(rules);
        var name = element.LocalName.ToLowerInvariant();
        var classes = element.ClassList.ToList();
        var id = element.Id;
        var candidates = new List<(CssDeclaration Declaration, (int Level, int Specificity, int Order, int Position) Rank)>();
        var position = 0;
        foreach (var rule in rules)
        {
            var specificity = rule.Selectors.Where(s => s.Matches(name, classes, id)).Select(s => s.Specificity).DefaultIfEmpty(-1).Max();
            if (specificity < 0)
            {
                continue;
            }

            foreach (var declaration in rule.Declarations)
            {
                candidates.Add((declaration, (declaration.Important ? 2 : 0, specificity, rule.Order, position++)));
            }
        }

        var style = element.GetAttribute("style");
        if (candidates.Count == 0 && style is null)
        {
            return null;
        }

        foreach (var declaration in CssParser.ParseDeclarations(style))
        {
            candidates.Add((declaration, (declaration.Important ? 3 : 1, 0, int.MaxValue, position++)));
        }

        var winners = new Dictionary<string, (CssDeclaration Declaration, (int, int, int, int) Rank)>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!winners.TryGetValue(candidate.Declaration.Property, out var current) || Compare(candidate.Rank, current.Rank) >= 0)
            {
                winners[candidate.Declaration.Property] = candidate;
            }
        }

        foreach (var (property, winner) in winners.ToList())
        {
            if (!Shorthands.TryGetValue(property, out var covered))
            {
                continue;
            }

            foreach (var longhand in covered)
            {
                if (winners.TryGetValue(longhand, out var other) && Compare(other.Rank, winner.Rank) < 0)
                {
                    winners.Remove(longhand);
                }
            }
        }

        return [.. winners.Values.OrderBy(w => w.Rank.Item1).ThenBy(w => w.Rank.Item2).ThenBy(w => w.Rank.Item3).ThenBy(w => w.Rank.Item4)
            .Select(w => w.Declaration with { Important = false })];
    }

    /// <summary>The cascaded declarations as a dictionary (last one wins).</summary>
    public static Dictionary<string, string> ToDictionary(IEnumerable<CssDeclaration> declarations)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            result[declaration.Property] = declaration.Value;
        }

        return result;
    }

    private static int Compare((int Level, int Specificity, int Order, int Position) a, (int Level, int Specificity, int Order, int Position) b) =>
        a.Level != b.Level ? a.Level.CompareTo(b.Level)
        : a.Specificity != b.Specificity ? a.Specificity.CompareTo(b.Specificity)
        : a.Order != b.Order ? a.Order.CompareTo(b.Order)
        : a.Position.CompareTo(b.Position);

    private static Dictionary<string, string[]> BuildShorthands()
    {
        string[] sides = ["top", "right", "bottom", "left"];
        var direct = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["margin"] = [.. sides.Select(s => "margin-" + s)],
            ["padding"] = [.. sides.Select(s => "padding-" + s)],
            ["border"] = [.. sides.Select(s => "border-" + s), "border-width", "border-style", "border-color"],
            ["border-width"] = [.. sides.Select(s => $"border-{s}-width")],
            ["border-style"] = [.. sides.Select(s => $"border-{s}-style")],
            ["border-color"] = [.. sides.Select(s => $"border-{s}-color")],
            ["font"] = ["font-style", "font-variant", "font-weight", "font-size", "line-height", "font-family"],
            ["text-decoration"] = ["text-decoration-line", "text-decoration-style", "text-decoration-color"],
            ["background"] = ["background-color", "background-image", "background-repeat", "background-position", "background-size"],
            ["list-style"] = ["list-style-type", "list-style-position", "list-style-image"],
        };
        foreach (var side in sides)
        {
            direct["border-" + side] = [$"border-{side}-width", $"border-{side}-style", $"border-{side}-color"];
        }

        // Transitive: border covers border-top, which covers border-top-width.
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var shorthand in direct.Keys)
        {
            var all = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(direct[shorthand]);
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (all.Add(next) && direct.TryGetValue(next, out var deeper))
                {
                    foreach (var d in deeper)
                    {
                        queue.Enqueue(d);
                    }
                }
            }

            result[shorthand] = [.. all];
        }

        return result;
    }
}
