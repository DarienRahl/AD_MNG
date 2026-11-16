using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.PowerShell;

namespace AdMng.App.PowerShell;

/// <summary>
/// The grid of <c>Out-AdmGrid</c> (<see cref="PowerShellGridViewModel"/>): the rows of a pipeline's objects, sorted by a click on a
/// column, filtered, copied (Ctrl+C), exported and opened in the program (Enter or a double-click on a row that has a
/// distinguished name). Every call of the command opens a window of its own.
/// </summary>
public partial class PowerShellGridWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public PowerShellGridWindow(PowerShellGridViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Loaded += (_, _) => ResultsList.Focus();
    }

    public PowerShellGridViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished initializing (it has nothing to load: at once).</summary>
    public Task Initialization => _lifecycle.Initialization;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.SetSelection(ResultsList.SelectedItems);

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: PowerShellGridRow } && ViewModel.OpenCommand.CanExecute(null))
        {
            ViewModel.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
