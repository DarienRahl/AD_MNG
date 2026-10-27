using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Filters;

namespace AdMng.App.Views;

/// <summary>
/// Saved filters (<see cref="SavedFiltersViewModel"/>, README U4): the list with New, Edit (double click, Enter), Delete,
/// export and import. Modeless, one per connection window (with its identity chrome); the filters themselves are shared by
/// every window.
/// </summary>
public partial class SavedFiltersWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public SavedFiltersWindow(SavedFiltersViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Loaded += (_, _) => (viewModel.Items.Count > 0 ? FiltersList : (UIElement)NewButton).Focus();
    }

    public SavedFiltersViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished initializing.</summary>
    public Task Initialization => _lifecycle.Initialization;

    private void OnFilterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: SavedFilterItemViewModel item } && ViewModel.EditCommand.CanExecute(null))
        {
            ViewModel.SelectedItem = item;
            ViewModel.EditCommand.Execute(null);
            e.Handled = true;
        }
    }
}
