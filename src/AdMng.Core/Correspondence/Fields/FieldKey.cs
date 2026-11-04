using System.Text;
using AdMng.Core.Templates;

namespace AdMng.Core.Correspondence.Fields;

/// <summary>
/// The comparison key of field and column names (design note 3.2): trimmed, whitespace collapsed, lower-invariant,
/// diacritics removed, '-', '_' and spaces removed ("Kod pocztowy" == "kodpocztowy").
/// </summary>
/// <remarks>Every white-space character (U+00A0 included), the hyphens U+2010/U+2011, soft hyphens and zero-width
/// characters are removed as well, so a name copied from Word or a web page matches the column it names.</remarks>
public static class FieldKey
{
    /// <summary>The folded key of <paramref name="name"/> ("" for null).</summary>
    public static string Of(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var folded = Diacritics.Remove(name);
        var builder = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '_' or '\u2010' or '\u2011' or '\u00AD' or '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF')
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>True when both names have the same key.</summary>
    public static bool Same(string? a, string? b) => string.Equals(Of(a), Of(b), StringComparison.Ordinal);
}
