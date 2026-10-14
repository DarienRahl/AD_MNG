using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AdMng.App.Infrastructure;
using AdMng.App.Localization;

namespace AdMng.App.Behaviors;

/// <summary>
/// Tool tip that explains why a command element is disabled (e.g. "You do not have permission to create objects of
/// type 'Group' in this container."). While the element is enabled it shows the normal tool tip: the localized title
/// with its shortcut for buttons, nothing for menu items. Re-evaluated when the element's IsEnabled changes (its
/// command's CanExecute) and when the language changes.
/// </summary>
internal static class DisabledReasonToolTip
{
    /// <param name="element">Menu item or button bound to the command.</param>
    /// <param name="titleKey">Resource key of the command title.</param>
    /// <param name="shortcut">Shortcut shown with the title in the enabled tool tip; null = no enabled tool tip (menu items).</param>
    /// <param name="reason">Localized reason while disabled, or null when there is none.</param>
    public static void Apply(FrameworkElement element, string titleKey, string? shortcut, Func<string?> reason)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(reason);
        var binding = new MultiBinding { Converter = new ReasonConverter(reason, shortcut), Mode = BindingMode.OneWay };
        binding.Bindings.Add(new Binding(nameof(UIElement.IsEnabled)) { RelativeSource = RelativeSource.Self });
        binding.Bindings.Add(new TrBinding(titleKey));
        element.SetBinding(FrameworkElement.ToolTipProperty, binding);
        ToolTipService.SetShowOnDisabled(element, true);
    }

    private sealed class ReasonConverter(Func<string?> reason, string? shortcut) : IMultiValueConverter
    {
        public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            var enabled = values.Length > 0 && values[0] is true;
            var title = values.Length > 1 ? values[1] as string ?? string.Empty : string.Empty;
            if (!enabled && reason() is { Length: > 0 } text)
            {
                return text;
            }

            return shortcut is null ? null : ShortcutText.WithShortcut(title, shortcut);
        }

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            [.. targetTypes.Select(_ => DependencyProperty.UnsetValue)];
    }
}
