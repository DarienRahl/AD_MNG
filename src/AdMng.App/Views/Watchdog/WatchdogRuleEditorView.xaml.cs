using System.Windows.Controls;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>The inline editor of the selected rule over <see cref="WatchdogRuleEditorViewModel"/>.</summary>
public partial class WatchdogRuleEditorView : UserControl
{
    public WatchdogRuleEditorView()
    {
        InitializeComponent();
    }

    /// <summary>The view model (DataContext), or null while no rule is selected.</summary>
    public WatchdogRuleEditorViewModel? ViewModel => DataContext as WatchdogRuleEditorViewModel;
}
