using System.Globalization;
using System.Windows.Data;

namespace AdMng.App.Correspondence;

/// <summary>
/// The two-way <c>SelectedItem</c> of a combo box inside a virtualized list row (the owner candidates of the "Skrzynki"
/// grid): the value passes through, but a cleared selection never reaches the row. WPF clears a combo box's selection while
/// a recycled or removed row container swaps its items and its data context; written back, that would erase the owner the
/// operator chose for a mailbox merely by scrolling. A non-editable combo box offers no way to choose "nothing", so no
/// choice of the operator is lost.
/// </summary>
[ValueConversion(typeof(object), typeof(object))]
public sealed class KeepChoiceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value ?? Binding.DoNothing;
}
