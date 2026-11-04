using System.Windows.Controls;
using System.Windows.Input;
using AdMng.Presentation.GroupPolicy;

namespace AdMng.App.GroupPolicy;

/// <summary>"Filtry WMI" (DataContext: <see cref="WmiFiltersDocumentViewModel"/>).</summary>
public partial class WmiFiltersView : UserControl
{
    public WmiFiltersView()
    {
        InitializeComponent();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is WmiFiltersDocumentViewModel viewModel && viewModel.EditCommand.CanExecute(null))
        {
            viewModel.EditCommand.Execute(null);
            e.Handled = true;
        }
    }
}
