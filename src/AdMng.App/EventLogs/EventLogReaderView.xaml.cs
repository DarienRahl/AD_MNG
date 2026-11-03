using System.Windows.Controls;

namespace AdMng.App.EventLogs;

/// <summary>"Dzienniki zdarzeń" (DataContext: <see cref="Presentation.EventLogs.EventLogReaderDocumentViewModel"/>).</summary>
public partial class EventLogReaderView : UserControl
{
    public EventLogReaderView()
    {
        InitializeComponent();
    }
}
