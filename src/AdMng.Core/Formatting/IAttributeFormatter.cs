using System.Globalization;
using AdMng.Core.Model;

namespace AdMng.Core.Formatting;

/// <summary>
/// Converts raw attribute values to operator friendly text (attribute editor, list columns, CSV export)
/// and back (attribute editor input).
/// </summary>
/// <remarks>
/// Rules: FILETIME attributes (<see cref="ActiveDirectory.Attr.FileTimeAttributes"/>) as local date/time or
/// "never"; GeneralizedTime as local date/time; objectGUID as GUID; SIDs as S-1-...; userAccountControl and
/// groupType as number plus decoded flag names; booleans as TRUE/FALSE; other binary values as hex
/// (truncated for very large values); DN values unchanged; multiple values joined with "; ".
/// </remarks>
public interface IAttributeFormatter
{
    /// <summary>All values of an attribute as one display string.</summary>
    string Format(string attribute, AttributeValues? values, CultureInfo culture);

    /// <summary>One value as a display string.</summary>
    string FormatValue(string attribute, object value, CultureInfo culture);

    /// <summary>
    /// Parses operator input for an attribute using its schema syntax into the wire value (string or byte[]).
    /// Returns false with an English diagnostic when the input is not valid for the syntax.
    /// </summary>
    bool TryParse(string attribute, string input, CultureInfo culture, out object? value, out string? error);
}
