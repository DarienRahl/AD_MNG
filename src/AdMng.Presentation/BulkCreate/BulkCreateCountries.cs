using System.Globalization;
using System.Resources;
using AdMng.Core.Batch.Creation;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// The country table of the <c>Kraj/region</c> target (docs/dev/phase3-bulk-create.md 2.6, owner B3), built from the ISO 3166
/// table of the property sheets (<see cref="Countries.All"/>): the alpha-2 code, the numeric code and the <c>Country.XX</c>
/// names of both resource languages (Polish and English), each folded (<see cref="ColumnSynonyms.Fold"/>) and mapped to the
/// code, the name in the UI language (<c>co</c>, as ADUC writes it) and the numeric code (<c>countryCode</c>).
/// </summary>
public static class BulkCreateCountries
{
    private static readonly ResourceManager Resources = new(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);

    private static readonly CultureInfo[] Languages = [CultureInfo.GetCultureInfo("pl"), CultureInfo.InvariantCulture];

    /// <summary>The table keyed by folded name or code (<see cref="BulkCreateInput.Countries"/>).</summary>
    public static IReadOnlyDictionary<string, CountryValue> Build(ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var table = new Dictionary<string, CountryValue>(StringComparer.Ordinal);
        foreach (var country in Countries.All)
        {
            var key = "Country." + country.Code;
            var value = new CountryValue(country.Code, localizer.Contains(key) ? localizer[key] : country.Code, country.NumericCode);
            Add(table, country.Code, value);
            Add(table, country.NumericCode.ToString(CultureInfo.InvariantCulture), value);
            Add(table, country.NumericCode.ToString("D3", CultureInfo.InvariantCulture), value);
            foreach (var language in Languages)
            {
                if (Resources.GetString(key, language) is { Length: > 0 } name)
                {
                    Add(table, name, value);
                }
            }
        }

        return table;
    }

    private static void Add(Dictionary<string, CountryValue> table, string text, CountryValue value)
    {
        var folded = ColumnSynonyms.Fold(text);
        if (folded.Length > 0)
        {
            table.TryAdd(folded, value);
        }
    }
}
