using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AdMng.App.Infrastructure;
using AdMng.App.Localization;
using AdMng.App.Services;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Localization;

namespace AdMng.App.Views;

/// <summary>
/// Batch editor and CSV import (<see cref="BatchEditorViewModel"/>). Code-behind only where XAML cannot reach: the
/// "Add operation" menu, the password boxes of password-reset operations (handed over as <see cref="PasswordBoxInput"/>),
/// the CSV preview columns (one per column of the file) and Space to include/exclude the selected preview rows.
/// </summary>
public partial class BatchEditorWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public BatchEditorWindow(BatchEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        BuildAddOperationMenu();
        viewModel.Operations.CollectionChanged += OnOperationsChanged;
        if (viewModel.CsvImport is { } csv)
        {
            csv.PropertyChanged += OnCsvImportChanged;
            BuildCsvPreviewColumns();
        }
    }

    public BatchEditorViewModel ViewModel { get; }

    /// <summary>True when the window was closed after an execution (see <see cref="BatchEditorViewModel.CloseCommand"/>).</summary>
    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished loading (targets, samples, pre-planned preview).</summary>
    public Task Initialization => _lifecycle.Initialization;

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.Operations.CollectionChanged -= OnOperationsChanged;
        if (ViewModel.CsvImport is { } csv)
        {
            csv.PropertyChanged -= OnCsvImportChanged;
        }

        // The boxes are gone with the window: the operations must not keep reading them.
        foreach (var reset in ViewModel.Operations.OfType<ResetPasswordEditorViewModel>())
        {
            reset.PasswordInput = null;
            reset.ConfirmPasswordInput = null;
        }

        base.OnClosed(e);
    }

    // ---- Add operation ---------------------------------------------------------------------------------------

    private void BuildAddOperationMenu()
    {
        var menu = AddOperationButton.ContextMenu;
        menu.Items.Clear();
        foreach (var option in ViewModel.OperationTypes)
        {
            menu.Items.Add(new MenuItem
            {
                Header = option.Text,
                ToolTip = option.Description,
                Command = ViewModel.AddOperationCommand,
                CommandParameter = option.Type,
            });
        }
    }

    private void OnAddOperationClick(object sender, RoutedEventArgs e)
    {
        var menu = AddOperationButton.ContextMenu;
        menu.PlacementTarget = AddOperationButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>A new operation is scrolled into view and gets the keyboard focus.</summary>
    private void OnOperationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is not [OperationEditorViewModel added, ..])
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (OperationsList.ItemContainerGenerator.ContainerFromItem(added) is not ContentPresenter container)
            {
                return;
            }

            container.BringIntoView();

            // The first input of the editor (not the move/remove buttons of the card).
            if (IsActive && OperationsList.ItemTemplate?.FindName("EditorBody", container) is UIElement body)
            {
                body.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        });
    }

    // ---- Password-reset operations ---------------------------------------------------------------------------

    private void OnPasswordBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: ResetPasswordEditorViewModel editor } box)
        {
            editor.PasswordInput = new PasswordBoxInput(box);
        }
    }

    private void OnConfirmPasswordBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: ResetPasswordEditorViewModel editor } box)
        {
            editor.ConfirmPasswordInput = new PasswordBoxInput(box);
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: ResetPasswordEditorViewModel editor })
        {
            editor.NotifyPasswordChanged();
        }
    }

    // ---- CSV import ------------------------------------------------------------------------------------------

    private void OnCsvImportChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CsvImportViewModel.Headers) or null or "")
        {
            BuildCsvPreviewColumns();
        }
    }

    /// <summary>Row number plus one column per CSV column (bound to <see cref="CsvPreviewRow.Cells"/>[i]).</summary>
    private void BuildCsvPreviewColumns()
    {
        if (CsvPreviewList.View is not GridView view || ViewModel.CsvImport is not { } csv)
        {
            return;
        }

        view.Columns.Clear();
        var rowColumn = new GridViewColumn
        {
            Width = 64,
            DisplayMemberBinding = new Binding(nameof(CsvPreviewRow.RowNumber)) { Mode = BindingMode.OneWay },
        };
        BindingOperations.SetBinding(rowColumn, GridViewColumn.HeaderProperty, new TrBinding(L.BatchUi.Row));
        view.Columns.Add(rowColumn);
        var headers = csv.Headers;
        for (var i = 0; i < headers.Count; i++)
        {
            view.Columns.Add(new GridViewColumn
            {
                Width = 140,
                Header = new TextBlock { Text = headers[i], TextTrimming = TextTrimming.CharacterEllipsis },
                DisplayMemberBinding = new Binding($"{nameof(CsvPreviewRow.Cells)}[{i}]") { Mode = BindingMode.OneWay },
            });
        }
    }

    // ---- Preview ---------------------------------------------------------------------------------------------

    /// <summary>Space includes or excludes the objects of the selected rows (all of them change together).</summary>
    private void OnPreviewListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is ToggleButton)
        {
            return;
        }

        var objects = PreviewList.SelectedItems.OfType<PreviewRowViewModel>()
            .Select(r => r.Object)
            .Where(o => o.CanToggle)
            .Distinct()
            .ToList();
        if (objects.Count == 0)
        {
            return;
        }

        var include = !objects.All(o => o.IsIncluded);
        foreach (var obj in objects)
        {
            obj.IsIncluded = include;
        }

        e.Handled = true;
    }
}
