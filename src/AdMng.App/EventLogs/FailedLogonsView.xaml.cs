using System.Windows.Controls;

namespace AdMng.App.EventLogs;

/// <summary>"Nieudane logowania" (DataContext: <see cref="Presentation.EventLogs.FailedLogonsDocumentViewModel"/>).</summary>
public partial class FailedLogonsView : UserControl
{
    public FailedLogonsView()
    {
        InitializeComponent();
    }
}
