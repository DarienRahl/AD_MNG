using System.Windows.Controls;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>The Rules sub-tab over <see cref="WatchdogRulesViewModel"/>: rule list with the inline editor (no new window).</summary>
public partial class WatchdogRulesView : UserControl
{
    public WatchdogRulesView()
    {
        InitializeComponent();
    }

    /// <summary>The view model (DataContext).</summary>
    public WatchdogRulesViewModel? ViewModel => DataContext as WatchdogRulesViewModel;
}
