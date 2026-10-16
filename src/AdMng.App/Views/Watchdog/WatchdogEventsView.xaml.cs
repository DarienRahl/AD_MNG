using System.Windows;
using System.Windows.Controls;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>
/// The event feed over <see cref="WatchdogEventsViewModel"/>. The view hands the list's multi-selection to the view model
/// (bulk acknowledge), reports whether the operator scrolled away from the newest events ("N nowych" badge) and scrolls
/// back to the top when asked.
/// </summary>
public partial class WatchdogEventsView : UserControl
{
    private WatchdogEventsViewModel? _viewModel;

    public WatchdogEventsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        EventsList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrolled));
    }

    /// <summary>The view model (DataContext).</summary>
    public WatchdogEventsViewModel? ViewModel => _viewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToTopRequested -= OnScrollToTopRequested;
            _viewModel.SelectedItems = null;
        }

        _viewModel = e.NewValue as WatchdogEventsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.SelectedItems = EventsList.SelectedItems;
            _viewModel.ScrollToTopRequested += OnScrollToTopRequested;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => _viewModel?.NotifySelectionChanged();

    private void OnListScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is not null && e.VerticalChange != 0)
        {
            _viewModel.IsScrolledAway = e.VerticalOffset > 0.5;
        }
    }

    private void OnScrollToTopRequested(object? sender, EventArgs e)
    {
        if (EventsList.Items.Count > 0)
        {
            EventsList.ScrollIntoView(EventsList.Items[0]);
        }
    }
}
