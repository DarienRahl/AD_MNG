using System.Globalization;
using System.Text;

namespace AdMng.Presentation.Infrastructure;

/// <summary>
/// Case- and diacritics-insensitive text matching for quick filters and the command palette
/// ("lukasz zolc" finds "Łukasz Żółć").
/// </summary>
public static class TextFolding
{
    /// <summary>Lower-cases and removes diacritics (including letters that do not decompose, such as ł, đ, ø).</summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(c switch
            {
                'ł' or 'Ł' => 'l',
                'đ' or 'Đ' => 'd',
                'ø' or 'Ø' => 'o',
                'ß' => 's',
                _ => char.ToLowerInvariant(c),
            });
        }

        return sb.ToString();
    }

    /// <summary>True when the folded <paramref name="haystack"/> contains the folded <paramref name="needle"/>.</summary>
    public static bool Contains(string? haystack, string? needle) =>
        string.IsNullOrEmpty(needle) || Fold(haystack).Contains(Fold(needle), StringComparison.Ordinal);

    /// <summary>
    /// Fuzzy subsequence score used by the command palette: null when the folded query is not a subsequence of the
    /// folded candidate; otherwise higher is better (word starts and consecutive matches score more, gaps cost).
    /// </summary>
    public static int? SubsequenceScore(string candidate, string query)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var q = Fold(query).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (q.Length == 0)
        {
            return 0;
        }

        var c = Fold(candidate);
        var score = 0;
        var qi = 0;
        var previous = -2;
        for (var ci = 0; ci < c.Length && qi < q.Length; ci++)
        {
            if (c[ci] != q[qi])
            {
                continue;
            }

            var wordStart = ci == 0 || !char.IsLetterOrDigit(c[ci - 1]);
            score += 1;
            if (wordStart)
            {
                score += 8;
            }

            if (previous == ci - 1)
            {
                score += 5;
            }
            else if (previous >= 0)
            {
                score -= Math.Min(3, ci - previous - 1);
            }

            previous = ci;
            qi++;
        }

        if (qi < q.Length)
        {
            return null;
        }

        // Prefer shorter candidates when everything else is equal.
        return (score * 100) - c.Length;
    }
}
