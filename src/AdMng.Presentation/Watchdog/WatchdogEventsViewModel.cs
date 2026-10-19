using System.Collections;
using System.Text;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// The event feed: newest first, filtered on the service (<see cref="WatchdogEventFilterViewModel"/>), older pages on
/// demand (<c>before</c>), new events from the poller prepended (a "N nowych" badge while the operator is scrolled away),
/// details of the selected event, bulk acknowledge with a comment and the CSV export – the only way watchdog data reaches
/// the operator's disk, started by the operator, formula-injection safe.
/// </summary>
public sealed partial class WatchdogEventsViewModel : ObservableObject, IDisposable
{
    /// <summary>Events per page.</summary>
    public const int PageSize = 100;

    /// <summary>Most ids per bulk acknowledge request (the service's limit).</summary>
    public const int AcknowledgeBatchSize = 500;

    /// <summary>
    /// Most rows the live feed grows to: older rows are dropped from memory (still reachable with "Wczytaj starsze") so a
    /// tab left open for days in a busy domain does not grow without bound.
    /// </summary>
    public const int MaxLiveItems = 2000;

    /// <summary>Most rows with a pending attribution read again per status round (newest first).</summary>
    public const int PendingRefreshLimit = 20;

    /// <summary>Events per request of the late-match merge (<see cref="MergeLateMatchesAsync"/>; the service's limit).</summary>
    public const int LateMatchPageSize = 500;

    /// <summary>
    /// The late-match window without the service's configuration (not loaded yet, not readable) and its lower bound: the
    /// default attribution retries end 30 minutes after detection, the rule evaluation lags at most 10 minutes.
    /// </summary>
    public static readonly TimeSpan DefaultLateMatchWindow = TimeSpan.FromHours(2);

    /// <summary>
    /// Added to the last attribution retry (<see cref="LateMatchWindowFor"/>): the rules job's evaluation lag (10 min) and
    /// a slow attribution pass.
    /// </summary>
    public static readonly TimeSpan LateMatchAllowance = TimeSpan.FromMinutes(15);

    private readonly WatchdogServices _services;
    private readonly WatchdogPoller _poller;
    private readonly SerialCancellation _loads = new();
    private readonly SerialCancellation _merges = new();
    private readonly CancellationTokenSource _lifetime = new();
    private IList? _selectedItems;
    private Task _loading = Task.CompletedTask;
    private volatile EventQuery _liveQuery = new();
    private int _rangeVersion;
    private bool _refreshingPending;
    private bool _disposed;

    public WatchdogEventsViewModel(WatchdogServices services, WatchdogPoller poller)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        Filter = new WatchdogEventFilterViewModel(services);
        Details = new WatchdogEventDetailsViewModel(services);
        Filter.Changed += OnFilterChanged;
        Details.Acknowledged += OnDetailsAcknowledged;

