using System.Windows.Controls;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>The stages and hints of a test send over <see cref="DeliveryTestViewModel"/>.</summary>
public partial class WatchdogDeliveryTestView : UserControl
{
    public WatchdogDeliveryTestView()
    {
        InitializeComponent();
    }

    /// <summary>The view model (DataContext).</summary>
    public DeliveryTestViewModel? ViewModel => DataContext as DeliveryTestViewModel;
}
