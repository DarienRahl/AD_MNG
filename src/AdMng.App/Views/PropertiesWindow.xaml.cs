using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;

namespace AdMng.App.Views;

/// <summary>
/// Property sheet of one object (modeless). The window only hosts <see cref="PropertySheetViewModel"/>: the pages,
/// fields and the Apply pipeline live in the view model. Code-behind wires what XAML cannot: multi-selection of the
/// membership lists, double click / Enter / Delete on list rows and the lazy "All (nested)" tab.
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
    }

    private void OnNestedItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is ListViewItem { DataContext: DirectoryItem item })
        {
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
}
