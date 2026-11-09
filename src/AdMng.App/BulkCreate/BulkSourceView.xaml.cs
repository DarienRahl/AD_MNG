using System.Windows.Controls;

namespace AdMng.App.BulkCreate;

/// <summary>Step 1 Dane: paste, file, typed rows, header choice, warnings (docs/dev/phase3-bulk-create.md 2.6). DataContext: <see cref="Presentation.BulkCreate.BulkSourceViewModel"/> (B3).</summary>
public partial class BulkSourceView : UserControl
{
    public BulkSourceView()
    {
        InitializeComponent();
    }
}
