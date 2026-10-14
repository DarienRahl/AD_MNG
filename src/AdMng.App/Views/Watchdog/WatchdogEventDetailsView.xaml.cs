using System.Windows.Controls;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>The details pane of the event feed over <see cref="WatchdogEventDetailsViewModel"/>.</summary>
public partial class WatchdogEventDetailsView : UserControl
{
    public WatchdogEventDetailsView()
    {
        InitializeComponent();
    }

    /// <summary>The view model (DataContext).</summary>
    public WatchdogEventDetailsViewModel? ViewModel => DataContext as WatchdogEventDetailsViewModel;
}
