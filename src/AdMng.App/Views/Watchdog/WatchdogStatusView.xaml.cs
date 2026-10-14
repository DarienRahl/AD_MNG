using System.Windows.Controls;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>The "Status" sub-tab over <see cref="WatchdogStatusViewModel"/> (cards, delivery log, configuration audit).</summary>
public partial class WatchdogStatusView : UserControl
{
    public WatchdogStatusView()
    {
        InitializeComponent();
    }

    /// <summary>The view model (DataContext).</summary>
    public WatchdogStatusViewModel? ViewModel => DataContext as WatchdogStatusViewModel;
}
