using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Schema;

namespace AdMng.Presentation.Lists;

/// <summary>
/// Local, stable, culture aware sorting of loaded rows. Keys come from raw values (thread-safe, no formatting):
/// numbers and FILETIME values numerically, GeneralizedTime ordinally, text with the UI culture's collation
/// (Polish: ą after a, ł after l, ż after ź).
/// </summary>
public static class RowSorting
{
    public static IReadOnlyList<ObjectRowViewModel> Sort(
        IReadOnlyList<ObjectRowViewModel> rows,
        string attribute,
        bool descending,
        CultureInfo culture,
        ISchemaCatalog? schema)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(culture);
        var keyKind = KeyKindOf(attribute, schema);
        var comparer = new KeyComparer(StringComparer.Create(culture, CompareOptions.IgnoreCase));
        var keyed = rows.Select(r => (Row: r, Key: KeyOf(r, attribute, keyKind)));
        var ordered = descending ? keyed.OrderByDescending(k => k.Key, comparer) : keyed.OrderBy(k => k.Key, comparer);
        return [.. ordered.Select(k => k.Row)];
    }

    private enum KeyKind
    {
        Text,
        Number,
        Ordinal,
    }

    private static KeyKind KeyKindOf(string attribute, ISchemaCatalog? schema)
    {
        if (Attr.FileTimeAttributes.Contains(attribute))
        {
            return KeyKind.Number;
        }

        return schema?.FindAttribute(attribute)?.Syntax switch
        {
            AttributeSyntax.Integer or AttributeSyntax.LargeInteger or AttributeSyntax.Enumeration => KeyKind.Number,
            AttributeSyntax.GeneralizedTime or AttributeSyntax.UtcTime => KeyKind.Ordinal,
            _ => string.Equals(attribute, Attr.WhenCreated, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(attribute, Attr.WhenChanged, StringComparison.OrdinalIgnoreCase)
                ? KeyKind.Ordinal
                : KeyKind.Text,
        };
    }

    private static SortKeyValue KeyOf(ObjectRowViewModel row, string attribute, KeyKind kind)
    {
        if (string.Equals(attribute, ColumnSets.TypeColumn, StringComparison.OrdinalIgnoreCase))
        {
            return new SortKeyValue(null, row.TypeText, false);
        }

        if (string.Equals(attribute, ColumnSets.LocationColumn, StringComparison.OrdinalIgnoreCase))
        {
            return new SortKeyValue(null, row.ParentPath, false);
        }

        if (string.Equals(attribute, Attr.Name, StringComparison.OrdinalIgnoreCase))
        {
            return new SortKeyValue(null, row.Name, false);
        }

        var raw = row.Object.GetString(attribute);
        return kind switch
        {
            KeyKind.Number => new SortKeyValue(AdConvert.ParseInt64(raw), null, false),
            KeyKind.Ordinal => new SortKeyValue(null, raw, true),
            _ => new SortKeyValue(null, raw, false),
        };
    }

    private readonly record struct SortKeyValue(long? Number, string? Text, bool Ordinal);

    /// <summary>Missing values sort first (ascending), like ADUC.</summary>
    private sealed class KeyComparer(StringComparer text) : IComparer<SortKeyValue>
    {
        public int Compare(SortKeyValue x, SortKeyValue y)
        {
            if (x.Number is not null || y.Number is not null)
            {
                return Nullable.Compare(x.Number, y.Number);
            }

            if (x.Text is null || y.Text is null)
            {
                return (x.Text is null ? 0 : 1) - (y.Text is null ? 0 : 1);
            }

            return x.Ordinal ? string.CompareOrdinal(x.Text, y.Text) : text.Compare(x.Text, y.Text);
        }
    }
}