        // The poller runs on the thread pool: it reads the immutable query of the loaded feed (built on the UI thread by
        // the last full load), never the filter's observable collections.
        _poller.Filter = () => _liveQuery;
        _poller.EventsReceived += OnEventsReceived;
        _poller.StatusReceived += OnStatusReceived;
    }

    /// <summary>A service call failed (the document updates its connection state for connection failures).</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>The view scrolls the list to the newest event.</summary>
    public event EventHandler? ScrollToTopRequested;

    public WatchdogEventFilterViewModel Filter { get; }

    public WatchdogEventDetailsViewModel Details { get; }

    public BulkObservableCollection<WatchdogEventRowViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeSelectedCommand))]
    public partial WatchdogEventRowViewModel? SelectedItem { get; set; }

    /// <summary>The list's multi-selection (set by the view); call <see cref="NotifySelectionChanged"/> when it changes.</summary>
    public IList? SelectedItems
    {
        get => _selectedItems;
        set
        {
            _selectedItems = value;
            NotifySelectionChanged();
        }
    }

    /// <summary>The selected rows (multi-selection, else the single selected row).</summary>
    public IReadOnlyList<WatchdogEventRowViewModel> Selection =>
        _selectedItems is { Count: > 0 } selected
            ? [.. selected.OfType<WatchdogEventRowViewModel>()]
            : SelectedItem is { } single ? [single] : [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadOlderCommand), nameof(RefreshCommand))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadOlderCommand))]
    public partial bool HasMore { get; private set; }

    /// <summary>Localized error of the last load (inline, never modal).</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>"120 zdarzeń · stan na 12:30:05".</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectionText { get; private set; } = string.Empty;

    /// <summary>Loaded, without error and without events (the view shows a hint).</summary>
    public bool IsEmpty => !IsLoading && Error is null && Items.Count == 0;

    /// <summary>The operator scrolled away from the newest events (set by the view).</summary>
    [ObservableProperty]
    public partial bool IsScrolledAway { get; set; }

    /// <summary>Events that arrived while <see cref="IsScrolledAway"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewEventsText), nameof(HasNewEvents))]
    public partial int NewCount { get; private set; }

    public bool HasNewEvents => NewCount > 0;

    /// <summary>"3 nowe" badge text.</summary>
    public string NewEventsText => NewCount > 0 ? _services.Localizer.Format(L.Watchdog.EventsNew, NewCount) : string.Empty;

    /// <summary>Optional comment of the acknowledgement.</summary>
    [ObservableProperty]
    public partial string AcknowledgeComment { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeSelectedCommand), nameof(ExportCsvCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The caller's account (stored with acknowledgements shown before the next reload).</summary>
    public string? Account { get; set; }

    /// <summary>The current load (tests and the view await it).</summary>
    public Task Loading => _loading;

    /// <summary>
    /// How far back (detection time) an event can still start or stop to match a filter on what the service fills in
    /// later: from the service's attribution settings (<see cref="ApplyConfiguration"/>), else
    /// <see cref="DefaultLateMatchWindow"/>.
    /// </summary>
    public TimeSpan LateMatchWindow { get; private set; } = DefaultLateMatchWindow;

    /// <summary>Status-round refreshes (pending attributions, late matches) completed so far (tests wait for the next one).</summary>
    internal int CompletedLateRefreshes { get; private set; }

    /// <summary>
    /// The late-match window for <paramref name="configuration"/>: an event is final once its last attribution retry
    /// (<c>Attribution.PendingRetries</c>, counted from detection) ran – plus the attribution job's poll interval and
    /// <see cref="LateMatchAllowance"/> – and never less than <see cref="DefaultLateMatchWindow"/>. With attribution off
    /// (or no retries) events are final at once: the default applies.
    /// </summary>
    public static TimeSpan LateMatchWindowFor(WatchdogConfiguration? configuration)
    {
        // A stored document may lack sections or list entries (redacted copies keep the attribution settings).
        var attribution = configuration?.Attribution;
        if (attribution is null || !attribution.Enabled || attribution.Source == AttributionSourceKind.None || attribution.PendingRetries is not { Count: > 0 } retries)
        {
            return DefaultLateMatchWindow;
        }

        var last = retries.Max();
        var window = last + (attribution.PollInterval > TimeSpan.Zero ? attribution.PollInterval : TimeSpan.Zero) + LateMatchAllowance;
        return window > DefaultLateMatchWindow ? window : DefaultLateMatchWindow;
    }

    /// <summary>The configuration the service runs (loaded or saved by the tab): sets <see cref="LateMatchWindow"/>.</summary>
    public void ApplyConfiguration(WatchdogConfiguration? configuration) => LateMatchWindow = LateMatchWindowFor(configuration);

    /// <summary>Call after the view's multi-selection changed.</summary>
    public void NotifySelectionChanged()
    {
        var count = Selection.Count;
        SelectionText = count <= 1 ? string.Empty : _services.Localizer.Format(L.Watchdog.EventsSelected, count);
        AcknowledgeSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>(Re)loads the newest page with the current filter; the poller continues from the newest id. Never throws.</summary>
    public Task LoadAsync()
    {
        _loading = LoadCoreAsync();
        return _loading;
    }

    /// <summary>Shows the history of one object (filter by its GUID), from the object history launcher.</summary>
    public Task ShowObjectAsync(Guid objectGuid, string? name)
    {
        Filter.ShowObject(objectGuid, name);
        return _loading;
    }

    /// <summary>An AD session was opened or closed (pickers, "Pokaż w AD Manager", unlock).</summary>
    public void NotifySessionChanged()
    {
        Filter.NotifySessionChanged();
        Details.NotifySessionChanged();
    }

    /// <summary>Texts after a language switch.</summary>
    public void Relocalize()
    {
        Filter.Relocalize();
        foreach (var row in Items)
        {
            row.Refresh();
        }

        Details.Relocalize();
        UpdateStatus();
        NotifySelectionChanged();
        OnPropertyChanged(nameof(NewEventsText));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Filter.Changed -= OnFilterChanged;
        Details.Acknowledged -= OnDetailsAcknowledged;
        _poller.EventsReceived -= OnEventsReceived;
        _poller.StatusReceived -= OnStatusReceived;
        _loads.Cancel();
        _merges.Cancel();
        _lifetime.Cancel();
        Details.Dispose();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync();

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanLoadOlder))]
    private async Task LoadOlderAsync()
    {
        if (Items.Count == 0)
        {
            return;
        }

        var token = _loads.Next();
        StartNewRange();
        IsLoading = true;
        try
        {
            var before = Items.Min(r => r.Id);
            var page = await _services.Client.GetEventsAsync(_liveQuery with { Before = before, Limit = PageSize }, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            Items.AddRange(page.Items.Where(e => e.Id < before).Select(CreateRow));
            HasMore = page.HasMore;
            Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && token.IsCancellationRequested)
        {
            // Superseded by a reload (the client reports a cancelled request as OperationCanceledException or as a
            // WatchdogClientException of kind Cancelled): never an error.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFailure(ex);
        }
        finally
        {
            if (_loads.IsCurrent(token))
            {
                IsLoading = false;
            }

            UpdateStatus();
        }
    }

    private bool CanLoadOlder() => HasMore && !IsLoading;

    /// <summary>Acknowledges the selected unacknowledged events (with the comment), 500 per request.</summary>
    [RelayCommand(CanExecute = nameof(CanAcknowledge))]
    private async Task AcknowledgeSelectedAsync()
    {
        var rows = Selection.Where(r => !r.IsAcknowledged).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var comment = string.IsNullOrWhiteSpace(AcknowledgeComment) ? null : AcknowledgeComment.Trim();
        IsBusy = true;
        try
        {
            foreach (var chunk in rows.Chunk(AcknowledgeBatchSize))
            {
                await _services.Client.AcknowledgeManyAsync(
                    new BulkAcknowledgeRequest { Ids = [.. chunk.Select(r => r.Id)], Comment = comment },
                    _lifetime.Token).ConfigureAwait(true);
                MarkAcknowledged(chunk, comment);
            }

            AcknowledgeComment = string.Empty;
            _services.Ui.Notifications.Show(NotificationSeverity.Success, _services.Localizer.Format(L.Watchdog.EventsAcknowledged, rows.Count));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && _lifetime.IsCancellationRequested)
        {
            // Tab closed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFailure(ex);
        }
        finally
        {
            IsBusy = false;
            UpdateStatus();
        }
    }

    private bool CanAcknowledge() => !IsBusy && Selection.Any(r => !r.IsAcknowledged);

    /// <summary>Writes the loaded events to a CSV file the operator chooses (formula-injection safe).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsvAsync()
    {
        var localizer = _services.Localizer;
        var title = localizer[L.Watchdog.EventsExport];
        var now = TimeZoneInfo.ConvertTime(_services.Time.GetUtcNow(), _services.TimeZone);
        var path = await _services.Ui.Files.SaveFileAsync(
            title,
            $"watchdog-{now:yyyy-MM-dd-HHmm}.csv",
            [new FileFilter(localizer[L.Files.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        string[] headers =
        [
            localizer[L.Watchdog.ColumnTime], localizer[L.Watchdog.ColumnSeverity], localizer[L.Watchdog.ColumnKind],
            localizer[L.Watchdog.ColumnObject], localizer[L.Watchdog.ColumnPath], localizer[L.Details.DistinguishedName],
            localizer[L.Watchdog.ColumnActor], localizer[L.Watchdog.ColumnSource], localizer[L.Watchdog.ColumnAcknowledged],
        ];
        var rows = Items.Select(r => (IReadOnlyList<string>)
        [
            r.TimeText, r.SeverityText, r.KindText, r.ObjectName, r.ObjectPath, r.Event.Object.Dn, r.ActorText, r.SourceText,
            r.IsAcknowledged ? localizer[L.Common.Yes] : localizer[L.Common.No],
        ]).ToList();
        var delimiter = CsvFiles.ListSeparator(_services.Ui.Regional.Culture);
        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                _services.Csv.Write(writer, headers, rows, delimiter, protectFormulas: true);
            }).ConfigureAwait(true);
            _services.Ui.Notifications.Show(NotificationSeverity.Success, localizer.Format(L.Files.Exported, rows.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _services.Dialogs.ShowErrorAsync(title, _services.Ui.Errors.Translate(ex)).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExport() => !IsBusy && Items.Count > 0;

    /// <summary>"N nowych": scroll to the newest events.</summary>
    [RelayCommand]
    private void ShowNew()
    {
        NewCount = 0;
        IsScrolledAway = false;
        ScrollToTopRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedItemChanged(WatchdogEventRowViewModel? value)
    {
        NotifySelectionChanged();
        Details.ShowAsync(value).Forget();
    }

    partial void OnIsScrolledAwayChanged(bool value)
    {
        if (!value)
        {
            NewCount = 0;
            foreach (var row in Items.Where(r => r.IsNew))
            {
                row.IsNew = false;
            }
        }
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(IsEmpty));

    private async Task LoadCoreAsync()
    {
        var token = _loads.Next();
        StartNewRange();
        _poller.Cursor = null;
        _liveQuery = Filter.ToQuery();
        IsLoading = true;
        Error = null;
        NewCount = 0;
        try
        {
            var page = await _services.Client.GetEventsAsync(_liveQuery with { Limit = PageSize }, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            var selectedId = SelectedItem?.Id;
            Items.ReplaceAll(page.Items.Select(CreateRow));
            HasMore = page.HasMore;
            SelectedItem = selectedId is { } id ? Items.FirstOrDefault(r => r.Id == id) : null;
            _poller.Cursor = Math.Max(page.LatestId, page.Items.Count == 0 ? 0 : page.Items.Max(e => e.Id));
            _poller.Wake();
            Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && token.IsCancellationRequested)
        {
            // Superseded by a newer load (the client reports a cancelled request as OperationCanceledException or as a
            // WatchdogClientException of kind Cancelled): the newer load owns the list and the error bar.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Items.ReplaceAll([]);
            HasMore = false;
            ReportFailure(ex);
        }
        finally
        {
            if (_loads.IsCurrent(token))
            {
                IsLoading = false;
            }

            UpdateStatus();
            ExportCsvCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>
    /// New events from the poller (ascending ids, filtered by the service): prepended newest first with one collection
    /// notification per page; the feed then keeps at most <see cref="MaxLiveItems"/> rows (or what the operator loaded
    /// with "Wczytaj starsze"), the oldest are dropped and stay reachable through <see cref="LoadOlderCommand"/> (not
    /// while an older page loads: that page continues below the oldest row). Pages are dropped only while a full reload
    /// runs (the poller has no cursor then; the reload reads them itself) – never while an older page loads, because the
    /// poller's cursor has already moved past them.
    /// </summary>
    private void OnEventsReceived(object? sender, EventPage page)
    {
        if (_disposed || _poller.Cursor is null)
        {
            return;
        }

        var known = Items.Select(r => r.Id).ToHashSet();
        var rows = page.Items
            .Where(e => !known.Contains(e.Id))
            .OrderByDescending(e => e.Id)
            .Select(e =>
            {
                var row = CreateRow(e);
                row.IsNew = IsScrolledAway;
                return row;
            })
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        // While "Wczytaj starsze" runs (IsLoading with a cursor) nothing is trimmed: its page continues below the oldest
        // row, so trimming that row now would leave a gap no later page fills. The next page after it trims instead.
        var limit = Math.Max(MaxLiveItems, Items.Count);
        Items.InsertRange(0, rows);
        if (!IsLoading && Items.TrimTo(limit) > 0)
        {
            HasMore = true;
        }

        if (IsScrolledAway)
        {
            NewCount += rows.Count;
        }

        UpdateStatus();
        ExportCsvCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Every status round of the visible tab: events still waiting for attribution are read again (the attribution engine
    /// fills in actor and source by updating the stored event, the live feed only fetches new ids), so "ustalanie…" is
    /// replaced without a reload – in the row and in the details pane; and events that match the filter only since then
    /// are inserted (<see cref="MergeLateMatchesAsync"/>).
    /// </summary>
    private void OnStatusReceived(object? sender, StatusDto status)
    {
        if (_disposed || !_poller.IsVisible || _poller.Cursor is null || _refreshingPending)
        {
            return;
        }

        RefreshLateChangesAsync().Forget();
    }

    private async Task RefreshLateChangesAsync()
    {
        _refreshingPending = true;
        try
        {
            await RefreshPendingAttributionAsync().ConfigureAwait(true);
            await MergeLateMatchesAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Informative only (the tab closed, an event was purged, the service is busy): the next round tries again,
            // load errors of the feed come from its own loads.
        }
        finally
        {
            _refreshingPending = false;
            CompletedLateRefreshes++;
        }
    }

    private async Task RefreshPendingAttributionAsync()
    {
        var pending = Items.Where(r => r.IsActorPending).Take(PendingRefreshLimit).Select(r => r.Id).ToList();
        foreach (var id in pending)
        {
            var details = await _services.Client.GetEventAsync(id, _lifetime.Token).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (details.Event.Attribution?.Status is AttributionStatus.Pending)
            {
                continue;
            }

            if (Items.FirstOrDefault(r => r.Id == id) is { } row)
            {
                UpdateRow(row, details.Event);
            }
            else
            {
                Details.UpdateEvent(details.Event);
            }
        }
    }

    /// <summary>
    /// The service fills in severity and tags (attribution, re-evaluation), the attribution status and actor (and with
    /// them the text on actor, workstation and IP) and the matched rules after it stored an event under its id. With a
    /// filter on any of them the live long-poll skips an event that matches only later (its id is behind the cursor) and
    /// never drops a row that stops matching. So every status round reads the filter's events detected within
    /// <see cref="LateMatchWindow"/> again – page by page, newest first, down to the oldest loaded row (the whole window
    /// while everything of the filter is loaded) – and merges each page: events that match only since are inserted at
    /// their place in id order (one collection notification per page; the live cap applies, trimmed rows stay reachable
    /// with "Wczytaj starsze"), rows whose event changed show the new version (row and details pane). Once the whole range
    /// was read, rows of the window the service no longer returns are removed; a selected one is only marked
    /// (<see cref="WatchdogEventRowViewModel.NoLongerMatches"/>) and removed in the first round after it was deselected.
    /// Memory: one page plus the ids of the range. A reload or "Wczytaj starsze" cancels the merge (they own the list);
    /// nothing is removed after an incomplete read. Acknowledgements are the operators': a row acknowledged meanwhile stays
    /// (shown acknowledged, like the operator's own), an already acknowledged late match is not inserted into an
    /// "unacknowledged only" feed.
    /// </summary>
    private async Task MergeLateMatchesAsync()
    {
        var query = _liveQuery;
        if (!MatchesLate(query) || IsLoading || _disposed || _poller.Cursor is null)
        {
            return;
        }

        var since = _services.Time.GetUtcNow() - LateMatchWindow;
        var from = WholeMilliseconds(query.From is { } start && start > since ? start : since);
        if (query.To is { } to && to < from)
        {
            // Everything the filter can show is final.
            return;
        }

        var range = _rangeVersion;
        var token = _merges.Next();
        var read = query with { From = from, After = null, Before = null, WaitSeconds = 0, Limit = LateMatchPageSize, UnacknowledgedOnly = false };

        // The rows a complete read can prove to no longer match: loaded before the first request (rows the poller adds
        // meanwhile are newer than the read), detected within the window – or marked by an earlier round.
        var candidates = Items.Where(r => r.Event.DetectedAt >= from || r.NoLongerMatches).Select(r => r.Id).ToHashSet();
        var seen = new HashSet<long>();
        var limit = Math.Max(MaxLiveItems, Items.Count);
        long? before = null;
        while (true)
        {
            var page = await _services.Client.GetEventsAsync(read with { Before = before }, token).ConfigureAwait(true);
            if (_disposed || token.IsCancellationRequested || range != _rangeVersion || IsLoading || !ReferenceEquals(query, _liveQuery))
            {
                return;
            }

            // While older events exist below the oldest loaded row, they are not loaded ("Wczytaj starsze" reads them with
            // the filter): the read covers the loaded range only.
            var floor = Floor();
            MergePage([.. page.Items.Where(e => floor is not { } oldest || e.Id >= oldest)], query.UnacknowledgedOnly, seen, limit);
            if (!page.HasMore || page.Items.Count == 0 || (Floor() is { } reached && page.Items.Min(e => e.Id) <= reached))
            {
                break;
            }

            before = page.Items.Min(e => e.Id);
        }

        var marked = RemoveNoLongerMatching(candidates, seen);

        // The marked (selected) rows show why they no longer match: their current version, read one by one.
        foreach (var row in marked.Take(PendingRefreshLimit))
        {
            var details = await _services.Client.GetEventAsync(row.Id, token).ConfigureAwait(true);
            if (_disposed || token.IsCancellationRequested || range != _rangeVersion)
            {
                return;
            }

            UpdateRow(row, details.Event);
        }
    }

    /// <summary>The oldest loaded row's id while older events of the filter exist, else null (everything is loaded).</summary>
    private long? Floor() => HasMore && Items.Count > 0 ? Items[^1].Id : null;

    /// <summary>One page of the late-match read: existing rows updated, missing ones inserted in id order, cap applied.</summary>
    private void MergePage(IReadOnlyList<WatchEvent> events, bool unacknowledgedOnly, HashSet<long> seen, int limit)
    {
        if (events.Count == 0)
        {
            return;
        }

        var rows = Items.ToDictionary(r => r.Id);
        var late = new List<WatchdogEventRowViewModel>();
        foreach (var watchEvent in events)
        {
            seen.Add(watchEvent.Id);
            if (rows.TryGetValue(watchEvent.Id, out var row))
            {
                row.NoLongerMatches = false;
                UpdateRow(row, watchEvent);
            }
            else if (!unacknowledgedOnly || watchEvent.AcknowledgedAt is null)
            {
                var created = CreateRow(watchEvent);
                created.IsNew = IsScrolledAway;
                late.Add(created);
            }
        }

        if (late.Count == 0)
        {
            return;
        }

        late.Sort((a, b) => b.Id.CompareTo(a.Id));
        if (late.Count == 1)
        {
            Items.Insert(IndexFor(late[0].Id), late[0]);
        }
        else
        {
            // One Reset notification for the whole page (its rows may fill several gaps); the selection is kept (same row
            // objects).
            Items.ReplaceAll(MergeDescending(Items, late));
        }

        if (Items.TrimTo(limit) > 0)
        {
            HasMore = true;
        }

        if (IsScrolledAway)
        {
            var oldest = Items.Count == 0 ? long.MaxValue : Items[^1].Id;
            NewCount += late.Count(r => r.Id >= oldest);
        }

        UpdateStatus();
        ExportCsvCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// After a complete read: the candidates the read did not return no longer match. Unselected rows are removed (one
    /// notification), selected ones are marked so that the operator keeps what they are looking at. Returns the marked rows.
    /// </summary>
    private List<WatchdogEventRowViewModel> RemoveNoLongerMatching(HashSet<long> candidates, HashSet<long> seen)
    {
        var selected = new HashSet<WatchdogEventRowViewModel>(Selection);
        if (SelectedItem is { } single)
        {
            selected.Add(single);
        }

        var removed = new HashSet<WatchdogEventRowViewModel>();
        var marked = new List<WatchdogEventRowViewModel>();
        foreach (var row in Items.Where(r => candidates.Contains(r.Id) && !seen.Contains(r.Id)))
        {
            if (selected.Contains(row))
            {
                row.NoLongerMatches = true;
                marked.Add(row);
            }
            else
            {
                removed.Add(row);
            }
        }

        if (removed.Count == 0)
        {
            return marked;
        }

        Items.RemoveWhere(removed.Contains);
        NewCount = Math.Max(0, NewCount - removed.Count(r => r.IsNew));
        UpdateStatus();
        ExportCsvCommand.NotifyCanExecuteChanged();
        AcknowledgeSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEmpty));
        return marked;
    }

    /// <summary>
    /// Shows a newer version of a row's event (row and details pane) when anything the service changes later differs.
    /// An acknowledgement is never undone: the service never removes one, so a version without it was read before it.
    /// </summary>
    private void UpdateRow(WatchdogEventRowViewModel row, WatchEvent fresh)
    {
        var current = row.Event;
        if (current.AcknowledgedAt is not null && fresh.AcknowledgedAt is null)
        {
            fresh = fresh with { AcknowledgedAt = current.AcknowledgedAt, AcknowledgedBy = current.AcknowledgedBy, AcknowledgeComment = current.AcknowledgeComment };
        }

        if (!LateStateDiffers(current, fresh))
        {
            return;
        }

        var acknowledged = row.IsAcknowledged;
        row.Update(fresh);
        Details.UpdateEvent(fresh);
        if (row.IsAcknowledged != acknowledged)
        {
            AcknowledgeSelectedCommand.NotifyCanExecuteChanged();
            UpdateStatus();
        }
    }

    /// <summary>
    /// True when what the service changes after storing an event differs: severity, tags, matched rules, attribution,
    /// lockout sources and the acknowledgement (the rest of an event is fixed once stored).
    /// </summary>
    private static bool LateStateDiffers(WatchEvent a, WatchEvent b) =>
        a.Severity != b.Severity
        || a.AcknowledgedAt != b.AcknowledgedAt
        || !string.Equals(a.AcknowledgedBy, b.AcknowledgedBy, StringComparison.Ordinal)
        || !string.Equals(a.AcknowledgeComment, b.AcknowledgeComment, StringComparison.Ordinal)
        || !(a.Tags ?? []).SequenceEqual(b.Tags ?? [], StringComparer.Ordinal)
        || !(a.MatchedRules ?? []).SequenceEqual(b.MatchedRules ?? [], StringComparer.Ordinal)
        || AttributionDiffers(a.Attribution, b.Attribution)
        || a.Lockout?.LockoutsToday != b.Lockout?.LockoutsToday
        || a.Lockout?.Sources?.Count != b.Lockout?.Sources?.Count
        || !string.Equals(a.Lockout?.CallerComputer, b.Lockout?.CallerComputer, StringComparison.Ordinal);

    private static bool AttributionDiffers(Attribution? a, Attribution? b)
    {
        if (ReferenceEquals(a, b))
        {
            return false;
        }

        if (a is null || b is null)
        {
            return true;
        }

        return a.Status != b.Status
            || a.Confidence != b.Confidence
            || a.SourceConfidence != b.SourceConfidence
            || a.ResolvedAt != b.ResolvedAt
            || !string.Equals(a.Reason, b.Reason, StringComparison.Ordinal)
            || !string.Equals(a.Actor?.Sid, b.Actor?.Sid, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(a.Actor?.Account, b.Actor?.Account, StringComparison.Ordinal)
            || !string.Equals(a.Actor?.DisplayName, b.Actor?.DisplayName, StringComparison.Ordinal)
            || !string.Equals(a.Workstation, b.Workstation, StringComparison.Ordinal)
            || !string.Equals(a.IpAddress, b.IpAddress, StringComparison.Ordinal)
            || !string.Equals(a.SourceMethod, b.SourceMethod, StringComparison.Ordinal)
            || !string.Equals(a.SourceReason, b.SourceReason, StringComparison.Ordinal)
            || a.Candidates?.Count != b.Candidates?.Count
            || a.Evidence?.Count != b.Evidence?.Count;
    }

    /// <summary>Index of the first row older than <paramref name="id"/> (rows are in descending id order).</summary>
    private int IndexFor(long id)
    {
        int low = 0, high = Items.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (Items[middle].Id > id)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Both lists in descending id order (no common ids) merged into one.</summary>
    private static List<WatchdogEventRowViewModel> MergeDescending(IReadOnlyList<WatchdogEventRowViewModel> rows, List<WatchdogEventRowViewModel> late)
    {
        var merged = new List<WatchdogEventRowViewModel>(rows.Count + late.Count);
        int i = 0, j = 0;
        while (i < rows.Count || j < late.Count)
        {
            merged.Add(j >= late.Count || (i < rows.Count && rows[i].Id > late[j].Id) ? rows[i++] : late[j++]);
        }

        return merged;
    }

    /// <summary>The service stores detection times in whole milliseconds: the window starts on one, like its query.</summary>
    private static DateTimeOffset WholeMilliseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Offset);

    /// <summary>A reload or "Wczytaj starsze" replaces or extends the loaded range: a running late-match merge stops.</summary>
    private void StartNewRange()
    {
        _rangeVersion++;
        _merges.Cancel();
    }

    /// <summary>True when <paramref name="query"/> filters on something the service fills in after storing the event.</summary>
    private static bool MatchesLate(EventQuery query) =>
        query.MinSeverity is not null || query.Tags.Count > 0 || query.AttributionStatuses.Count > 0 || query.ActorSid is not null
        || query.RuleId is not null || !string.IsNullOrWhiteSpace(query.Text);

    private void OnFilterChanged(object? sender, EventArgs e) => LoadAsync().Forget();

    private void OnDetailsAcknowledged(object? sender, WatchEvent watchEvent)
    {
        Items.FirstOrDefault(r => r.Id == watchEvent.Id)?.Update(watchEvent);
        AcknowledgeSelectedCommand.NotifyCanExecuteChanged();
        UpdateStatus();
    }

    private void MarkAcknowledged(IEnumerable<WatchdogEventRowViewModel> rows, string? comment)
    {
        var now = _services.Time.GetUtcNow();
        foreach (var row in rows)
        {
            row.Update(row.Event with { AcknowledgedAt = now, AcknowledgedBy = Account, AcknowledgeComment = comment });
        }

        if (Details.Event is { } shown && rows.FirstOrDefault(r => r.Id == shown.Id) is { } updated)
        {
            Details.UpdateEvent(updated.Event);
        }

        AcknowledgeSelectedCommand.NotifyCanExecuteChanged();
    }

    private WatchdogEventRowViewModel CreateRow(WatchEvent watchEvent) => new(watchEvent, _services.Localizer, _services.TimeZone);

    private void ReportFailure(Exception ex)
    {
        Error = _services.Connection.Describe(ex);
        Failed?.Invoke(this, ex);
    }

    private void UpdateStatus()
    {
        var localizer = _services.Localizer;
        if (Items.Count == 0)
        {
            StatusText = string.Empty;
            return;
        }

        var unacknowledged = Items.Count(r => !r.IsAcknowledged);
        var time = TimeZoneInfo.ConvertTime(_services.Time.GetUtcNow(), _services.TimeZone).ToString("T", localizer.Culture);
        StatusText = localizer.Format(L.Watchdog.EventsStatus, Items.Count, unacknowledged, time);
    }
}
