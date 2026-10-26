using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;

namespace AdMng.App.Views;

/// <summary>
/// Property sheet of one object (modeless). The window only hosts <see cref="PropertySheetViewModel"/>: the pages,
/// fields and the Apply pipeline live in the view model. Code-behind wires what XAML cannot: multi-selection of the
/// membership lists, double click / Enter / Delete on list rows, the lazy "All (nested)" tab and keyboard focus. Enter
/// presses OK (ADUC) except where it has a local meaning: lists open or edit their row, filter boxes move to their list
/// and the Attribute Editor's value editor confirms the value.
/// </summary>
public partial class PropertiesWindow : Window, IDialogResultWindow
{
    // The size the operator gave the last property sheet in this session (memory only, never persisted).
    private static Size? _lastSize;

    private readonly IDialogService _dialogs;
    private readonly WindowViewModelHost _host;

    public PropertiesWindow(PropertySheetViewModel viewModel, IDialogService dialogs)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        DataContext = viewModel;
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        if (_lastSize is { } size)
        {
            Width = size.Width;
            Height = size.Height;
        }

        _host = WindowViewModelHost.Attach(this, viewModel);
        SizeChanged += OnSizeChanged;
    }

    public PropertySheetViewModel ViewModel { get; }

    public bool? Result => _host.Result;

    /// <summary>Completes when the first load of the object finished (tests).</summary>
    internal Task Initialization => _host.Initialization;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded && WindowState == WindowState.Normal)
        {
            _lastSize = e.NewSize;
        }
    }

    /// <summary>
    /// Pages of the same type share one visual tree (the tab content only changes its DataContext), so a field page
    /// would open scrolled to where the previous one was left: start every page at the top.
    /// </summary>
    private void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PageTabs))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(
            () =>
            {
                foreach (var viewer in Descendants<ScrollViewer>(PageTabs).Where(v => v.DataContext is FieldsPageViewModel))
                {
                    viewer.ScrollToTop();
                }
            },
            DispatcherPriority.Loaded);
    }

    // ---- Referenced objects (direct reports) --------------------------------------------------------------------

    private void OnReferenceItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left
            && sender is ListBoxItem { DataContext: DirectoryItem item } container
            && ItemsControl.ItemsControlFromItemContainer(container)?.DataContext is ReferenceListFieldViewModel field
            && field.OpenCommand.CanExecute(item))
        {
            field.OpenCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnReferenceListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && Keyboard.Modifiers == ModifierKeys.None
            && sender is ListBox { DataContext: ReferenceListFieldViewModel field }
            && field.OpenCommand.CanExecute(null))
        {
            // Enter opens the selected object instead of pressing OK.
            field.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- Filter boxes -------------------------------------------------------------------------------------------

    /// <summary>
    /// The filters apply while typing; Enter moves to the filtered list instead of pressing OK (which would apply the
    /// pending changes and close the sheet).
    /// </summary>
    private void OnFilterBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || sender is not DependencyObject box)
        {
            return;
        }

        e.Handled = true;
        for (var scope = VisualTreeHelper.GetParent(box); scope is not null; scope = VisualTreeHelper.GetParent(scope))
        {
            if (Descendants<ListView>(scope).FirstOrDefault(l => l.IsVisible) is { } list)
            {
                FocusList(list);
                return;
            }

            if (scope is TabControl or Window)
            {
                return;
            }
        }
    }

    // ---- Member Of / Members ------------------------------------------------------------------------------------

    private void OnMembershipListLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView { DataContext: MembershipPageViewModel page } list)
        {
            page.SelectedItems = list.SelectedItems;
        }
    }

    private void OnMembershipListUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView { DataContext: MembershipPageViewModel page } list && ReferenceEquals(page.SelectedItems, list.SelectedItems))
        {
            page.SelectedItems = null;
        }
    }

    private void OnMembershipSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { DataContext: MembershipPageViewModel page } list)
        {
            if (!ReferenceEquals(page.SelectedItems, list.SelectedItems))
            {
                page.SelectedItems = list.SelectedItems;
            }

            page.NotifySelectionChanged();
        }

        // Keep the event away from the enclosing tab controls.
        e.Handled = true;
    }

    private void OnMembershipItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left
            && sender is ListViewItem { DataContext: MembershipItemViewModel item } container
            && ItemsControl.ItemsControlFromItemContainer(container)?.DataContext is MembershipPageViewModel page
            && page.OpenCommand.CanExecute(item))
        {
            page.OpenCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnMembershipListKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListView { DataContext: MembershipPageViewModel page })
        {
            return;
        }

        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && page.RemoveCommand.CanExecute(null))
        {
            page.RemoveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && page.OpenCommand.CanExecute(null))
        {
            // Enter opens the selected member (like ADUC) instead of pressing OK.
            page.OpenCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && page.CopyNamesCommand.CanExecute(null))
        {
            // Ctrl+C: the selected names; Ctrl+Shift+C: their distinguished names.
            page.CopyNamesCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && page.CopyDnsCommand.CanExecute(null))
        {
            page.CopyDnsCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- E-mail addresses ---------------------------------------------------------------------------------------

    /// <summary>Ctrl+C on the e-mail addresses: the selected address without its type prefix.</summary>
    private void OnProxyAddressListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C
            && Keyboard.Modifiers == ModifierKeys.Control
            && sender is ListView { DataContext: ProxyAddressesFieldViewModel field }
            && field.CopyAddressCommand.CanExecute(null))
        {
            field.CopyAddressCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnNestedItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is ListViewItem { DataContext: DirectoryItem item })
        {
            _ = _dialogs.ShowPropertiesAsync(item.Dn);
            e.Handled = true;
        }
    }

    private void OnNestedListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && sender is ListView { SelectedItem: DirectoryItem item })
        {
            // Enter opens the selected object instead of pressing OK.
            _ = _dialogs.ShowPropertiesAsync(item.Dn);
            e.Handled = true;
        }
    }

    /// <summary>The "All (nested)" list is loaded the first time its tab is shown.</summary>
    private void OnMembershipTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender))
        {
            return;
        }

        e.Handled = true;
        if (sender is TabControl { SelectedIndex: 1, DataContext: MembershipPageViewModel page }
            && !page.IsNestedLoaded
            && !page.IsNestedLoading
            && page.LoadNestedCommand.CanExecute(null))
        {
            page.LoadNestedCommand.Execute(null);
        }
    }

    // ---- Attribute Editor ---------------------------------------------------------------------------------------

    private void OnAttributeRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left
            && sender is ListViewItem { DataContext: AttributeRowViewModel row } container
            && ItemsControl.ItemsControlFromItemContainer(container)?.DataContext is AttributeEditorPageViewModel page
            && page.EditCommand.CanExecute(row))
        {
            page.EditCommand.Execute(row);
            e.Handled = true;
        }
    }

    private void OnAttributeListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && Keyboard.Modifiers == ModifierKeys.None
            && sender is ListView { DataContext: AttributeEditorPageViewModel page }
            && page.EditCommand.CanExecute(null))
        {
            // Enter edits the selected attribute instead of pressing OK.
            page.EditCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>The value editor opened: its input gets the keyboard focus (it is docked below the list).</summary>
    private void OnValueEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement editor)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(
            () =>
            {
                if (editor.IsVisible
                    && Descendants<Control>(editor).FirstOrDefault(c => c is TextBox or RadioButton or ListBox && c.IsVisible && c.IsEnabled) is { } input)
                {
                    input.Focus();
                }
            },
            DispatcherPriority.Input);
    }

    /// <summary>
    /// The value editor closed (OK, Cancel, Esc): the focused input went with it, so the focus returns to the attribute
    /// list - otherwise the next Enter would press the sheet's OK button.
    /// </summary>
    private void OnValueEditorUnloaded(object sender, RoutedEventArgs e) =>
        _ = Dispatcher.InvokeAsync(
            () =>
            {
                if (IsVisible && !IsKeyboardFocusWithin
                    && Descendants<ListView>(PageTabs).FirstOrDefault(l => l.Name == "AttributeList" && l.IsVisible) is { } list)
                {
                    FocusList(list);
                }
            },
            DispatcherPriority.Background);

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>Focuses the selected row of <paramref name="list"/> (else the first row, else the list).</summary>
    private static void FocusList(ListView list)
    {
        var index = Math.Max(list.SelectedIndex, 0);
        if (list.Items.Count > 0 && list.ItemContainerGenerator.ContainerFromIndex(index) is ListViewItem row)
        {
            row.Focus();
        }
        else
        {
            list.Focus();
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
