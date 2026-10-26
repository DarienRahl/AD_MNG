using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdMng.App.Behaviors;
using AdMng.Presentation.Lists;

namespace AdMng.App.Views;

/// <summary>Arguments of <see cref="ObjectListView.ItemActivated"/>.</summary>
public sealed class ObjectRowEventArgs(ObjectRowViewModel row) : EventArgs
{
    public ObjectRowViewModel Row { get; } = row;

    /// <summary>Set to suppress the default action (the shell's "open item": navigate into containers, else properties).</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// One document: a virtualized ListView/GridView over an <see cref="ObjectListViewModel"/> with dynamic columns,
/// header sort, quick filter, filter chips, the "Action" context menu, inline rename, Enter/double-click activation and
/// dragging the selection onto navigation tree containers. Reusable outside the main window (e.g. Find results):
/// without a shell view model the shell actions are skipped and <see cref="ItemActivated"/> is the hook.
/// </summary>
public partial class ObjectListView : UserControl
{
    /// <summary>Drag &amp; drop data format: the dragged objects' DNs (string[]).</summary>
    public const string DistinguishedNamesFormat = "AdMng.DistinguishedNames";

    /// <summary>
    /// Drag &amp; drop data format: the id of the connection window the objects come from (a GUID as text). The tree
    /// accepts a drop only from its own connection.
    /// </summary>
    public const string ConnectionIdFormat = "AdMng.ConnectionId";

    /// <summary>Typed characters of the virtual list's type-to-find form one prefix within this time.</summary>
    private const long TypeToFindWindowMs = 1000;

    private ObjectListViewModel? _viewModel;
    private Point? _dragStart;
    private ListViewItem? _pressedItem;
    private bool _selectOnMouseUp;
    private ObjectRowViewModel? _renaming;
    private string _typed = string.Empty;
    private long _typedAt;

