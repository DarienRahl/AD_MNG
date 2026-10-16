using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.PropertySheets;

namespace AdMng.App.Converters;

/// <summary>
/// Group scope radio buttons of a group's General page: <c>ScopeOptions</c> (the conversions ADUC allows) → whether the
/// scope given as <c>ConverterParameter</c> may be chosen. Scopes that are not listed (options not loaded yet) are disabled.
/// </summary>
[ValueConversion(typeof(IEnumerable), typeof(bool))]
public sealed class GroupScopeEnabledConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable options && parameter is GroupScope scope
        && options.OfType<GroupScopeOption>().Any(o => o.Scope == scope && o.IsEnabled);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>
/// Radio buttons over a nullable bool (Attribute Editor: TRUE / FALSE / not set). <c>ConverterParameter</c> is
/// "True", "False" or "Null"; a checked radio button writes its value back, an unchecked one writes nothing.
/// </summary>
[ValueConversion(typeof(bool?), typeof(bool))]
public sealed class NullableBoolRadioConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value as bool?, Parse(parameter));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true)
        {
            return Binding.DoNothing;
        }

        // null ("not set") is a valid value of the bound bool? property.
        return Parse(parameter) is { } flag ? flag : null!;
    }

    private static bool? Parse(object? parameter) => parameter switch
    {
        bool flag => flag,
        string text when bool.TryParse(text, out var flag) => flag,
        _ => null,
    };
}
