using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Dialogs;

namespace AdMng.App.Views;

/// <summary>
/// AD Recycle Bin (<see cref="RecycleBinViewModel"/>): the deleted objects of the domain with server side filters, and
/// their restore after a preview. The list's multi-selection is handed to the view model; double-click or Enter opens the
/// restore preview of the selection, Ctrl+C copies the DNs (in CN=Deleted Objects), F5 reloads.
/// </summary>
public partial class RecycleBinWindow : Window, IDialogResultWindow
{
    private readonly WindowViewModelLifecycle _lifecycle;

    public RecycleBinWindow(RecycleBinViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        DataContext = viewModel;
        viewModel.SelectedItems = ItemsList.SelectedItems;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Loaded += (_, _) => NameFilterBox.Focus();
    }

    public RecycleBinViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model finished its first load.</summary>
    public Task Initialization => _lifecycle.Initialization;

    private void OnItemsSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.NotifySelectionChanged();

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: DeletedObjectRowViewModel } && ViewModel.RestoreCommand.CanExecute(null))
        {
            ViewModel.RestoreCommand.Execute(null);
            e.Handled = true;
        }
    }
}
