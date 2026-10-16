using System.Windows.Controls;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>The save bar of the configuration sub-tabs over the shared <see cref="WatchdogConfigSession"/>.</summary>
public partial class WatchdogConfigBar : UserControl
{
    public WatchdogConfigBar()
    {
        InitializeComponent();
    }

    /// <summary>The configuration session (DataContext).</summary>
    public WatchdogConfigSession? Session => DataContext as WatchdogConfigSession;
}
