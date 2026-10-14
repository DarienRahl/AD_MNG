using System.Windows.Controls;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>
/// The AdMng Watchdog document tab over <see cref="WatchdogDocumentViewModel"/>: connection banner, rollout banner,
/// address editor and the sub-tabs. The shell sets <see cref="WatchdogDocumentViewModel.IsVisible"/> when the tab is
/// selected (live polling only while visible).
/// </summary>
public partial class WatchdogView : UserControl
{
    public WatchdogView()
    {
        InitializeComponent();
    }

    /// <summary>The view model (DataContext), or null before the template applied it.</summary>
    public WatchdogDocumentViewModel? ViewModel => DataContext as WatchdogDocumentViewModel;
}
