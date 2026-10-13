using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AdMng.App.Controls;
using AdMng.App.Localization;
using AdMng.Core.Batch;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Localization;

namespace AdMng.App.Converters;

/// <summary>Severity of a batch preview row → glyph (empty for rows without findings).</summary>
[ValueConversion(typeof(RowSeverity), typeof(string))]
public sealed class RowSeverityToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RowSeverity.Error => Glyphs.Error,
        RowSeverity.Warning => Glyphs.Warning,
        RowSeverity.Info => Glyphs.Info,
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>Outcome of one object of a batch execution → glyph.</summary>
[ValueConversion(typeof(ObjectResultStatus), typeof(string))]
public sealed class ResultStatusToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ObjectResultStatus.Succeeded => Glyphs.Completed,
        ObjectResultStatus.Failed => Glyphs.Error,
        ObjectResultStatus.PartiallySucceeded or ObjectResultStatus.Cancelled => Glyphs.Warning,
        ObjectResultStatus.Skipped => Glyphs.Info,
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>true (succeeded) → check mark, false → error glyph (session log).</summary>
[ValueConversion(typeof(bool), typeof(string))]
public sealed class SucceededToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Glyphs.Completed : Glyphs.Error;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>Non-empty string → true (e.g. enable a tool tip only when there is something to show).</summary>
[ValueConversion(typeof(string), typeof(bool))]
public sealed class NotEmptyToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? text.Length > 0 : value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>
/// Batch operation type → its localized one-line description (<c>OperationHint.*</c>). The texts are composite
/// format strings (escaped braces of template examples), so they are formatted, not shown raw.
/// </summary>
[ValueConversion(typeof(OperationType), typeof(string))]
public sealed class OperationHintConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is OperationType type ? TranslationSource.Localizer.Format(DisplayNames.OperationDescriptionKey(type)) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
