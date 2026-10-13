using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdMng.App.Behaviors;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
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
/// </summary>
public partial class MainWindow : Window
{
    private readonly IDialogService _dialogs;
    private readonly ViewLibrary _views;
    private readonly ILocalizer _localizer;
    private GridLength _detailsWidth = new(300);
    private bool _closeConfirmed;
    private bool _confirmingClose;
    private bool _closed;
    private bool _addressPicked;
    private TreeViewItem? _dropTarget;

    public MainWindow(
        ShellViewModel shell,
        NotificationService notifications,
        AppearanceService appearance,
        IDialogService dialogs,
        ViewLibrary views,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(appearance);
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _views = views ?? throw new ArgumentNullException(nameof(views));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));

        InitializeComponent();
        UiLanguage.ApplyTo(this);

        // Menus over other view models get their DataContext before the window's, so they never bind to the shell.
        ThemeMenu.DataContext = appearance;
        LanguageMenu.DataContext = appearance;
        ToastHost.ItemsSource = notifications.Toasts;
        DataContext = shell;

        BuildKeyBindings();
        UpdateDetailsPane();
        FiltersMenuItem.IsChecked = FilterToggle.IsChecked == true;

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

    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (e.Cancel || _closeConfirmed || !Shell.Activities.HasRunning)
        {
            return;
        }

        // Running batches would be interrupted: cancel this close and ask first. The question (a modal dialog) and the
        // second Close() must run after this event: WPF throws when Close() is called while the window is closing.
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
        Shell.FocusRequested -= OnFocusRequested;
        Shell.RenameRequested -= OnRenameRequested;
        Shell.PropertyChanged -= OnShellPropertyChanged;
        Shell.CommandPalette.PropertyChanged -= OnPaletteChanged;
        Shell.Dispose();
        base.OnClosed(e);
    }

    private async void ConfirmClose()
    {
        try
        {
            if (!_closed
                && await _dialogs.ConfirmAsync(_localizer[L.Ui.Exit], _localizer[L.Ui.ExitWhileRunning], destructive: true).ConfigureAwait(true)
                && !_closed)
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    // ---- Keyboard ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Key bindings from the command descriptors. Object commands (Delete, F2, Alt+Enter...) act on the list selection,
    /// so they are bound to the document area only - Delete in the search box or the tree never deletes list objects.
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
            if (string.Equals(descriptor.CategoryKey, L.CommandCategories.Object, StringComparison.Ordinal))
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

        var index = Shell.SelectedDocument is { } current ? Shell.Documents.IndexOf(current) : 0;
        Shell.SelectedDocument = Shell.Documents[(((index + delta) % count) + count) % count];
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
            case nameof(ShellViewModel.IsDetailsPaneVisible):
                UpdateDetailsPane();
                break;
            case nameof(ShellViewModel.SelectedDocument):
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

    private void UpdateDetailsPane()
    {
        var visible = Shell.IsDetailsPaneVisible;
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

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? string.Empty;
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        version = plus > 0 ? version[..plus] : version;
        _ = _dialogs.ShowInfoAsync(_localizer[L.Ui.About], _localizer.Format(L.Ui.AboutText, _localizer[L.App.ShortTitle], version));
    }

    private void OnFilterToggleChanged(object sender, RoutedEventArgs e)
    {
        var show = FilterToggle.IsChecked == true;
        ViewState.SetShowFilterChips(this, show);
        FiltersMenuItem.IsChecked = show;
    }

    private void OnFiltersMenuClick(object sender, RoutedEventArgs e) => FilterToggle.IsChecked = FilterToggle.IsChecked != true;

    // ---- Tabs --------------------------------------------------------------------------------------------------

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ObjectListViewModel document })
        {
            Shell.CloseTabCommand.Execute(document);
            e.Handled = true;
        }
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is TabItem { DataContext: ObjectListViewModel document })
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

    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Like ADUC: right-click selects the node (and opens it), so "New" acts on that container.
        if (ItemFrom(e.OriginalSource) is { DataContext: TreeNodeViewModel node and not PlaceholderNodeViewModel } item)
        {
            item.Focus();
            node.IsSelected = true;
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

    private static string[]? DraggedDns(DragEventArgs e) =>
        e.Data.GetDataPresent(ObjectListView.DistinguishedNamesFormat) ? e.Data.GetData(ObjectListView.DistinguishedNamesFormat) as string[] : null;

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        var item = ItemFrom(e.OriginalSource);
        var allowed = item?.DataContext is ContainerNodeViewModel target
            && DraggedDns(e) is { Length: > 0 } dns
            && Shell.CanDrop(dns, target.Dn);
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(allowed ? item : null);
        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        SetDropTarget(null);
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
