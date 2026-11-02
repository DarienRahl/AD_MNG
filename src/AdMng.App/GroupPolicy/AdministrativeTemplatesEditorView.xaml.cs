using System.Windows;
using System.Windows.Controls;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

namespace AdMng.App.GroupPolicy;

/// <summary>The administrative templates editor (K3); the tree selection goes to the view model.</summary>
public partial class AdministrativeTemplatesEditorView : UserControl
{
    public AdministrativeTemplatesEditorView()
    {
        InitializeComponent();
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is AdministrativeTemplatesEditorViewModel editor && e.NewValue is PolicyTreeNodeViewModel node)
        {
            editor.SelectedNode = node;
        }
    }
}
