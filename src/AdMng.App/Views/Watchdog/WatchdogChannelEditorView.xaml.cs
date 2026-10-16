using System.Windows.Controls;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>The editor of one Teams or webhook channel over <see cref="ChannelEditorViewModel"/>.</summary>
public partial class WatchdogChannelEditorView : UserControl
{
    public WatchdogChannelEditorView()
    {
        InitializeComponent();
    }

    /// <summary>The view model (DataContext), or null while no channel is selected.</summary>
    public ChannelEditorViewModel? ViewModel => DataContext as ChannelEditorViewModel;
}
