using System.Windows.Controls;

namespace AdMng.App.EventLogs;

/// <summary>"Skąd blokada konta" (DataContext: <see cref="Presentation.EventLogs.LockoutDocumentViewModel"/>).</summary>
public partial class LockoutView : UserControl
{
    public LockoutView()
    {
        InitializeComponent();
    }
}
