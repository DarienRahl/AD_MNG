using System.Windows.Controls;

namespace AdMng.App.BulkCreate;

/// <summary>The created accounts with their initial passwords in the batch editor's results (2.7). DataContext: <see cref="Presentation.BulkCreate.BulkCreateViewModel"/> (B4).</summary>
public partial class BulkResultsView : UserControl
{
    public BulkResultsView()
    {
        InitializeComponent();
    }
}
