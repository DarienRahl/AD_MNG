using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.AccountReview;

namespace AdMng.App.Views;

/// <summary>
/// Account review (<see cref="AccountReviewViewModel"/>, README U2 and U4). On the "Stan kont" tab the columns of the list
/// follow the selected check: they are rebuilt from <see cref="AccountReviewViewModel.Columns"/> (cells bound to the row
/// indexer, headers sort). The "Uprawnienia" tab (<see cref="PrivilegedAccessViewModel"/>) has fixed columns and a details
/// pane. The lists' multi-selection is handed to the view models; double-click or Enter opens the properties, F5 runs the
/// selected tab.
/// </summary>
public partial class AccountReviewWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public AccountReviewWindow(AccountReviewViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        viewModel.SelectedItems = ItemsList.SelectedItems;
        viewModel.Privileges.SelectedItems = PrivilegesList.SelectedItems;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        BuildColumns();
        Loaded += (_, _) => (viewModel.RunCommand.CanExecute(null) ? RunButton : (UIElement)ScopeKindBox).Focus();
    }

    public AccountReviewViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished initializing.</summary>
    public Task Initialization => _lifecycle.Initialization;

    /// <summary>Default width of a column (device independent pixels).</summary>
    internal static double WidthOf(string column) => column switch
    {
        AccountReviewColumns.Name => 230,
        AccountReviewColumns.Login => 150,
        AccountReviewColumns.Location => 260,
        AccountReviewColumns.Problems or AccountReviewColumns.Groups => 280,
        AccountReviewColumns.PasswordDays or AccountReviewColumns.AccountDays or AccountReviewColumns.LogonDays
            or AccountReviewColumns.PasswordAge => 110,
        AccountReviewColumns.Mail or AccountReviewColumns.ExactLastLogon => 220,
        _ => 160,
    };

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountReviewViewModel.Columns))
        {
            BuildColumns();
        }
    }

    /// <summary>One GridView column per column of the selected check; the name column has the icon.</summary>
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
            if (column.Id == AccountReviewColumns.Name)
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

    private void OnPrivilegesSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.Privileges.NotifySelectionChanged();

    private void OnPrivilegeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: PrivilegedAccountRowViewModel row } && ViewModel.Privileges.OpenCommand.CanExecute(row))
        {
            ViewModel.Privileges.OpenCommand.Execute(row);
            e.Handled = true;
        }
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: AccountReviewRowViewModel row } && ViewModel.OpenCommand.CanExecute(row))
        {
            ViewModel.OpenCommand.Execute(row);
            e.Handled = true;
        }
    }
}
