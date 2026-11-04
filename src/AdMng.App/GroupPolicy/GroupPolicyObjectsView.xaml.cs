using System.Windows.Controls;
using System.Windows.Input;
using AdMng.Presentation.GroupPolicy;

namespace AdMng.App.GroupPolicy;

/// <summary>"Obiekty zasad grupy" (DataContext: <see cref="GroupPolicyObjectsDocumentViewModel"/>).</summary>
public partial class GroupPolicyObjectsView : UserControl
{
    public GroupPolicyObjectsView()
    {
        InitializeComponent();
    }

    private GroupPolicyObjectsDocumentViewModel? ViewModel => DataContext as GroupPolicyObjectsDocumentViewModel;

    /// <summary>The multi-selection reaches the view model (Back Up, Compare work on it).</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.Selection = [.. GpoList.SelectedItems.OfType<GpoRowViewModel>()];
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: GpoRowViewModel row } && ViewModel?.OpenCommand.CanExecute(row) == true)
        {
            ViewModel.OpenCommand.Execute(row);
            e.Handled = true;
        }
    }
}
