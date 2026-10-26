using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Localization;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Services;

namespace AdMng.App.Converters;

/// <summary>
/// true → Visible, false → Collapsed. <see cref="Invert"/> swaps the result. Nullable bools: null counts as false.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility visibility ? (visibility == Visibility.Visible) != Invert : DependencyProperty.UnsetValue;
}

/// <summary>Inverts a bool (for IsEnabled bindings).</summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Null, empty strings and empty collections → Collapsed, anything else → Visible (<see cref="Invert"/> swaps).
/// </summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            string text => text.Length == 0,
            ICollection collection => collection.Count == 0,
            int count => count == 0,
            _ => false,
        };
        return empty != Invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary><c>Equals(value, ConverterParameter)</c> (radio-like menu check marks). One-way only.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && parameter is string expected
            ? string.Equals(text, expected, StringComparison.Ordinal)
            : Equals(value, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Multi-binding: Visible when all values are the same instance (e.g. a document and the selected document), else
/// Collapsed.
/// </summary>
public sealed class SameInstanceToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not { Length: >= 2 } || values[0] is null || values[0] == DependencyProperty.UnsetValue)
        {
            return Visibility.Collapsed;
        }

        return values.Skip(1).All(v => ReferenceEquals(v, values[0])) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>
/// Multi-binding: the first value is a composite format string (usually a <see cref="TrBinding"/>), the others are
/// its arguments; formatted with the UI culture. Unset values count as empty strings.
/// </summary>
public sealed class FormatConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not { Length: > 0 } || values[0] is not string format)
        {
            return string.Empty;
        }

        var args = values.Skip(1).Select(v => v == DependencyProperty.UnsetValue ? string.Empty : v).ToArray();
        try
        {
            return string.Format(TranslationSource.Localizer.Culture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>Sort direction of a column → chevron glyph for the header (empty when not sorted).</summary>
[ValueConversion(typeof(SortDirection), typeof(string))]
public sealed class SortDirectionToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SortDirection.Ascending => Glyphs.ChevronUp,
        SortDirection.Descending => Glyphs.ChevronDown,
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>Toast severity → glyph.</summary>
[ValueConversion(typeof(NotificationSeverity), typeof(string))]
public sealed class SeverityToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        NotificationSeverity.Success => Glyphs.Completed,
        NotificationSeverity.Warning => Glyphs.Warning,
        NotificationSeverity.Error => Glyphs.Error,
        _ => Glyphs.Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>Keyboard gesture text of a command descriptor ("Ctrl+OemComma") → display text ("Ctrl+,").</summary>
[ValueConversion(typeof(string), typeof(string))]
public sealed class ShortcutTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ShortcutText.Format(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>
/// A connection colour (<see cref="Presentation.Connections.ConnectionColor"/>, null = not connected) → its solid brush:
/// the identity strip, the connection badge and the colour dots of the window switcher.
/// </summary>
public sealed class ConnectionColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Connections.ConnectionChrome.Brush(value as Presentation.Connections.ConnectionColor?);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
