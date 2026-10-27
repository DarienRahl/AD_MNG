using AdMng.Core.Templates;

namespace AdMng.Core.Messaging.Salutations;

/// <summary>Comparison keys of titles, names and values: lower case, without diacritics, without a trailing dot.</summary>
public static class SalutationKey
{
    /// <summary>"Dr." → "dr", "inż." → "inz", "ŁUCJA" → "lucja".</summary>
    public static string Of(string? word) =>
        Diacritics.Remove((word ?? string.Empty).Trim()).ToLowerInvariant().TrimEnd('.');

    /// <summary>The two texts are equal ignoring case, diacritics and a trailing dot.</summary>
    public static bool Same(string? a, string? b) => string.Equals(Of(a), Of(b), StringComparison.Ordinal);
}

/// <summary>
/// The title table (U1d, settings): rows of <see cref="TitleRule"/> with their spellings tokenized for matching. A
/// <c>null</c> table in the settings means <see cref="Default"/>, so later improvements of the defaults reach operators
/// who never edited it.
/// </summary>
public sealed class TitleTable
{
    /// <summary>Maximum number of rows.</summary>
    public const int MaxRows = 100;

    /// <summary>Maximum length of a spelling or a form.</summary>
    public const int MaxTextLength = 100;

    /// <summary>Maximum rank.</summary>
    public const int MaxRank = 1000;

    public TitleTable(IEnumerable<TitleRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Rules = [.. rules.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Title)).Take(MaxRows)];
        var spellings = new List<TitleSpelling>();
        for (var i = 0; i < Rules.Count; i++)
        {
            var rule = Rules[i];
            foreach (var spelling in new[] { rule.Title }.Concat(rule.Variants ?? []))
            {
                var keys = TitleParser.Tokenize(spelling).Select(t => t.Key).ToList();
                if (keys.Count > 0 && keys.All(k => k.Length > 0) && !spellings.Any(s => s.Rule == i && s.Keys.SequenceEqual(keys, StringComparer.Ordinal)))
                {
                    spellings.Add(new TitleSpelling(i, keys));
                }
            }
        }

        // Longest spelling first; among equal lengths the earlier row.
        Spellings = [.. spellings.OrderByDescending(s => s.Keys.Count).ThenBy(s => s.Rule)];
    }

    /// <summary>The built-in table (<see cref="SalutationData.DefaultTitles"/>).</summary>
    public static TitleTable Default { get; } = new(SalutationData.DefaultTitles);

    /// <summary>The table of the settings: <paramref name="rules"/>, or the default for null.</summary>
    public static TitleTable From(IEnumerable<TitleRule>? rules) => rules is null ? Default : new TitleTable(rules);

    public IReadOnlyList<TitleRule> Rules { get; }

    /// <summary>Every spelling (canonical and variants) of every row as keys, longest first.</summary>
    internal IReadOnlyList<TitleSpelling> Spellings { get; }

    /// <summary>The longest spelling matching the keys at <paramref name="start"/>, or null.</summary>
    internal TitleSpelling? LongestAt(IReadOnlyList<string> keys, int start) =>
        Spellings.FirstOrDefault(s => Matches(keys, start, s.Keys));

    /// <summary>
    /// Rows having a spelling that occurs as a contiguous key sequence in one of <paramref name="segments"/>, by rank
    /// (highest first) and then table order.
    /// </summary>
    internal IReadOnlyList<TitleRule> Candidates(IEnumerable<IReadOnlyList<string>> segments)
    {
        var lists = segments.Where(s => s.Count > 0).ToList();
        var rows = new SortedSet<int>(Spellings
            .Where(s => lists.Any(keys => Enumerable.Range(0, keys.Count).Any(i => Matches(keys, i, s.Keys))))
            .Select(s => s.Rule));
        return [.. rows.OrderByDescending(i => Rules[i].Rank).ThenBy(i => i).Select(i => Rules[i])];
    }

    private static bool Matches(IReadOnlyList<string> keys, int start, IReadOnlyList<string> spelling)
    {
        if (start + spelling.Count > keys.Count)
        {
            return false;
        }

        for (var j = 0; j < spelling.Count; j++)
        {
            if (!string.Equals(keys[start + j], spelling[j], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>One spelling of a row of the title table, as keys.</summary>
internal sealed record TitleSpelling(int Rule, IReadOnlyList<string> Keys);
