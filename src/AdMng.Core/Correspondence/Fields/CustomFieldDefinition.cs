using System.Globalization;
using AdMng.Core.Messaging;

namespace AdMng.Core.Correspondence.Fields;

/// <summary>What a custom field holds.</summary>
public enum CustomFieldKind
{
    /// <summary>Text that may use <c>{{other fields}}</c> (not other custom fields).</summary>
    Text,

    /// <summary>A date (<see cref="CustomFieldDefinition.Value"/> is <c>yyyy-MM-dd</c>) rendered with a date format.</summary>
    Date,
}

/// <summary>
/// An operator-defined field. <see cref="CustomFieldKind.Text"/>: <paramref name="Value"/> may use <c>{{other fields}}</c>
/// (not other custom fields). <see cref="CustomFieldKind.Date"/>: <paramref name="Value"/> is <c>yyyy-MM-dd</c>
/// (invariant), <paramref name="Format"/> a .NET custom date format (null or blank → <see cref="CustomFieldFormats.DefaultDate"/>),
/// rendered with the Polish culture (<see cref="MessageLanguages.Culture"/>); an empty or unparsable date renders "".
/// </summary>
public sealed record CustomFieldDefinition(string Name, string Value, CustomFieldKind Kind = CustomFieldKind.Text, string? Format = null)
{
    public override string ToString() => $"{nameof(CustomFieldDefinition)} {Name} ({Kind})";
}

/// <summary>The date formats of date fields (<see cref="CustomFieldKind.Date"/>).</summary>
public static class CustomFieldFormats
{
    /// <summary>"15 listopada 2026r." – as the operator's mailbox query writes its deadline (no space before "r.").</summary>
    public const string DefaultDate = "d MMMM yyyy'r.'";

    /// <summary>The invariant format of a date field's <see cref="CustomFieldDefinition.Value"/>.</summary>
    public const string ValueFormat = "yyyy-MM-dd";

    /// <summary>The longest format used; a longer one renders with <see cref="DefaultDate"/>.</summary>
    public const int MaxFormatLength = 64;

    /// <summary>The formats offered (the combo box is editable: any .NET custom date format works).</summary>
    public static IReadOnlyList<string> DateChoices { get; } =
    [
        DefaultDate,
        "d MMMM yyyy 'r.'",
        "d MMMM yyyy",
        "dd.MM.yyyy",
        "dd.MM.yyyy 'r.'",
        "dddd, d MMMM yyyy",
        "yyyy-MM-dd",
    ];

    /// <summary>
    /// <paramref name="date"/> in the Polish culture with <paramref name="format"/> (the genitive month after a day:
    /// "15 listopada 2026r."); a blank, too long or invalid format (or one with time parts) → <see cref="DefaultDate"/>.
    /// </summary>
    public static string FormatDate(DateOnly date, string? format)
    {
        var culture = MessageLanguages.Culture(MessageLanguage.Polish);
        if (!string.IsNullOrWhiteSpace(format) && format.Length <= MaxFormatLength)
        {
            try
            {
                return date.ToString(format, culture);
            }
            catch (FormatException)
            {
                // An unclosed quote, a time part or an unknown standard format: the default below.
            }
        }

        return date.ToString(DefaultDate, culture);
    }

    /// <summary>The date of a date field's value (<see cref="ValueFormat"/>, surrounding white space ignored).</summary>
    public static bool TryParseValue(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value?.Trim(), ValueFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>The value of a date field for <paramref name="date"/> (<see cref="ValueFormat"/>).</summary>
    public static string ValueOf(DateOnly date) => date.ToString(ValueFormat, CultureInfo.InvariantCulture);

    /// <summary>The text a date field renders: its date formatted, "" when it has none (or one that does not parse).</summary>
    public static string DateText(CustomFieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return TryParseValue(field.Value, out var date) ? FormatDate(date, field.Format) : string.Empty;
    }
}
