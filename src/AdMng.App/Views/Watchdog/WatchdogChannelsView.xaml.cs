using System.Windows.Controls;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>The "Channels and SMTP" sub-tab over <see cref="WatchdogChannelsViewModel"/>.</summary>
public partial class WatchdogChannelsView : UserControl
{
    public WatchdogChannelsView()
    {
        InitializeComponent();
    }

    /// <summary>The view model (DataContext).</summary>
    public WatchdogChannelsViewModel? ViewModel => DataContext as WatchdogChannelsViewModel;
}
