using System.Windows.Controls;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>The "Daily digest" sub-tab over <see cref="WatchdogDigestViewModel"/>.</summary>
public partial class WatchdogDigestView : UserControl
{
    public WatchdogDigestView()
    {
        InitializeComponent();
    }

    /// <summary>The view model (DataContext).</summary>
    public WatchdogDigestViewModel? ViewModel => DataContext as WatchdogDigestViewModel;
}
