using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AdMng.Presentation.Watchdog;

namespace AdMng.App.Views.Watchdog;

/// <summary>
/// The event feed over <see cref="WatchdogEventsViewModel"/>. The view hands the list's multi-selection to the view model
/// (bulk acknowledge), reports whether the operator scrolled away from the newest events ("N nowych" badge) and scrolls
/// back to the top when asked. While the operator is scrolled away, the first visible row stays where it is when rows come
/// or go above it (live pages, late matches, rows leaving the filter, the live cap) – the list scrolls by items, so an
/// insert above would otherwise push the rows being read down – and after a full reload (a Reset) the same event is the
/// first visible row again, else the selection is brought into view, else the newest events are shown.
/// </summary>
public partial class WatchdogEventsView : UserControl
{
    private WatchdogEventsViewModel? _viewModel;
    private ScrollViewer? _scroller;
    private WatchdogEventRowViewModel? _anchor;
    private double _anchorFraction;
    private bool _restorePending;
    private bool _resetPending;

    public WatchdogEventsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        EventsList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrolled));
    }

    /// <summary>The view model (DataContext).</summary>
    public WatchdogEventsViewModel? ViewModel => _viewModel;

    /// <summary>The row kept in place while the operator is scrolled away from the newest events (null at the top).</summary>
    internal WatchdogEventRowViewModel? AnchorRow => _anchor;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToTopRequested -= OnScrollToTopRequested;
            _viewModel.Items.CollectionChanged -= OnItemsChanged;
            _viewModel.SelectedItems = null;
        }

        _viewModel = e.NewValue as WatchdogEventsViewModel;
        _anchor = null;
        if (_viewModel is not null)
        {
            _viewModel.SelectedItems = EventsList.SelectedItems;
            _viewModel.ScrollToTopRequested += OnScrollToTopRequested;
            _viewModel.Items.CollectionChanged += OnItemsChanged;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => _viewModel?.NotifySelectionChanged();

    private void OnListScrolled(object sender, ScrollChangedEventArgs e)
    {
        // The list's own scroll viewer (the header row has one of its own).
        if (e.OriginalSource is not ScrollViewer { TemplatedParent: ListView } scroller || e.VerticalChange == 0)
        {
            return;
        }

        _scroller = scroller;
        if (_viewModel is not null)
        {
            _viewModel.IsScrolledAway = e.VerticalOffset > 0.5;
        }

        RememberAnchor(scroller.VerticalOffset);
    }

    /// <summary>The first visible row (the list scrolls by items: the offset is its index), none at the top.</summary>
    private void RememberAnchor(double offset)
    {
        var items = _viewModel?.Items;
        var index = (int)Math.Floor(offset);
        if (items is null || offset < 0.5 || index >= items.Count)
        {
            _anchor = null;
            return;
        }

        _anchor = items[index];
        _anchorFraction = offset - index;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_anchor is null || _viewModel is null)
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _resetPending = true;
        }
        else if (e.OldItems is { } old && old.Contains(_anchor))
        {
            // The row being read left the feed: the one that took its place is read next.
            var items = _viewModel.Items;
            _anchor = items.Count == 0 ? null : items[Math.Min(Math.Max(e.OldStartingIndex, 0), items.Count - 1)];
            _anchorFraction = 0;
        }

        // Once per batch (the view model applies a page in one pass), before the list is laid out and rendered.
        if (!_restorePending)
        {
            _restorePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, RestoreAnchor);
        }
    }

    private void RestoreAnchor()
    {
        var reset = _resetPending;
        _restorePending = false;
        _resetPending = false;
        if (_anchor is not { } anchor || _viewModel is null || _scroller is not { } scroller)
        {
            return;
        }

        var items = _viewModel.Items;
        var index = items.IndexOf(anchor);
        if (index < 0)
        {
            // A reload put new rows in: the same event, if the feed still has it.
            for (var i = 0; i < items.Count && index < 0; i++)
            {
                index = items[i].Id == anchor.Id ? i : -1;
            }
        }

        if (index < 0)
        {
            _anchor = null;
            if (reset && EventsList.SelectedItem is { } selected)
            {
                EventsList.ScrollIntoView(selected);
            }
            else
            {
                scroller.ScrollToTop();
            }

            return;
        }

        _anchor = items[index];
        var target = index + _anchorFraction;
        if (Math.Abs(scroller.VerticalOffset - target) > 0.01)
        {
            scroller.ScrollToVerticalOffset(target);
        }
    }

    private void OnScrollToTopRequested(object? sender, EventArgs e)
    {
        _anchor = null;
        if (EventsList.Items.Count > 0)
        {
            EventsList.ScrollIntoView(EventsList.Items[0]);
        }
    }
}
