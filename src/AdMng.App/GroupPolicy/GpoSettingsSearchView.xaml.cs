using System.Windows.Controls;
using AdMng.Presentation.GroupPolicy.Search;

namespace AdMng.App.GroupPolicy;

/// <summary>"Wyszukiwarka ustawień GPO" (DataContext: <see cref="GpoSettingsSearchDocumentViewModel"/>).</summary>
public partial class GpoSettingsSearchView : UserControl
{
    public GpoSettingsSearchView()
    {
        InitializeComponent();
    }

    private GpoSettingsSearchDocumentViewModel? ViewModel => DataContext as GpoSettingsSearchDocumentViewModel;

    /// <summary>The multi-selection reaches the view model ("Porównaj GPO" and "Kopiuj do innego GPO…" work on it).</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.SelectedRows = [.. ResultsList.SelectedItems.OfType<GpoSearchRowViewModel>()];
        }
    }
}
