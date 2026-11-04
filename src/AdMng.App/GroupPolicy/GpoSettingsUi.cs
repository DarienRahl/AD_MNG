using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AdMng.Presentation.GroupPolicy;

namespace AdMng.App.GroupPolicy;

/// <summary>
/// Behaviours of the GPO document's "Ustawienia" tab (K6, docs/dev/k-computers-and-gpo.md 20.1): the list's rows grouped by
/// their properties (a fresh grouped view for every list the view model shows), the search box takes the keyboard when Ctrl+F
/// switched to the tab (the box exists only while its tab is shown), and a double click on a row runs a command ("Edytuj to
/// ustawienie").
/// </summary>
public static class GpoSettingsUi
{
    /// <summary>The properties the rows are grouped by, outermost first, comma separated ("Configuration,Group").</summary>
    public static readonly DependencyProperty GroupByProperty = DependencyProperty.RegisterAttached(
        "GroupBy", typeof(string), typeof(GpoSettingsUi), new PropertyMetadata(null));

    /// <summary>The rows: the items control shows them through a view grouped by <see cref="GroupByProperty"/>.</summary>
    public static readonly DependencyProperty GroupedItemsSourceProperty = DependencyProperty.RegisterAttached(
        "GroupedItemsSource", typeof(IEnumerable), typeof(GpoSettingsUi), new PropertyMetadata(null, OnGroupedItemsSourceChanged));

    /// <summary>The text box takes the keyboard when it appears after Ctrl+F switched to its tab (<see cref="GroupPolicyObjectDocumentViewModel.TakeSearchFocusRequest"/>).</summary>
    public static readonly DependencyProperty FocusWhenRequestedProperty = DependencyProperty.RegisterAttached(
        "FocusWhenRequested", typeof(bool), typeof(GpoSettingsUi), new PropertyMetadata(false, OnFocusWhenRequestedChanged));

    /// <summary>The command a double click on the item runs (when it can execute).</summary>
    public static readonly DependencyProperty DoubleClickCommandProperty = DependencyProperty.RegisterAttached(
        "DoubleClickCommand", typeof(ICommand), typeof(GpoSettingsUi), new PropertyMetadata(null, OnDoubleClickCommandChanged));

    public static string? GetGroupBy(DependencyObject element) => (string?)element.GetValue(GroupByProperty);

    public static void SetGroupBy(DependencyObject element, string? value) => element.SetValue(GroupByProperty, value);

    public static IEnumerable? GetGroupedItemsSource(DependencyObject element) => (IEnumerable?)element.GetValue(GroupedItemsSourceProperty);

    public static void SetGroupedItemsSource(DependencyObject element, IEnumerable? value) => element.SetValue(GroupedItemsSourceProperty, value);

    public static bool GetFocusWhenRequested(DependencyObject element) => (bool)element.GetValue(FocusWhenRequestedProperty);

    public static void SetFocusWhenRequested(DependencyObject element, bool value) => element.SetValue(FocusWhenRequestedProperty, value);

    public static ICommand? GetDoubleClickCommand(DependencyObject element) => (ICommand?)element.GetValue(DoubleClickCommandProperty);

    public static void SetDoubleClickCommand(DependencyObject element, ICommand? value) => element.SetValue(DoubleClickCommandProperty, value);

    private static void OnGroupedItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list)
        {
            return;
        }

        if (e.NewValue is not IEnumerable items)
        {
            list.ItemsSource = null;
            return;
        }

        var view = new ListCollectionView(items as IList ?? items.Cast<object>().ToList());
        foreach (var property in (GetGroupBy(list) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(property));
        }

        list.ItemsSource = view;
    }

    private static void OnFocusWhenRequestedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.Loaded -= OnSearchLoaded;
        if ((bool)e.NewValue)
        {
            box.Loaded += OnSearchLoaded;
        }
    }

    private static void OnSearchLoaded(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.DataContext is GroupPolicyObjectDocumentViewModel document && document.TakeSearchFocusRequest())
        {
            box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (box.IsVisible && box.IsEnabled)
                {
                    box.Focus();
                    Keyboard.Focus(box);
                    box.SelectAll();
                }
            });
        }
    }

    private static void OnDoubleClickCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control)
        {
            return;
        }

        control.MouseDoubleClick -= OnMouseDoubleClick;
        if (e.NewValue is not null)
        {
            control.MouseDoubleClick += OnMouseDoubleClick;
        }
    }

    private static void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is Control control && GetDoubleClickCommand(control) is { } command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
