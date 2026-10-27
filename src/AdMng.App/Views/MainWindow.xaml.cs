using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using AdMng.App.Behaviors;
using AdMng.App.Connections;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Views;

namespace AdMng.App.Views;

/// <summary>
/// The ADUC-style main window over <see cref="ShellViewModel"/>: menu, toolbar, address bar, global search, navigation
/// tree, document tabs, details pane, status bar with identity banner and activity center, command palette and toasts.
/// Keyboard shortcuts come from the shell's command descriptors (object commands only while a list has the focus).
/// One main window per connection window (design note docs/dev/multi-window.md): the title starts with the domain, a
/// strip and a badge show the connection colour, closing it ends only its connection.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IDialogService _dialogs;
    private readonly ViewLibrary _views;
    private readonly ILocalizer _localizer;
    private readonly ToolWindows _tools;
    private readonly ConnectionWindowManager? _windows;
    private GridLength _detailsWidth = new(300);
    private bool _closeConfirmed;
    private bool _confirmingClose;
    private bool _closed;
    private bool _addressPicked;
    private TreeViewItem? _dropTarget;
    private TaskCompletionSource<bool>? _closeRequest;

    /// <remarks>
    /// <c>connection</c> and <c>windows</c>: this window's connection and the application's connection windows (null in
    /// hosts without them). The shell is the scope's: a second main window of the same scope, or one over a shell whose
    /// window already closed, fails (<see cref="ShellViewModel.AttachToWindow"/>).
    /// </remarks>
    public MainWindow(
        ShellViewModel shell,
        NotificationService notifications,
        AppearanceService appearance,
        IDialogService dialogs,
        ViewLibrary views,
        ILocalizer localizer,
        ToolWindows tools,
        ConnectionContext? connection = null,
        ConnectionWindowManager? windows = null)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(appearance);
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _views = views ?? throw new ArgumentNullException(nameof(views));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _windows = windows;
        shell.AttachToWindow();
        ConnectionChrome.SetConnectionId(this, connection?.Id ?? shell.Connection?.Id);

        InitializeComponent();
        UiLanguage.ApplyTo(this);

        // Menus over other view models get their DataContext before the window's, so they never bind to the shell.
        ThemeMenu.DataContext = appearance;
        LanguageMenu.DataContext = appearance;
        ToastHost.ItemsSource = notifications.Toasts;
        DataContext = shell;

        BuildKeyBindings();
        UpdateDetailsPane();
        UpdateTaskbar();
        FiltersMenuItem.IsChecked = FilterToggle.IsChecked == true;
        ExitAllMenuItem.Visibility = windows is null ? Visibility.Collapsed : Visibility.Visible;

        shell.FocusRequested += OnFocusRequested;
        shell.RenameRequested += OnRenameRequested;
        shell.PropertyChanged += OnShellPropertyChanged;
        shell.CommandPalette.PropertyChanged += OnPaletteChanged;
    }

    /// <summary>The shell view model (DataContext).</summary>
    public ShellViewModel Shell { get; }

    /// <summary>The list view of the selected document (null when no tab is open or it is not realized yet).</summary>
    public ObjectListView? SelectedListView => FindListView(Shell.SelectedDocument);

    /// <summary>The list view showing <paramref name="document"/>.</summary>
    public ObjectListView? FindListView(ObjectListViewModel? document)
    {
        if (document is null || DocumentHost.ItemContainerGenerator.ContainerFromItem(document) is not ContentPresenter presenter)
        {
            return null;
        }

        return VisualTreeHelper.GetChildrenCount(presenter) > 0 ? VisualTreeHelper.GetChild(presenter, 0) as ObjectListView : null;
    }

    /// <summary>
    /// Closes the window after its questions (running operations, unsaved generated passwords, unsaved tabs). True when it
    /// closed, false when the operator kept it open ("Zakończ program" stops at the first refusal).
    /// </summary>
    public Task<bool> TryCloseAsync()
    {
        if (_closed)
        {
            return Task.FromResult(true);
        }

        _closeRequest ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = _closeRequest.Task;
        Close();
        if (_closed)
        {
            _closeRequest?.TrySetResult(true);
        }

        return request;
    }

    /// <summary>The domain this window works in, for the questions before it closes.</summary>
    private string Domain => Shell.Connection?.Identity?.DomainDnsName ?? string.Empty;

    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (e.Cancel || _closeConfirmed || (!Shell.Activities.HasRunning && !_tools.HasUnsavedGeneratedPasswords && !Shell.HasUnsavedDocuments))
        {
            return;
        }

        // Running batches would be interrupted, generated passwords that were not exported would be lost with the batch
        // editors (owned windows close without asking), and a tab's unsaved changes (the Watchdog configuration) would be
        // discarded: cancel this close and ask first. The question (a modal dialog) and the second Close() must run after
        // this event: WPF throws when Close() is called while the window is closing.
        e.Cancel = true;
        if (!_confirmingClose)
        {
            _confirmingClose = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, ConfirmClose);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _closeRequest?.TrySetResult(true);
        _closeRequest = null;
        Shell.FocusRequested -= OnFocusRequested;
        Shell.RenameRequested -= OnRenameRequested;
        Shell.PropertyChanged -= OnShellPropertyChanged;
        Shell.CommandPalette.PropertyChanged -= OnPaletteChanged;
        Shell.Dispose();
        base.OnClosed(e);
    }

    private async void ConfirmClose()
    {
        var closing = false;
        try
        {
            // Only this connection window closes: the questions name its domain (title, dialog header and sentence).
            var title = _localizer.Format(L.Connections.CloseWindowTitle, Domain);
            if (!_closed
                && (!Shell.Activities.HasRunning
                    || await _dialogs.ConfirmAsync(title, _localizer.Format(L.Shell.CloseWindowWhileRunning, Domain), destructive: true).ConfigureAwait(true))
                && (!_tools.HasUnsavedGeneratedPasswords
                    || await _dialogs.ConfirmAsync(title, _localizer[L.Batch.DiscardPasswords], destructive: true).ConfigureAwait(true))
                && !_closed
                && await Shell.CanCloseDocumentsAsync().ConfigureAwait(true)
                && !_closed)
            {
                _closeConfirmed = true;
                closing = true;
                Close();
            }
        }
        finally
        {
            _confirmingClose = false;
            if (!closing && !_closed)
            {
                // Kept open: "Zakończ program" stops here.
                _closeRequest?.TrySetResult(false);
                _closeRequest = null;
            }
        }
    }

    // ---- Keyboard ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Key bindings from the command descriptors. Object and "Copy as" commands (Delete, F2, Alt+Enter, Ctrl+C...) act on
    /// the list selection, so they are bound to the document area only - Delete in the search box or the tree never
    /// deletes list objects, and Ctrl+C there copies the text box's selection.
    /// </summary>
    private void BuildKeyBindings()
    {
        foreach (var descriptor in Shell.CommandDescriptors)
        {
            if (ShortcutText.TryParse(descriptor.Shortcut) is not { } gesture)
            {
                continue;
            }

            var binding = new KeyBinding(descriptor.Command, gesture) { CommandParameter = descriptor.Parameter };
            if (descriptor.CategoryKey is L.CommandCategories.Object or L.Copy.Category)
            {
                DocumentHost.InputBindings.Add(binding);
            }
            else
            {
                InputBindings.Add(binding);
            }
        }

        // Tab cycling like in browsers.
        InputBindings.Add(new KeyBinding(new RelayUiCommand(() => CycleTab(1)), Key.Tab, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayUiCommand(() => CycleTab(-1)), Key.Tab, ModifierKeys.Control | ModifierKeys.Shift));
    }

    private void CycleTab(int delta)
    {
        var count = Shell.Documents.Count;
        if (count < 2)
        {
            return;
        }

        var index = Shell.SelectedTab is { } current ? Shell.Documents.IndexOf(current) : 0;
        Shell.SelectedTab = Shell.Documents[(((index + delta) % count) + count) % count];
    }

    private void OnFocusRequested(object? sender, ShellFocusTarget target)
    {
        if (target == ShellFocusTarget.Search)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }

        if (AddressBox.Template?.FindName("PART_EditableTextBox", AddressBox) is TextBox editor)
        {
            editor.Focus();
            editor.SelectAll();
        }
        else
        {
            AddressBox.Focus();
        }
    }

    // ---- Shell state -------------------------------------------------------------------------------------------

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            // Settings changes (details pane) may be raised on the thread that updated the settings.
            _ = Dispatcher.BeginInvoke(() => OnShellPropertyChanged(sender, e));
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.IsDetailsPaneShown):
                UpdateDetailsPane();
                break;
            case nameof(ShellViewModel.ConnectionColor) or nameof(ShellViewModel.ConnectionLabel):
                UpdateTaskbar();
                break;
            case nameof(ShellViewModel.SelectedTab):
                // Let the new tab's list take the keyboard once it is realized (unless the operator is typing).
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (Keyboard.FocusedElement is not TextBox && IsActive)
                    {
                        SelectedListView?.FocusList();
                    }
                });
                break;
        }
    }

    /// <summary>
    /// Shows or collapses the details pane and its column (hidden by Ctrl+D and on tabs without directory objects such
    /// as the Watchdog tab); the width the operator dragged it to comes back with it.
    /// </summary>
    private void UpdateDetailsPane()
    {
        var visible = Shell.IsDetailsPaneShown;
        if (!visible && DetailsColumn.Width.Value > 0)
        {
            _detailsWidth = DetailsColumn.Width;
        }

        DetailsPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DetailsSplitter.Visibility = DetailsPane.Visibility;
        DetailsColumn.MinWidth = visible ? 200 : 0;
        DetailsColumn.Width = visible ? _detailsWidth : new GridLength(0);
    }

    private void OnRenameRequested(object? sender, ObjectRowViewModel row) => SelectedListView?.BeginRename(row);

    // ---- Menu / toolbar ----------------------------------------------------------------------------------------

    private void OnConnectClick(object sender, RoutedEventArgs e) => _dialogs.ShowConnectAsync();

    /// <summary>File → "Zamknij okno" (Alt+F4): this connection window only.</summary>
    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>File → "Zakończ program": every connection window, each asking its own questions.</summary>
    private async void OnExitAllClick(object sender, RoutedEventArgs e)
    {
        if (_windows is null)
        {
            Close();
            return;
        }

        await _windows.ExitAllAsync(this).ConfigureAwait(true);
    }

    /// <summary>
    /// The connection badge: the open connection windows (colour dot, domain · account, DC; this one checked) and
    /// "Nowe połączenie w nowym oknie…".
    /// </summary>
    private void OnConnectionBadgeClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ConnectionBadge, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = _localizer[L.Connections.OpenWindows], IsEnabled = false });
        foreach (var window in Shell.OpenWindows)
        {
            var item = new MenuItem
            {
                Header = window.Server.Length > 0 ? $"{window.Label}  ({window.Server})" : window.Label,
                IsChecked = window.IsCurrent,
                Command = Shell.ActivateWindowCommand,
                CommandParameter = window.Id,
                Icon = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = ConnectionChrome.Brush(window.Color) },
            };
            AutomationProperties.SetName(item, window.Label);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var newWindow = new MenuItem { Command = Shell.NewConnectionWindowCommand, InputGestureText = ShortcutText.Format("Ctrl+Shift+N") };
        newWindow.SetBinding(HeaderedItemsControl.HeaderProperty, new Localization.TrBinding(L.Commands.NewConnectionWindow));
        menu.Items.Add(newWindow);
        ConnectionBadge.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>The taskbar button: a dot in the connection colour and the label (best effort; the title is the reliable cue).</summary>
    private void UpdateTaskbar()
    {
        TaskbarItemInfo ??= new TaskbarItemInfo();
        TaskbarItemInfo.Description = Shell.ConnectionLabel;
        var dot = new GeometryDrawing(ConnectionChrome.Brush(Shell.ConnectionColor), new Pen(Brushes.White, 2), new EllipseGeometry(new Point(8, 8), 7, 7));
        var image = new DrawingImage(dot);
        image.Freeze();
        TaskbarItemInfo.Overlay = image;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        _ = _dialogs.ShowInfoAsync(_localizer[L.Ui.About], _localizer.Format(L.Ui.AboutText, _localizer[L.App.ShortTitle], AppVersion.Display));
    }

    private void OnFilterToggleChanged(object sender, RoutedEventArgs e)
    {
        var show = FilterToggle.IsChecked == true;
        ViewState.SetShowFilterChips(this, show);
        FiltersMenuItem.IsChecked = show;
    }

    private void OnFiltersMenuClick(object sender, RoutedEventArgs e) => FilterToggle.IsChecked = FilterToggle.IsChecked != true;

    /// <summary>The toolbar's "Kopiuj jako…" button opens its menu below itself (also with Enter / Space).</summary>
    private void OnCopyAsButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>Tools > PowerShell history (no IDialogService entry: it is a tool window of the WPF shell).</summary>
    private void OnPowerShellHistoryClick(object sender, RoutedEventArgs e) => _tools.ShowPowerShellHistory();

    // ---- Tabs --------------------------------------------------------------------------------------------------

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IShellDocument document })
        {
            Shell.CloseTabCommand.Execute(document);
            e.Handled = true;
        }
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is TabItem { DataContext: IShellDocument document })
        {
            Shell.CloseTabCommand.Execute(document);
            e.Handled = true;
        }
    }

    // ---- Address bar -------------------------------------------------------------------------------------------

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !AddressBox.IsDropDownOpen)
        {
            Shell.NavigateToAddressCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !AddressBox.IsDropDownOpen)
        {
            SelectedListView?.FocusList();
            e.Handled = true;
        }
    }

    private void OnAddressSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _addressPicked |= AddressBox.IsDropDownOpen && AddressBox.SelectedItem is string;

    private void OnAddressDropDownClosed(object? sender, EventArgs e)
    {
        // Navigate only when a recent container was picked (not when the drop-down was just closed).
        if (_addressPicked)
        {
            _addressPicked = false;
            Shell.NavigateToAddressCommand.Execute(null);

            // Clearing the selection keeps the text and lets the same entry be picked again.
            AddressBox.SelectedIndex = -1;
        }
    }

    // ---- Global search -----------------------------------------------------------------------------------------

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        var suggestions = Shell.SearchSuggestions;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Down or Key.Up when suggestions.Count > 0:
                if (!Shell.IsSuggestionsOpen)
                {
                    Shell.IsSuggestionsOpen = true;
                }

                var index = Shell.SelectedSuggestion is { } current ? suggestions.IndexOf(current) : -1;
                index = Math.Clamp(index + (key == Key.Down ? 1 : -1), 0, suggestions.Count - 1);
                Shell.SelectedSuggestion = suggestions[index];
                SuggestionsList.ScrollIntoView(Shell.SelectedSuggestion);
                e.Handled = true;
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Alt:
                Shell.ShowSuggestionPropertiesCommand.Execute(Shell.SelectedSuggestion);
                e.Handled = true;
                break;
            case Key.Enter:
                Shell.SubmitSearchCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when Shell.IsSuggestionsOpen:
                Shell.IsSuggestionsOpen = false;
                e.Handled = true;
                break;
            case Key.Escape:
                Shell.SearchText = string.Empty;
                SelectedListView?.FocusList();
                e.Handled = true;
                break;
        }
    }

    private void OnSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(SuggestionsList, source) is ListBoxItem { DataContext: SearchSuggestionViewModel suggestion })
        {
            Shell.OpenSuggestionCommand.Execute(suggestion);
            e.Handled = true;
        }
    }

    // ---- Command palette ---------------------------------------------------------------------------------------

    private void OnPaletteChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommandPaletteViewModel.IsOpen))
        {
            return;
        }

        if (Shell.CommandPalette.IsOpen)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                PaletteInput.Focus();
                PaletteInput.SelectAll();
            });
        }
        else if (PaletteLayer.IsKeyboardFocusWithin)
        {
            SelectedListView?.FocusList();
        }
    }

    private void OnPaletteKeyDown(object sender, KeyEventArgs e)
    {
        var palette = Shell.CommandPalette;
        switch (e.Key)
        {
            case Key.Down:
                palette.MoveSelectionCommand.Execute(1);
                PaletteList.ScrollIntoView(palette.SelectedResult);
                e.Handled = true;
                break;
            case Key.Up:
                palette.MoveSelectionCommand.Execute(-1);
                PaletteList.ScrollIntoView(palette.SelectedResult);
                e.Handled = true;
                break;
            case Key.Enter:
                if (palette.ExecuteCommand.CanExecute(null))
                {
                    palette.ExecuteCommand.Execute(null);
                }

                e.Handled = true;
                break;
            case Key.Escape:
                palette.CloseCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteItemClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(PaletteList, source) is ListBoxItem { DataContext: CommandPaletteItem item })
        {
            Shell.CommandPalette.ExecuteCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnPaletteBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PaletteLayer))
        {
            Shell.CommandPalette.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- Navigation tree: context menu, drop target ------------------------------------------------------------

    private static TreeViewItem? ItemFrom(object source)
    {
        for (var current = source as DependencyObject; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : null)
        {
            if (current is TreeViewItem item)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>True when the click went to a node's expander (it toggles the node, it does not open it).</summary>
    private static bool IsOnExpander(object source)
    {
        for (var current = source as DependencyObject;
             current is not null and not TreeViewItem;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : null)
        {
            if (current is System.Windows.Controls.Primitives.ToggleButton)
            {
                return true;
            }
        }

        return false;
    }

    private void OnTreeLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A click on the node that is already selected changes no selection and would open nothing - but the tab may
        // show something else by now (search results, an edited view): open it explicitly. The shell ignores a click on
        // the container the tab already shows.
        if (e.ClickCount == 1
            && !IsOnExpander(e.OriginalSource)
            && ItemFrom(e.OriginalSource)?.DataContext is TreeNodeViewModel { IsSelected: true } node
            && ReferenceEquals(node, Shell.Tree.SelectedNode))
        {
            Shell.Tree.ActivateSelected();
        }
    }

    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Like ADUC: right-click selects the node (and opens it), so "New" acts on that container.
        if (ItemFrom(e.OriginalSource) is { DataContext: TreeNodeViewModel node and not PlaceholderNodeViewModel } item)
        {
            item.Focus();
            if (node.IsSelected && ReferenceEquals(node, Shell.Tree.SelectedNode))
            {
                Shell.Tree.ActivateSelected();
            }
            else
            {
                node.IsSelected = true;
            }
        }
    }

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = NavigationTree.ContextMenu;
        menu.Items.Clear();
        if (ItemFrom(e.OriginalSource)?.DataContext is TreeNodeViewModel node)
        {
            foreach (var item in TreeMenu.Create(node, Shell, _views, _dialogs))
            {
                menu.Items.Add(item);
            }
        }

        if (menu.Items.Count == 0)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// The dragged DNs - only from a list of THIS connection window. A drag from another connection window (or another
    /// AdMng.exe, which carries no connection id) is refused: DNs of domain A dropped on B's tree would be moved in B.
    /// </summary>
    private string[]? DraggedDns(DragEventArgs e) =>
        e.Data.GetDataPresent(ObjectListView.DistinguishedNamesFormat) && IsFromThisConnection(e)
            ? e.Data.GetData(ObjectListView.DistinguishedNamesFormat) as string[]
            : null;

    private bool IsFromThisConnection(DragEventArgs e) =>
        e.Data.GetDataPresent(ObjectListView.ConnectionIdFormat)
        && e.Data.GetData(ObjectListView.ConnectionIdFormat) is string id
        && Guid.TryParse(id, out var connection)
        && ConnectionChrome.GetConnectionId(this) == connection;

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        var item = ItemFrom(e.OriginalSource);
        var foreign = e.Data.GetDataPresent(ObjectListView.DistinguishedNamesFormat) && !IsFromThisConnection(e);
        var allowed = !foreign
            && item?.DataContext is ContainerNodeViewModel target
            && DraggedDns(e) is { Length: > 0 } dns
            && Shell.CanDrop(dns, target.Dn);
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(allowed ? item : null);
        DropRefusedText.Visibility = foreign ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e)
    {
        SetDropTarget(null);
        DropRefusedText.Visibility = Visibility.Collapsed;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        SetDropTarget(null);
        DropRefusedText.Visibility = Visibility.Collapsed;
        if (ItemFrom(e.OriginalSource)?.DataContext is ContainerNodeViewModel target && DraggedDns(e) is { Length: > 0 } dns)
        {
            e.Handled = true;

            // Confirm and move after the OLE drag loop has ended: a modal dialog inside Drop would keep the drag
            // source (DoDragDrop in the list) blocked, with the drag cursor on screen, until the dialog is closed.
            var targetDn = target.Dn;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => MoveDropped(dns, targetDn));
        }
    }

    private async void MoveDropped(string[] dns, string targetDn) => await Shell.MoveAsync(dns, targetDn).ConfigureAwait(true);

    private void SetDropTarget(TreeViewItem? item)
    {
        if (ReferenceEquals(_dropTarget, item))
        {
            return;
        }

        if (_dropTarget is not null)
        {
            ViewState.SetIsDropTarget(_dropTarget, false);
        }

        _dropTarget = item;
        if (item is not null)
        {
            ViewState.SetIsDropTarget(item, true);
        }
    }

    /// <summary>A command for window-local key bindings.</summary>
    private sealed class RelayUiCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
