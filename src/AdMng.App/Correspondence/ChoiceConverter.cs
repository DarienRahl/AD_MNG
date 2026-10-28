using System.Globalization;
using System.Windows.Data;

namespace AdMng.App.Correspondence;

/// <summary>
/// A radio button of one value of an enum property (<c>ConverterParameter</c> = the value): checked when the property has
/// that value; checking it sets the value, unchecking it (another button of the group was checked) changes nothing.
/// </summary>
[ValueConversion(typeof(object), typeof(bool))]
public sealed class ChoiceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : Binding.DoNothing;
}
