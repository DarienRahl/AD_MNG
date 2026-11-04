using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Computers;

namespace AdMng.App.Computers;

/// <summary>
/// "Przegląd komputerów" (<see cref="ComputerReviewViewModel"/>, K1). The columns of the list follow the selected tile: they
/// are rebuilt from <see cref="ComputerReviewViewModel.Columns"/> (cells bound to the row indexer, headers sort). The
/// list's multi-selection is handed to the view model; double-click or Enter opens the properties, F5 runs the review.
/// </summary>
public partial class ComputerReviewWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public ComputerReviewWindow(ComputerReviewViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        viewModel.SelectedItems = ItemsList.SelectedItems;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        BuildColumns();
        Loaded += (_, _) => (viewModel.RunCommand.CanExecute(null) ? RunButton : (UIElement)ScopeKindBox).Focus();
    }

    public ComputerReviewViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished initializing.</summary>
    public Task Initialization => _lifecycle.Initialization;

    /// <summary>Default width of a column (device independent pixels).</summary>
    internal static double WidthOf(string column) => column switch
    {
        ComputerReviewColumns.Name => 190,
        ComputerReviewColumns.DnsName => 230,
        ComputerReviewColumns.System => 250,
        ComputerReviewColumns.Support or ComputerReviewColumns.Placement or ComputerReviewColumns.Container => 230,
        ComputerReviewColumns.Type or ComputerReviewColumns.LogonDays => 110,
        ComputerReviewColumns.ExactLastLogon or ComputerReviewColumns.Owner or ComputerReviewColumns.Description => 210,
        _ => 150,
    };

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComputerReviewViewModel.Columns))
        {
            BuildColumns();
        }
    }

    /// <summary>One GridView column per column of the selected tile; the name column has the type icon.</summary>
    private void BuildColumns()
    {
        ItemsGrid.Columns.Clear();
        foreach (var column in ViewModel.Columns)
        {
            var header = new GridViewColumnHeader
            {
                Content = column.Header,
                Command = ViewModel.SortCommand,
                CommandParameter = column.Id,
            };
            AutomationProperties.SetName(header, column.Header);
            var gridColumn = new GridViewColumn { Header = header, Width = WidthOf(column.Id) };
            if (column.Id == ComputerReviewColumns.Name)
            {
                gridColumn.CellTemplate = (DataTemplate)FindResource("NameCell");
            }
            else
            {
                gridColumn.DisplayMemberBinding = new Binding($"[{column.Id}]") { Mode = BindingMode.OneWay };
            }

            ItemsGrid.Columns.Add(gridColumn);
        }
    }

    private void OnItemsSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.NotifySelectionChanged();

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: ComputerReviewRowViewModel row } && ViewModel.OpenCommand.CanExecute(row))
        {
            ViewModel.OpenCommand.Execute(row);
            e.Handled = true;
        }
    }
}
