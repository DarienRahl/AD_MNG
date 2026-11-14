using System.Collections;
using System.Text;
using AdMng.Presentation.Feedback;
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
/// the operator's disk, started by the operator, formula-injection safe. Only a full load replaces <see cref="Items"/>
/// (one Reset); live pages, late matches, rows leaving the filter and older pages change it row by row (Add / Remove, in
/// id order, applied in one pass), so the list keeps its scroll position and selection – a Reset re-creates a WPF list's
/// rows and scrolls it back to the top.
/// </summary>
/// <remarks>
/// <para><b>What the feed says</b> (track V, stage V2b-4, unit 4; docs/dev/v-interface-renewal.md "V2b-4 as built",
/// <i>Areas</i> 7). <see cref="Status"/> is the holder of the page; the details pane's holder is in front of it. The summary is
/// <see cref="StatusText"/> with <see cref="SelectionText"/>. The full read and the read of older events are its operations and
/// can be stopped; the acknowledgement of the selected events is a write (no Stop) with the part done. A full read that failed
/// or was stopped leaves the feed empty and its live updates paused until it is read again - that is the state of the page
/// that lasts, with Refresh or Retry; a failed or stopped read of older events leaves the list as it was and is a result that
/// can be closed. The log copies are keys, counts and a result code - never an event, a name or an address.</para>
/// </remarks>
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
    private DateTimeOffset? _observedAt;
    private DateTimeOffset? _serviceUpSince;
    private bool _refreshingPending;
    private bool _disposed;
    private Problem? _problem;
    private bool _stopped;

    public WatchdogEventsViewModel(WatchdogServices services, WatchdogPoller poller)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        Filter = new WatchdogEventFilterViewModel(services);
        Details = new WatchdogEventDetailsViewModel(services);
        Status = services.CreateStatus();
        Status.Front = Details.Status;
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

    /// <summary>What the feed says in the status line: its summary, its reads, its acknowledgements and the state that lasts.</summary>
    public PageStatusHolder Status { get; }

    /// <summary>The rows, newest first (descending ids).</summary>
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

    /// <summary>Loaded, without error and without events (the view shows a hint). A read that was stopped says why the list is empty.</summary>
    public bool IsEmpty => !IsLoading && Error is null && !_stopped && Items.Count == 0;

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
        UpdateIdle();
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
        UpdateLasting();
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
        Status.Operation?.Dispose();
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

        var localizer = _services.Localizer;
        var token = _loads.Next();
        StartNewRange();
        using var scope = Status.Begin(
            localizer.Format(L.WatchdogEventsStatus.ReadingOlder, Items.Count),
            new PageStatusLog(L.WatchdogEventsStatus.ReadingOlder, Items.Count));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, scope.Token, _lifetime.Token);
        IsLoading = true;
        try
        {
            var before = Items.Min(r => r.Id);
            var page = await _services.Client.GetEventsAsync(_liveQuery with { Before = before, Limit = PageSize }, linked.Token).ConfigureAwait(true);
            linked.Token.ThrowIfCancellationRequested();

            // Row by row below the loaded ones: the list stays where the operator scrolled to.
            foreach (var watchEvent in page.Items.Where(e => e.Id < before).OrderByDescending(e => e.Id))
            {
                Items.Add(CreateRow(watchEvent));
            }

            HasMore = page.HasMore;
            Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && scope.IsCancelling && !token.IsCancellationRequested)
        {
            // The operator's Stop: nothing was added, the list is as it was.
            var label = localizer[L.WatchdogUi.LoadOlder];
            scope.CompleteAttention(
                localizer.Format(L.WatchdogEventsStatus.StoppedOlder, label),
                new PageStatusLog(L.WatchdogEventsStatus.StoppedOlder),
                new PageStatusAction(label, LoadOlderCommand));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && (token.IsCancellationRequested || scope.IsEnded || _lifetime.IsCancellationRequested))
        {
            // Superseded by a reload (the client reports a cancelled request as OperationCanceledException or as a
            // WatchdogClientException of kind Cancelled), or the tab was closed: never an error.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFailure(ex);
            scope.CompleteError(
                PageStatusText.Failure(localizer[L.WatchdogEventsStatus.OlderFailed], Error, localizer[L.Feedback.RetryNext], ResultCode.Of(ex)),
                new PageStatusLog(L.WatchdogEventsStatus.OlderFailed).WithCode(ResultCode.Of(ex)),
                new PageStatusAction(localizer[L.Common.Retry], LoadOlderCommand));
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

        var localizer = _services.Localizer;
        var comment = string.IsNullOrWhiteSpace(AcknowledgeComment) ? null : AcknowledgeComment.Trim();
        var total = rows.Count;
        var done = 0;

        // A write to the service: no Stop. The part done is the ring, one request of up to 500 events at a time.
        using var scope = Status.Begin(
            localizer.Format(L.WatchdogEventsStatus.Acknowledging, PageStatusText.Progress(localizer, 0, total)),
            new PageStatusLog(L.WatchdogEventsStatus.Acknowledging, 0, total),
            PageProgress.Of(0, total),
            canCancel: false);
        IsBusy = true;
        try
        {
            foreach (var chunk in rows.Chunk(AcknowledgeBatchSize))
            {
                await _services.Client.AcknowledgeManyAsync(
                    new BulkAcknowledgeRequest { Ids = [.. chunk.Select(r => r.Id)], Comment = comment },
                    _lifetime.Token).ConfigureAwait(true);
                MarkAcknowledged(chunk, comment);
                done += chunk.Length;
                scope.Report(
                    localizer.Format(L.WatchdogEventsStatus.Acknowledging, PageStatusText.Progress(localizer, done, total)),
                    PageProgress.Of(done, total),
                    new PageStatusLog(L.WatchdogEventsStatus.Acknowledging, done, total));
            }

            AcknowledgeComment = string.Empty;
            _services.Ui.Notifications.Show(NotificationSeverity.Success, localizer.Format(L.Watchdog.EventsAcknowledged, rows.Count));

            // The result is the rows themselves (the tick, the counts of the summary): the line goes back to the summary.
            scope.Complete(null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && _lifetime.IsCancellationRequested)
        {
            // Tab closed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportFailure(ex);
            scope.CompleteError(
                PageStatusText.Failure(
                    localizer[L.WatchdogEventsStatus.AckFailed],
                    Error,
                    done > 0 ? localizer.Format(L.WatchdogEventsStatus.AckPartial, done, total) : null,
                    ResultCode.Of(ex)),
                new PageStatusLog(L.WatchdogEventsStatus.AckFailed, done, total).WithCode(ResultCode.Of(ex)));
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
        var localizer = _services.Localizer;
        var token = _loads.Next();
        StartNewRange();
        _poller.Cursor = null;
        _liveQuery = Filter.ToQuery();
        using var scope = Status.Begin(localizer[L.WatchdogEventsStatus.Reading], new PageStatusLog(L.WatchdogEventsStatus.Reading));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, scope.Token, _lifetime.Token);
        IsLoading = true;
        Error = null;
        NewCount = 0;
        _problem = null;
        _stopped = false;
        try
        {
            var requestedAt = _services.Time.GetUtcNow();
            var page = await _services.Client.GetEventsAsync(_liveQuery with { Limit = PageSize }, linked.Token).ConfigureAwait(true);
            linked.Token.ThrowIfCancellationRequested();
            _observedAt = requestedAt;
            var selectedId = SelectedItem?.Id;
            Items.ReplaceAll(page.Items.Select(CreateRow));
            HasMore = page.HasMore;
            SelectedItem = selectedId is { } id ? Items.FirstOrDefault(r => r.Id == id) : null;
            _poller.Cursor = Math.Max(page.LatestId, page.Items.Count == 0 ? 0 : page.Items.Max(e => e.Id));
            _poller.Wake();
            Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && scope.IsCancelling && !token.IsCancellationRequested)
        {
            // The operator's Stop. The feed cannot go on from where it was (the filter may have changed, the live updates wait
            // for the answer of this read), so it is empty and paused until it is read again - the state of the page.
            Items.ReplaceAll([]);
            HasMore = false;
            _stopped = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && (token.IsCancellationRequested || scope.IsEnded || _lifetime.IsCancellationRequested))
        {
            // Superseded by a newer load (the client reports a cancelled request as OperationCanceledException or as a
            // WatchdogClientException of kind Cancelled) or the tab was closed: the newer load owns the list and the line.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Items.ReplaceAll([]);
            HasMore = false;
            ReportFailure(ex);
            _problem = new Problem(Error ?? string.Empty, ResultCode.Of(ex));
        }
        finally
        {
            if (_loads.IsCurrent(token))
            {
                IsLoading = false;

                // The state first, then the operation: the line goes from "working" to what the read left.
                UpdateLasting();
            }

            UpdateStatus();
            ExportCsvCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>
    /// New events from the poller (ascending ids, filtered by the service): inserted newest first at their place in id order
    /// – on top, or between rows when the late-match merge already inserted newer ones – row by row (<see cref="MergeRows"/>);
    /// the feed then keeps at most <see cref="MaxLiveItems"/> rows (or what the operator loaded with "Wczytaj starsze"), the
    /// oldest are dropped and stay reachable through <see cref="LoadOlderCommand"/> (not while an older page loads: that page
    /// continues below the oldest row). Pages are dropped only while a full reload runs (the poller has no cursor then; the
    /// reload reads them itself) – never while an older page loads, because the poller's cursor has already moved past them.
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
        // row, so trimming that row now would leave a gap no later page fills. The next page after it trims instead. The
        // late-match merge reads newest first, not bounded by the cursor: while the poll catches up on a backlog (a tab
        // shown again, a bulk operation) the merge may already have inserted newer events of the filter, so a page goes to
        // its place in id order, not simply on top.
        var (inserted, dropped) = MergeRows(rows, IsLoading ? int.MaxValue : Math.Max(MaxLiveItems, Items.Count));
        if (dropped > 0)
        {
            HasMore = true;
        }

        if (IsScrolledAway)
        {
            NewCount += inserted;
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
        // Since when the service runs, on this clock (also while the tab is hidden: a restart meanwhile counts).
        var uptime = status.Now - status.StartedAt;
        _serviceUpSince = _services.Time.GetUtcNow() - (uptime > TimeSpan.Zero ? uptime : TimeSpan.Zero);
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
    /// <see cref="LateMatchWindow"/> before the last moment the feed saw the service's state (the load or the last complete
    /// read: a tab hidden for hours, rounds that failed and a service restart – see <see cref="AdvanceObservedAt"/> – do not
    /// shorten what is read again) – page by page, newest first, down to the oldest loaded row (the whole window while
    /// everything of the filter is loaded) – and merges each page: events that match only since are inserted at
    /// their place in id order (row by row, <see cref="MergeRows"/>; the live cap applies, trimmed rows stay reachable
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

        var roundStart = _services.Time.GetUtcNow();
        var observed = _observedAt is { } seenAt && seenAt < roundStart ? seenAt : roundStart;
        var since = observed - LateMatchWindow;
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

        AdvanceObservedAt(observed, roundStart);
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

    /// <summary>
    /// After a complete read that started at <paramref name="roundStart"/>: the feed knows the service's state as of then,
    /// so the next round reads from <c>roundStart - LateMatchWindow</c> – except while the service runs for less than
    /// <see cref="LateMatchWindow"/>: after a restart it catches up on the evaluation and attribution of the events it
    /// stored before (however old), so the window stays anchored at <paramref name="observed"/>, the feed's last view
    /// before the restart (or its load).
    /// </summary>
    private void AdvanceObservedAt(DateTimeOffset observed, DateTimeOffset roundStart) =>
        _observedAt = _serviceUpSince is { } upSince && roundStart - upSince < LateMatchWindow ? observed : roundStart;

    /// <summary>The oldest loaded row's id while older events of the filter exist, else null (everything is loaded).</summary>
    private long? Floor() => HasMore && Items.Count > 0 ? Items[^1].Id : null;

    /// <summary>One page of the late-match read: existing rows updated, missing ones inserted in id order (row by row), cap applied.</summary>
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
        var (inserted, dropped) = MergeRows(late, limit);
        if (dropped > 0)
        {
            HasMore = true;
        }

        if (IsScrolledAway)
        {
            NewCount += inserted;
        }

        UpdateStatus();
        ExportCsvCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// After a complete read: the candidates the read did not return no longer match. Unselected rows are removed (one
    /// notification per row), selected ones are marked so that the operator keeps what they are looking at. Returns the
    /// marked rows.
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

        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (removed.Contains(Items[i]))
            {
                Items.RemoveAt(i);
            }
        }

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

    /// <summary>
    /// Inserts <paramref name="rows"/> (descending ids, none of them in the feed) at their place in id order and then keeps
    /// the first <paramref name="limit"/> rows – in one pass, one Add or Remove notification per row (WPF lists take no
    /// multi-row notifications, and a Reset would scroll the list back to the top and lose the selected row's place). A row
    /// whose place is beyond the limit is not inserted at all. Returns how many rows were inserted and how many were left
    /// out or removed because of the limit (they stay reachable with "Wczytaj starsze").
    /// </summary>
    private (int Inserted, int Dropped) MergeRows(IReadOnlyList<WatchdogEventRowViewModel> rows, int limit)
    {
        var inserted = 0;
        var index = 0;
        foreach (var row in rows)
        {
            while (index < Items.Count && Items[index].Id > row.Id)
            {
                index++;
            }

            // Every row above this place is final (later rows are older and go below it).
            if (index >= limit)
            {
                break;
            }

            Items.Insert(index++, row);
            inserted++;
        }

        var dropped = rows.Count - inserted;
        for (var i = Items.Count - 1; i >= limit; i--)
        {
            Items.RemoveAt(i);
            dropped++;
        }

        return (inserted, dropped);
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
            UpdateIdle();
            return;
        }

        var unacknowledged = Items.Count(r => !r.IsAcknowledged);
        var time = TimeZoneInfo.ConvertTime(_services.Time.GetUtcNow(), _services.TimeZone).ToString("T", localizer.Culture);
        StatusText = localizer.Format(L.Watchdog.EventsStatus, Items.Count, unacknowledged, time);
        UpdateIdle();
    }

    /// <summary>The summary of the line: the events, how many are unacknowledged and as of when, then the selection.</summary>
    private void UpdateIdle() => Status.SetIdle(string.Join(" · ", new[] { StatusText, SelectionText }.Where(part => part.Length > 0)));

    /// <summary>
    /// Says the state of the feed that lasts: the read failed (with Retry) or was stopped (with Refresh) - the feed is empty and
    /// its live updates are paused until it is read again. Computed anew whenever a condition changes, so it follows the language.
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(LastingState());

    private PageStatus? LastingState()
    {
        var localizer = _services.Localizer;
        if (_problem is { } problem)
        {
            return PageStatus.Error(
                PageStatusText.Failure(localizer[L.WatchdogEventsStatus.ReadFailed], problem.Text, localizer[L.Feedback.RetryNext], problem.Code),
                new PageStatusLog(L.WatchdogEventsStatus.ReadFailed).WithCode(problem.Code),
                new PageStatusAction(localizer[L.Common.Retry], RefreshCommand));
        }

        if (_stopped)
        {
            var refresh = localizer[L.Commands.Refresh];
            return PageStatus.Attention(
                localizer.Format(L.WatchdogEventsStatus.Stopped, refresh),
                new PageStatusLog(L.WatchdogEventsStatus.Stopped),
                new PageStatusAction(refresh, RefreshCommand));
        }

        return null;
    }

    /// <summary>Why the feed could not be read: the sentence on screen and the result code of the failed call.</summary>
    private sealed record Problem(string Text, ResultCode Code)
    {
        /// <summary>The code only - never the text.</summary>
        public override string ToString() => $"{nameof(Problem)}({Code})";
    }
}
