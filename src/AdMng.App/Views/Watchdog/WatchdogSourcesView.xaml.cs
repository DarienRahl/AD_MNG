using System.Windows.Controls;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>The "Sources and retention" sub-tab over <see cref="WatchdogSourcesViewModel"/>.</summary>
public partial class WatchdogSourcesView : UserControl
{
    public WatchdogSourcesView()
    {
        InitializeComponent();
    }

    /// <summary>The view model (DataContext).</summary>
    public WatchdogSourcesViewModel? ViewModel => DataContext as WatchdogSourcesViewModel;
}
