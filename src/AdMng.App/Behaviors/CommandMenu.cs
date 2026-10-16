using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AdMng.App.Infrastructure;
using AdMng.App.Localization;
using AdMng.Presentation.Shell;

namespace AdMng.App.Behaviors;

/// <summary>
/// Wires a menu item or button to a shell command by its descriptor id (<see cref="CommandDescriptor.Id"/>):
/// <c>&lt;MenuItem b:CommandMenu.Id="object.properties"/&gt;</c>. The element gets the command, its parameter,
/// a localized live header / tool tip / automation name and the shortcut text, so menus, the context menu, the
/// toolbar, key bindings and the command palette always agree. The shell view model is found through the
/// DataContext, the logical tree, or - inside a context menu - its placement target.
/// </summary>
public static class CommandMenu
{
    public static readonly DependencyProperty IdProperty = DependencyProperty.RegisterAttached(
        "Id", typeof(string), typeof(CommandMenu), new PropertyMetadata(null, OnIdChanged));

    private static readonly ShortcutToolTipConverter ToolTipConverter = new();

    public static string? GetId(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(IdProperty);
    }

    public static void SetId(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IdProperty, value);
    }

    /// <summary>Applies the descriptor now (e.g. after building elements in code). Returns false when it was not found.</summary>
    public static bool Apply(FrameworkElement element, ShellViewModel? shell = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        var id = GetId(element);
        shell ??= FindShell(element);
        if (id is null || shell?.CommandPalette.Find(id) is not { } descriptor)
        {
            return false;
        }

        Apply(element, descriptor);
        return true;
    }

    /// <summary>Configures <paramref name="element"/> for <paramref name="descriptor"/>.</summary>
    public static void Apply(FrameworkElement element, CommandDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(descriptor);
        switch (element)
        {
            case MenuItem item:
                item.Command = descriptor.Command;
                item.CommandParameter = descriptor.Parameter;
                if (item.ReadLocalValue(HeaderedItemsControl.HeaderProperty) == DependencyProperty.UnsetValue
                    || BindingOperations.GetBinding(item, HeaderedItemsControl.HeaderProperty) is TrBinding)
                {
                    item.SetBinding(HeaderedItemsControl.HeaderProperty, new TrBinding(descriptor.TitleKey));
                }

                item.InputGestureText = ShortcutText.Format(descriptor.Shortcut);
                if (descriptor.DisabledReason is { } itemReason)
                {
                    // A disabled item says why (e.g. no permission to create this class in the container).
                    DisabledReasonToolTip.Apply(item, descriptor.TitleKey, shortcut: null, () => itemReason(descriptor.Parameter));
                }

                break;
            case ButtonBase button:
                button.Command = descriptor.Command;
                button.CommandParameter = descriptor.Parameter;
                if (descriptor.DisabledReason is { } buttonReason)
                {
                    DisabledReasonToolTip.Apply(button, descriptor.TitleKey, descriptor.Shortcut, () => buttonReason(descriptor.Parameter));
                }
                else
                {
                    button.SetBinding(FrameworkElement.ToolTipProperty, new TrBinding(descriptor.TitleKey)
                    {
                        Converter = ToolTipConverter,
                        ConverterParameter = descriptor.Shortcut,
                    });
                }

                button.SetBinding(AutomationProperties.NameProperty, new TrBinding(descriptor.TitleKey));
                ToolTipService.SetShowOnDisabled(button, true);
                break;
        }
    }

    /// <summary>The shell view model governing <paramref name="element"/>, or null.</summary>
    public static ShellViewModel? FindShell(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        for (var current = element; current is not null; current = ParentOf(current))
        {
            if (current is FrameworkElement { DataContext: ShellViewModel shell })
            {
                return shell;
            }

            if (current is Window window)
            {
                return window.DataContext as ShellViewModel;
            }
        }

        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject element)
    {
        if (element is ContextMenu menu)
        {
            return menu.PlacementTarget;
        }

        return LogicalTreeHelper.GetParent(element)
            ?? (element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : null);
    }

    private static void OnIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnLoaded;
        if (e.NewValue is string)
        {
            element.Loaded += OnLoaded;
            if (element.IsLoaded)
            {
                Apply(element);
            }
        }
    }

    // Menu items are loaded every time their sub-menu or context menu opens; the descriptor is (re)applied then.
    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((FrameworkElement)sender);

    /// <summary>Tool tip text: "Title (shortcut)".</summary>
    private sealed class ShortcutToolTipConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            ShortcutText.WithShortcut(value as string ?? string.Empty, parameter as string);

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            DependencyProperty.UnsetValue;
    }
}
