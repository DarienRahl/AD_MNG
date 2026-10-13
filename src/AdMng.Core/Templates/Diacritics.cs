using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace AdMng.Core.Templates;

/// <summary>Removes diacritics from Latin text ("Łukasz Żółć" → "Lukasz Zolc").</summary>
internal static class Diacritics
{
    /// <summary>
    /// Explicit mappings. All Polish letters are listed for clarity and speed; ł/Ł (and the other letters below)
    /// have no Unicode canonical decomposition, so they would survive the NFD based removal.
    /// </summary>
    private static readonly FrozenDictionary<char, string> Explicit = new Dictionary<char, string>
    {
        ['ą'] = "a", ['ć'] = "c", ['ę'] = "e", ['ł'] = "l", ['ń'] = "n", ['ó'] = "o", ['ś'] = "s", ['ź'] = "z", ['ż'] = "z",
        ['Ą'] = "A", ['Ć'] = "C", ['Ę'] = "E", ['Ł'] = "L", ['Ń'] = "N", ['Ó'] = "O", ['Ś'] = "S", ['Ź'] = "Z", ['Ż'] = "Z",
        ['ß'] = "ss", ['ẞ'] = "SS", ['æ'] = "ae", ['Æ'] = "AE", ['œ'] = "oe", ['Œ'] = "OE", ['ø'] = "o", ['Ø'] = "O",
        ['đ'] = "d", ['Đ'] = "D", ['ð'] = "d", ['Ð'] = "D", ['þ'] = "th", ['Þ'] = "Th", ['ħ'] = "h", ['Ħ'] = "H",
        ['ı'] = "i", ['ŀ'] = "l", ['Ŀ'] = "L", ['ŧ'] = "t", ['Ŧ'] = "T",
    }.ToFrozenDictionary();

    public static string Remove(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Ascii.IsValid(value))
        {
            return value;
        }

        var mapped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (Explicit.TryGetValue(c, out var replacement))
            {
                mapped.Append(replacement);
            }
            else
            {
                mapped.Append(c);
            }
        }

        var decomposed = mapped.ToString().Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                result.Append(c);
            }
        }

        return result.ToString().Normalize(NormalizationForm.FormC);
    }
}
