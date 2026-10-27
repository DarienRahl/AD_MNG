using System.Windows.Controls;
using System.Windows.Input;
using AdMng.Presentation.GroupPolicy;

namespace AdMng.App.GroupPolicy;

/// <summary>"Zasady grupy" of a container (DataContext: <see cref="GroupPolicyScopeDocumentViewModel"/>).</summary>
public partial class GroupPolicyScopeView : UserControl
{
    public GroupPolicyScopeView()
    {
        InitializeComponent();
    }

    /// <summary>A double click on a link opens its GPO.</summary>
    private void OnLinkDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: ScopeLinkRowViewModel row } && DataContext is GroupPolicyScopeDocumentViewModel viewModel
            && viewModel.OpenGpoCommand.CanExecute(row))
        {
            viewModel.OpenGpoCommand.Execute(row);
            e.Handled = true;
        }
    }
}