    public ObjectListView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrolled));
        List.PreviewTextInput += OnListTextInput;
    }

    /// <summary>Raised on double click / Enter on a row, before the default action.</summary>
    public event EventHandler<ObjectRowEventArgs>? ItemActivated;

    public ObjectListViewModel? ViewModel => _viewModel;

    /// <summary>The list control (focus management, tests).</summary>
    public ListView ListControl => List;

    /// <summary>True while the inline rename editor is open.</summary>
    public bool IsRenaming => _renaming is not null;

    /// <summary>Moves the keyboard focus into the list (to the selected row when there is one).</summary>
    public void FocusList()
    {
        if (List.SelectedItem is { } selected && List.ItemContainerGenerator.ContainerFromItem(selected) is ListViewItem item)
        {
            item.Focus();
        }
        else
        {
            List.Focus();
        }
    }

    /// <summary>Opens the inline editor over the name of <paramref name="row"/> (F2).</summary>
    public void BeginRename(ObjectRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (CommandMenu.FindShell(this) is null)
        {
            return;
        }

        List.ScrollIntoView(row);
        List.UpdateLayout();
        if (List.ItemContainerGenerator.ContainerFromItem(row) is not ListViewItem item)
        {
            return;
        }

        var nameCell = FindDescendant(item, e => e is TextBlock { Tag: "NameCell" }) ?? item;
        var origin = nameCell.TranslatePoint(new Point(0, 0), EditorLayer);
        RenameEditor.MinHeight = 0;
        RenameEditor.Height = Math.Max(item.ActualHeight + 2, 26);
        RenameEditor.Padding = new Thickness(4, 1, 4, 1);
        RenameEditor.Width = Math.Max(nameCell.ActualWidth + 24, 200);
        Canvas.SetLeft(RenameEditor, Math.Max(0, origin.X - 5));
        Canvas.SetTop(RenameEditor, origin.Y + ((nameCell.ActualHeight - RenameEditor.Height) / 2));
        _renaming = row;
        RenameEditor.Text = row.Name;
        RenameEditor.Visibility = Visibility.Visible;
        RenameEditor.Focus();
        RenameEditor.SelectAll();
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, Func<FrameworkElement, bool> match)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && match(element))
            {
                return element;
            }

            if (FindDescendant(child, match) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollIntoViewRequested -= OnScrollIntoViewRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (ReferenceEquals(_viewModel.SelectedItems, List.SelectedItems))
            {
                _viewModel.SelectedItems = null;
            }
        }

        CancelRename();
        _viewModel = e.NewValue as ObjectListViewModel;
        if (_viewModel is not null)
        {
            // The list's live multi-selection (observable) drives the view model's Selection.
            _viewModel.SelectedItems = List.SelectedItems;
            _viewModel.ScrollIntoViewRequested += OnScrollIntoViewRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateTextSearch();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ObjectListViewModel.IsVirtual))
        {
            UpdateTextSearch();
        }
    }

    /// <summary>
    /// WPF's own type-to-find walks the items: fine for loaded lists, but in a virtual list (VLV) it would page through the
    /// whole container - there the typed text goes to the server (<see cref="ObjectListViewModel.FindByPrefixAsync"/>).
    /// </summary>
    private void UpdateTextSearch() => List.IsTextSearchEnabled = _viewModel?.IsVirtual != true;

    private async void OnListTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_viewModel is not { IsVirtual: true } viewModel || IsRenaming || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
        {
            return;
        }

        e.Handled = true;
        var now = Environment.TickCount64;
        _typed = now - _typedAt > TypeToFindWindowMs ? e.Text : _typed + e.Text;
        _typedAt = now;
        if (await viewModel.FindByPrefixAsync(_typed).ConfigureAwait(true))
        {
            FocusList();
        }
    }

    private void OnScrollIntoViewRequested(object? sender, ObjectRowViewModel row) => List.ScrollIntoView(row);

    // ---- Activation --------------------------------------------------------------------------------------------

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is ListViewItem { DataContext: ObjectRowViewModel row })
        {
            Activate(row);
            e.Handled = true;
        }
    }

    private void Activate(ObjectRowViewModel row)
    {
        var args = new ObjectRowEventArgs(row);
        ItemActivated?.Invoke(this, args);
        if (!args.Handled && CommandMenu.FindShell(this) is { } shell && shell.OpenItemCommand.CanExecute(row))
        {
            shell.OpenItemCommand.Execute(row);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (IsRenaming)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Enter && modifiers == ModifierKeys.None && List.SelectedItem is ObjectRowViewModel row)
        {
            Activate(row);
            e.Handled = true;
        }
        else if (e.Key == Key.C && modifiers == ModifierKeys.Control && CommandMenu.FindShell(this) is { } shell
            && shell.CopyRowsCommand.CanExecute(null))
        {
            // The selected rows with the visible columns, tab separated (Ctrl+Shift+C copies the DNs).
            shell.CopyRowsCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.A && modifiers == ModifierKeys.Control && _viewModel?.IsVirtual == true)
        {
            // A virtual list holds only the shown pages: "select all" would select placeholders, not the container.
            e.Handled = true;
        }
    }

    private void OnQuickFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(QuickFilterBox.Text))
        {
            _viewModel?.ClearQuickFilterCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            FocusList();
            e.Handled = true;
        }
    }

    // ---- Drag & drop (selection onto tree containers) ----------------------------------------------------------

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _pressedItem = null;
        _selectOnMouseUp = false;
        if (e.ClickCount != 1 || ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) is not ListViewItem item)
        {
            return;
        }

        _dragStart = e.GetPosition(List);
        _pressedItem = item;
        if (item.IsSelected && List.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Keep the multi-selection for a drag; a plain click selects the row on mouse up.
            _selectOnMouseUp = true;
            e.Handled = true;
        }
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectOnMouseUp && _pressedItem is { DataContext: ObjectRowViewModel row })
        {
            List.SelectedItem = row;
            _pressedItem.Focus();
        }

        _dragStart = null;
        _pressedItem = null;
        _selectOnMouseUp = false;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || IsRenaming || _viewModel is null)
        {
            return;
        }

        var delta = e.GetPosition(List) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStart = null;
        _selectOnMouseUp = false;
        var dns = _viewModel.Selection.Select(r => r.Dn).ToArray();
        if (dns.Length == 0)
        {
            return;
        }

        var data = new DataObject();
        data.SetData(DistinguishedNamesFormat, dns);
        if (Window.GetWindow(this) is { } window && Connections.ConnectionChrome.GetConnectionId(window) is { } connection)
        {
            data.SetData(ConnectionIdFormat, connection.ToString("N"));
        }

        DragDrop.DoDragDrop(List, data, DragDropEffects.Move);
    }

    // ---- Inline rename -----------------------------------------------------------------------------------------

    private void OnRenameEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename(refocus: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename();
            FocusList();
            e.Handled = true;
        }
    }

    // Focus moved elsewhere (a click into the tree, the search box...): commit, but leave the focus where it went.
    private void OnRenameEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitRename(refocus: false);

    private void OnListScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (IsRenaming && (e.VerticalChange != 0 || e.HorizontalChange != 0))
        {
            CommitRename(refocus: RenameEditor.IsKeyboardFocusWithin);
        }
    }

    private async void CommitRename(bool refocus)
    {
        if (_renaming is not { } row)
        {
            return;
        }

        var text = RenameEditor.Text;
        _renaming = null;
        RenameEditor.Visibility = Visibility.Collapsed;
        if (refocus)
        {
            FocusList();
        }

        if (CommandMenu.FindShell(this) is { } shell && !string.Equals(text.Trim(), row.Name, StringComparison.Ordinal))
        {
            // Goes through the change pipeline; failures are shown by the coordinator / an error dialog.
            await shell.RenameAsync(row, text).ConfigureAwait(true);
        }
    }

    private void CancelRename()
    {
        _renaming = null;
        RenameEditor.Visibility = Visibility.Collapsed;
    }
}
