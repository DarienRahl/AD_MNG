using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SortKey = AdMng.Core.Directory.SortKey;

namespace AdMng.Presentation.Lists;

/// <summary>
/// A document showing directory objects: container contents, search results, saved/built-in views and Find
/// results. Results stream in page by page (one dispatcher call per batch), a new load cancels the previous one,
/// and edits refresh only the affected rows. Public members are UI-thread members.
/// </summary>
public sealed partial class ObjectListViewModel : ObservableObject, IDisposable
{
    /// <summary>Rows of the first batch - small so the first objects appear almost immediately.</summary>
    internal const int FirstBatchSize = 50;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan LiveUpdateDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan LayoutSaveDelay = TimeSpan.FromSeconds(1);
    private const int BackgroundSortThreshold = 2000;
    private const int RefreshParallelism = 4;

    /// <summary>
    /// Modified objects that are in scope but not listed (they may match the filter now, e.g. a newly disabled
    /// account in "Disabled users") are re-read up to this number per change batch; larger batches show them on F5.
    /// </summary>
    private const int MaxUnlistedRefresh = 100;

    private readonly ObjectListServices _services;
    private readonly Func<IDirectorySession?> _session;
    private readonly List<ObjectRowViewModel> _allRows = [];
    private readonly Dictionary<string, ObjectRowViewModel> _rowsByDn = new(StringComparer.Ordinal);
    private readonly SerialCancellation _loads = new();
    private readonly NavigationHistory<ListLocation> _history = new();
    private readonly Debouncer _layoutSaver;
    private readonly Debouncer _liveRefresh;
    private readonly HashSet<string> _liveChanged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _liveGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _generation;

    // Incremented whenever rows are added to or removed from _allRows: a background sort of a snapshot is stale when
    // it changed meanwhile (a rename removes one row and adds another - the count alone stays the same).
    private int _rowsVersion;

    // InactiveDays the Inactive chip's title (and an active chip's query) were built with.
    private int _inactiveDays;
    private RowContext? _rowContext;
    private DirectorySearch? _lastSearch;
    private SortKey? _serverSort;
    private string? _localSortAttribute;
    private bool _localSortDescending;
    private string? _pendingSelectionKey;
    private IList? _selectedItems;
    private IAsyncDisposable? _watch;
    private bool _disposed;
    private bool _settingChips;

    /// <param name="services">Shared list services.</param>
    /// <param name="session">
    /// The session the list reads from; null = the current session. A window passes the session it was opened in
    /// (null once it ended), so its list never reads with the identity of a newer session.
    /// </param>
    public ObjectListViewModel(ObjectListServices services, Func<IDirectorySession?>? session = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _session = session ?? (() => services.Sessions.Current);
        _layoutSaver = new Debouncer(services.Time, LayoutSaveDelay);
        _liveRefresh = new Debouncer(services.Time, LiveUpdateDelay);
        _inactiveDays = services.Settings.Current.InactiveDays;
        foreach (var kind in Enum.GetValues<QuickFilterKind>())
        {
            var chip = new FilterChipViewModel(kind, ChipTitle(kind));
            chip.PropertyChanged += OnChipChanged;
            FilterChips.Add(chip);
        }

        Columns.CollectionChanged += (_, _) => OnPropertyChanged(nameof(VisibleColumns));
        services.Changes.Changed += OnDirectoryChanged;
        services.Localizer.CultureChanged += OnCultureChanged;
        services.Settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised after the location changed (navigation, back/forward/up).</summary>
    public event EventHandler? Navigated;

    /// <summary>Raised when the selection changed (either <see cref="SelectedItem"/> or <see cref="SelectedItems"/>).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Asks the view to scroll a row into view (after "locate" / navigation to an object).</summary>
    public event EventHandler<ObjectRowViewModel>? ScrollIntoViewRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Icon))]
    public partial ListLocation? Location { get; private set; }

    public string Title => Location?.Title ?? string.Empty;

    public ObjectIcon Icon => Location?.Icon ?? ObjectIcon.Folder;

    public ObservableCollection<ColumnViewModel> Columns { get; } = [];

    public IEnumerable<ColumnViewModel> VisibleColumns => Columns.Where(c => c.IsVisible);

    /// <summary>Rows shown (loaded rows matching the quick filter, in the current sort order).</summary>
    public BulkObservableCollection<ObjectRowViewModel> Items { get; } = [];

    /// <summary>Server side filter chips (AND-combined, re-query on toggle).</summary>
    public ObservableCollection<FilterChipViewModel> FilterChips { get; } = [];

    /// <summary>Client side filter over loaded rows (name, display name, logon name, description; case/diacritics insensitive).</summary>
    [ObservableProperty]
    public partial string QuickFilterText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullyLoaded), nameof(StatusText), nameof(QuickFilterStatus))]
    [NotifyCanExecuteChangedFor(nameof(CancelLoadCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Rows received from the server (before the quick filter).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(QuickFilterStatus))]
    public partial int LoadedCount { get; private set; }

    /// <summary>Duration of the last completed (or stopped) load.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial TimeSpan? Elapsed { get; private set; }

    /// <summary>When the data was read ("as of 14:35:02") - lists are live snapshots, never cached.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial DateTimeOffset? LoadedAt { get; private set; }

    /// <summary>Localized error of the last load.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullyLoaded), nameof(StatusText))]
    public partial string? Error { get; private set; }

    /// <summary>True when the last load was stopped before all results arrived.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullyLoaded), nameof(StatusText))]
    public partial bool Partial { get; private set; }

    [ObservableProperty]
    public partial ObjectRowViewModel? SelectedItem { get; set; }

    /// <summary>True when every result of the current location is loaded (local sorting is exact).</summary>
    public bool IsFullyLoaded => !IsLoading && !Partial && Error is null && Location is not null;

    /// <summary>Number of loaded rows (before the quick filter).</summary>
    public int TotalCount => _allRows.Count;

    /// <summary>All loaded rows in the current order (before the quick filter).</summary>
    public IReadOnlyList<ObjectRowViewModel> AllRows => _allRows;

    /// <summary>Status bar text: "428 objects · 0.17 s · as of 14:35:02", "Loading… 1500 objects", errors.</summary>
    public string StatusText
    {
        get
        {
            var l = _services.Localizer;
            if (Error is not null)
            {
                return Error;
            }

            if (IsLoading)
            {
                return l.Format(L.List.Loading, LoadedCount);
            }

            if (Partial)
            {
                return l.Format(L.List.Partial, LoadedCount);
            }

            if (LoadedAt is null)
            {
                return string.Empty;
            }

            var seconds = (Elapsed ?? TimeSpan.Zero).TotalSeconds.ToString("0.00", l.Culture);
            return l.Format(L.List.LoadSummary, LoadedCount, seconds, LoadedAt.Value.ToString("T", l.Culture));
        }
    }

    /// <summary>"Filtering 1200 loaded objects…" while loading, "12 of 428 objects match" when filtered.</summary>
    public string QuickFilterStatus
    {
        get
        {
            if (string.IsNullOrWhiteSpace(QuickFilterText))
            {
                return string.Empty;
            }

            return IsLoading
                ? _services.Localizer.Format(L.List.FilteringLoaded, LoadedCount)
                : _services.Localizer.Format(L.List.FilterMatches, Items.Count, _allRows.Count);
        }
    }

    /// <summary>"3 selected" (empty without selection).</summary>
    public string SelectionText
    {
        get
        {
            var count = Selection.Count;
            return count == 0 ? string.Empty : _services.Localizer.Format(L.List.Selected, count);
        }
    }

    /// <summary>
    /// The view's multi-selection (e.g. <c>DataGrid.SelectedItems</c>). If the list implements
    /// <see cref="INotifyCollectionChanged"/>, changes are tracked automatically.
    /// </summary>
    public IList? SelectedItems
    {
        get => _selectedItems;
        set
        {
            if (ReferenceEquals(_selectedItems, value))
            {
                return;
            }

            if (_selectedItems is INotifyCollectionChanged oldObservable)
            {
                oldObservable.CollectionChanged -= OnSelectedItemsChanged;
            }

            _selectedItems = value;
            if (_selectedItems is INotifyCollectionChanged newObservable)
            {
                newObservable.CollectionChanged += OnSelectedItemsChanged;
            }

            OnPropertyChanged();
            RaiseSelectionChanged();
        }
    }

    /// <summary>Snapshot of the selected rows (multi-selection, else <see cref="SelectedItem"/>).</summary>
    public IReadOnlyList<ObjectRowViewModel> Selection
    {
        get
        {
            if (_selectedItems is { Count: > 0 })
            {
                return [.. _selectedItems.OfType<ObjectRowViewModel>()];
            }

            return SelectedItem is null ? [] : [SelectedItem];
        }
    }

    /// <summary>Active server sort (column header click while not fully loaded), null for local sorting.</summary>
    public SortKey? ServerSort => _serverSort;

    /// <summary>The last executed search (for Save as view, diagnostics and tests).</summary>
    public DirectorySearch? LastSearch => _lastSearch;

    /// <summary>The task of the running/last load (tests and "await refresh" scenarios).</summary>
    internal Task CurrentLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Navigates to a location (pushes history) and loads it.</summary>
    /// <param name="location">What to show.</param>
    /// <param name="selectDn">Object to select as soon as it is loaded ("locate"), optional.</param>
    public Task NavigateAsync(ListLocation location, string? selectDn = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        _history.Navigate(location);
        if (selectDn is not null)
        {
            _pendingSelectionKey = DnKey.For(selectDn);
        }

        return ShowLocationAsync(location);
    }

    /// <summary>
    /// Shows a new definition of the current location (e.g. the edited saved view) in its place: loads it without a
    /// new history entry, so Back does not return to the outdated definition.
    /// </summary>
    public Task ReplaceLocationAsync(ListLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        _history.ReplaceCurrent(location);
        return ShowLocationAsync(location);
    }

    /// <summary>Selects the row with this DN as soon as it is loaded (navigation to an object).</summary>
    public void SelectAfterLoad(string distinguishedName)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        var key = DnKey.For(distinguishedName);
        if (_rowsByDn.TryGetValue(key, out var row))
        {
            Select(row);
            return;
        }

        _pendingSelectionKey = key;
    }

    /// <summary>Reloads the location from the directory (cancels a running load).</summary>
    /// <remarks>
    /// Navigation commands allow concurrent executions: their task completes only when the whole list is loaded, and a
    /// new navigation must be possible meanwhile (it cancels the running load) - otherwise F5, Back or a column click
    /// would stay disabled until a 50 000 object container finished streaming.
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task RefreshAsync() => LoadAsync();

    /// <summary>Loads the current location (cancels a running load). Completes when the load ended.</summary>
    public Task LoadAsync()
    {
        CurrentLoad = LoadCoreAsync();
        return CurrentLoad;
    }

    /// <summary>
    /// Re-reads only the given objects (base scope with the list filter) and replaces, adds or removes their rows.
    /// Used after edits and for live updates - never reloads the whole list.
    /// </summary>
    public async Task RefreshObjectsAsync(IEnumerable<string> distinguishedNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        var session = _session();
        var search = _lastSearch;
        var location = Location;
        var context = _rowContext;
        if (session is null || search is null || location is null || context is null)
        {
            return;
        }

        var dns = distinguishedNames.DistinctBy(DnKey.For).ToList();
        if (dns.Count == 0)
        {
            return;
        }

        var generation = _generation;
        var results = await Task.Run(() => ReadObjectsAsync(session, search, location, dns, cancellationToken), cancellationToken)
            .ConfigureAwait(true);
        await _services.Dispatcher.RunAsync(() =>
        {
            if (generation == _generation)
            {
                ApplyRefreshedObjects(results, context, location.PostFilter);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>Sorts by a column: locally when everything is loaded, otherwise re-queries with server side sort.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task SortAsync(ColumnViewModel? column)
    {
        if (column is null)
        {
            return;
        }

        var descending = column.SortDirection == SortDirection.Ascending;
        SetSortIndicator(column.Attribute, descending);
        if (!IsFullyLoaded && ColumnSets.IsServerSortable(column.Attribute) && Location is not null)
        {
            _serverSort = new SortKey(column.Attribute, descending);
            _localSortAttribute = null;
            await LoadAsync().ConfigureAwait(true);
            return;
        }

        _localSortAttribute = column.Attribute;
        _localSortDescending = descending;
        await ApplyLocalSortAsync().ConfigureAwait(true);
    }

    /// <summary>Stops the running load; already loaded rows stay (status "stopped after N objects").</summary>
    [RelayCommand(CanExecute = nameof(IsLoading))]
    public void CancelLoad() => _loads.Cancel();

    [RelayCommand]
    private void ClearQuickFilter() => QuickFilterText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanGoBack), AllowConcurrentExecutions = true)]
    private Task GoBackAsync() => _history.GoBack() is { } previous ? ShowLocationAsync(previous) : Task.CompletedTask;

    private bool CanGoBack() => _history.CanGoBack;

    [RelayCommand(CanExecute = nameof(CanGoForward), AllowConcurrentExecutions = true)]
    private Task GoForwardAsync() => _history.GoForward() is { } next ? ShowLocationAsync(next) : Task.CompletedTask;

    private bool CanGoForward() => _history.CanGoForward;

    [RelayCommand(CanExecute = nameof(CanGoUp), AllowConcurrentExecutions = true)]
    private Task GoUpAsync() => UpLocation() is { } up ? NavigateAsync(up) : Task.CompletedTask;

    private bool CanGoUp() => UpLocation() is not null;

    /// <summary>Column chooser pre-filled with the current columns and all schema attributes of users, computers, groups and contacts.</summary>
    public ColumnChooserViewModel CreateColumnChooser() =>
        new(_services.Localizer, _session()?.Schema, [.. Columns.Select(c => c.Attribute)], Location?.DefaultColumns ?? ColumnSets.Container);

    /// <summary>Applies a column selection, persists it for the location's layout key and reloads when new attributes are needed.</summary>
    public async Task ApplyColumnsAsync(IReadOnlyList<string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Count == 0)
        {
            return;
        }

        var previous = Columns.ToDictionary(c => c.Attribute, StringComparer.OrdinalIgnoreCase);
        var requested = _lastSearch?.Attributes ?? [];
        var needsReload = attributes.Any(a => !ColumnSets.IsPseudo(a) && !requested.Contains(a, StringComparer.OrdinalIgnoreCase));
        ReplaceColumns(attributes.Select(a => (a, previous.TryGetValue(a, out var c) ? c.Width : 0d)));
        if (_localSortAttribute is not null)
        {
            SetSortIndicator(_localSortAttribute, _localSortDescending);
        }
        else if (_serverSort is not null)
        {
            SetSortIndicator(_serverSort.Attribute, _serverSort.Descending);
        }

        await SaveLayoutAsync().ConfigureAwait(true);
        if (needsReload)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Snapshot for CSV export: visible columns and the rows currently shown (after the quick filter).</summary>
    public ListExportSnapshot CreateExportSnapshot() =>
        new([.. VisibleColumns.Select(c => (c.Attribute, c.Header))], [.. Items]);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loads.Cancel();
        _layoutSaver.Cancel();
        _liveRefresh.Cancel();
        _lifetime.Cancel();
        _services.Changes.Changed -= OnDirectoryChanged;
        _services.Localizer.CultureChanged -= OnCultureChanged;
        foreach (var chip in FilterChips)
        {
            chip.PropertyChanged -= OnChipChanged;
        }

        foreach (var column in Columns)
        {
            column.PropertyChanged -= OnColumnChanged;
        }

        _services.Settings.Changed -= OnSettingsChanged;
        SelectedItems = null;
        StopWatchingAsync().Forget();
    }

    partial void OnQuickFilterTextChanged(string value)
    {
        ApplyLocalView();
        OnPropertyChanged(nameof(QuickFilterStatus));
    }

    partial void OnSelectedItemChanged(ObjectRowViewModel? value) => RaiseSelectionChanged();

    private async Task ShowLocationAsync(ListLocation location)
    {
        // A history entry from before a language switch: show its title in the current language.
        var localized = location.Relocalize(_services.Localizer);
        if (!ReferenceEquals(localized, location))
        {
            _history.ReplaceCurrent(localized);
            location = localized;
        }

        var previousKey = Location?.ColumnLayoutKey;
        Location = location;
        if (!string.Equals(previousKey, location.ColumnLayoutKey, StringComparison.Ordinal) || Columns.Count == 0)
        {
            BuildColumns(location);
        }

        _serverSort = location.Sort;
        _localSortAttribute = null;
        SetSortIndicator(location.Sort?.Attribute, location.Sort?.Descending ?? false);
        SetChipsSilently(location.QuickFilters);
        QuickFilterText = string.Empty;
        NotifyNavigationCommands();
        Navigated?.Invoke(this, EventArgs.Empty);
        await LoadAsync().ConfigureAwait(true);
    }

    private async Task LoadCoreAsync()
    {
        var location = Location;
        if (location is null)
        {
            return;
        }

        var token = _loads.Next();
        var generation = ++_generation;

        // The previous search/context describe the previous location: a row refresh started before this load
        // publishes its own search must not insert objects of the old location under the new generation.
        _lastSearch = null;
        _rowContext = null;
        await StopWatchingAsync().ConfigureAwait(true);
        if (generation != _generation || token.IsCancellationRequested)
        {
            // Disposing the old watch yields: a newer load (or Dispose) started meanwhile and owns rows, status,
            // search and watch now - touching them here would empty its list or watch the wrong container.
            return;
        }

        ClearRows();
        Error = null;
        Partial = false;
        IsLoading = true;
        OnPropertyChanged(nameof(TotalCount));
        var started = _services.Time.GetTimestamp();

        var session = _session();
        if (session is null)
        {
            Error = _services.Localizer[L.Errors.NotConnected];
            IsLoading = false;
            return;
        }

        try
        {
            var policy = await _services.Sessions.GetDomainPolicyAsync(token).ConfigureAwait(true);

            // WaitAsync returns an already read (cached) policy even for a cancelled token: a superseded load must not
            // publish its row context and search over the newer load's.
            token.ThrowIfCancellationRequested();
            var context = new RowContext(_services.Localizer, _services.Sessions.Formatter, policy, _services.Time.GetUtcNow());
            _rowContext = context;
            var search = BuildSearch(location, session.Info.DefaultNamingContext, policy.DomainSid);
            _lastSearch = search;
            try
            {
                await StreamAsync(session, search, location.PostFilter, context, generation, token).ConfigureAwait(true);
            }
            catch (DirectoryException ex) when (search.Sort is not null
                && ex.Kind is DirectoryErrorKind.UnsupportedControl or DirectoryErrorKind.UnwillingToPerform)
            {
                // The DC cannot sort this result (no sort control or sort table limit): load unsorted, sort locally.
                _localSortAttribute = search.Sort.Attribute;
                _localSortDescending = search.Sort.Descending;
                _serverSort = null;
                search = search with { Sort = null };
                _lastSearch = search;
                ClearRows();
                await StreamAsync(session, search, location.PostFilter, context, generation, token).ConfigureAwait(true);
            }

            if (generation != _generation)
            {
                return;
            }

            LoadedAt = _services.Time.GetLocalNow();
            Elapsed = _services.Time.GetElapsedTime(started);
            IsLoading = false;
            if (_localSortAttribute is not null)
            {
                await ApplyLocalSortAsync().ConfigureAwait(true);
            }

            await StartWatchingAsync(session, location, search.BaseDn, generation).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation == _generation)
            {
                Partial = true;
                LoadedAt = _services.Time.GetLocalNow();
                Elapsed = _services.Time.GetElapsedTime(started);
            }
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                Error = _services.Errors.Translate(ex);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(QuickFilterStatus));
            }
        }
    }

    private DirectorySearch BuildSearch(ListLocation location, string defaultNamingContext, string? domainSid)
    {
        var settings = _services.Settings.Current;
        var filter = location.Filter;
        if (location.Kind == ListLocationKind.Container && !settings.ShowAdvancedFeatures)
        {
            filter = LdapFilter.And(filter, LdapFilter.Not(LdapFilter.Equal(Attr.ShowInAdvancedViewOnly, "TRUE")));
        }

        var chips = FilterChips.Where(c => c.IsActive).Select(c => c.Kind).ToList();
        if (chips.Count > 0)
        {
            filter = QuickFilters.Combine(filter, chips, _services.Time.GetUtcNow(), settings.InactiveDays, domainSid);
        }

        var attributes = ColumnSets.BaseAttributes
            .Concat(Columns.Where(c => c.IsVisible).Select(c => c.Attribute))
            .Concat(_serverSort is null ? [] : [_serverSort.Attribute])
            .Where(a => !ColumnSets.IsPseudo(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DirectorySearch
        {
            BaseDn = ResolveBaseDn(location.BaseDn, defaultNamingContext),
            Scope = location.Scope,
            Filter = filter,
            Attributes = attributes,
            PageSize = settings.PageSize,
            Sort = _serverSort,
        };
    }

    private static string ResolveBaseDn(string baseDn, string defaultNamingContext) =>
        baseDn.Replace(SavedView.DomainPlaceholder, defaultNamingContext, StringComparison.OrdinalIgnoreCase);

    private bool ChipsRequireLockedPostFilter => FilterChips.Any(c => c.IsActive && c.Kind == QuickFilterKind.Locked);

    private Task StreamAsync(
        IDirectorySession session,
        DirectorySearch search,
        ListPostFilter postFilter,
        RowContext context,
        int generation,
        CancellationToken token)
    {
        var lockedOnly = postFilter == ListPostFilter.LockedOnly || ChipsRequireLockedPostFilter;
        var time = _services.Time;
        return Task.Run(
            async () =>
            {
                var threshold = FirstBatchSize;
                var buffer = new List<ObjectRowViewModel>(threshold);
                var lastFlush = time.GetTimestamp();
                await foreach (var obj in session.SearchAsync(search, token).WithCancellation(token).ConfigureAwait(false))
                {
                    var row = new ObjectRowViewModel(obj, context);
                    if (lockedOnly && row.LockState == LockState.NotLocked)
                    {
                        continue;
                    }

                    buffer.Add(row);
                    if (buffer.Count >= threshold || time.GetElapsedTime(lastFlush) >= FlushInterval)
                    {
                        await FlushAsync(buffer, generation).ConfigureAwait(false);
                        threshold = Math.Max(search.PageSize, FirstBatchSize);
                        buffer = new List<ObjectRowViewModel>(threshold);
                        lastFlush = time.GetTimestamp();
                    }
                }

                if (buffer.Count > 0)
                {
                    await FlushAsync(buffer, generation).ConfigureAwait(false);
                }
            },
            token);
    }

    private Task FlushAsync(List<ObjectRowViewModel> rows, int generation) => _services.Dispatcher.InvokeAsync(() =>
    {
        if (generation == _generation)
        {
            AppendRows(rows);
        }
    });

    private void AppendRows(List<ObjectRowViewModel> rows)
    {
        var added = new List<ObjectRowViewModel>(rows.Count);
        foreach (var row in rows)
        {
            if (_rowsByDn.TryAdd(row.Key, row))
            {
                _allRows.Add(row);
                added.Add(row);
            }
        }

        if (added.Count > 0)
        {
            _rowsVersion++;
        }

        var filter = TextFolding.Fold(QuickFilterText);
        Items.AddRange(filter.Length == 0 ? added : added.Where(r => r.SearchKey.Contains(filter, StringComparison.Ordinal)));
        LoadedCount = _allRows.Count;
        OnPropertyChanged(nameof(TotalCount));
        TrySelectPending(added);
    }

    private void ClearRows()
    {
        _rowsVersion++;
        _allRows.Clear();
        _rowsByDn.Clear();
        Items.ReplaceAll([]);
        LoadedCount = 0;
    }

    private void ApplyLocalView()
    {
        var filter = TextFolding.Fold(QuickFilterText);
        Items.ReplaceAll(filter.Length == 0 ? _allRows : _allRows.Where(r => r.SearchKey.Contains(filter, StringComparison.Ordinal)));
        OnPropertyChanged(nameof(QuickFilterStatus));
    }

    /// <summary>Runs a sort of large lists off the UI thread (a test seam: tests hold it while the rows change).</summary>
    internal Func<Func<IReadOnlyList<ObjectRowViewModel>>, Task<IReadOnlyList<ObjectRowViewModel>>> BackgroundSort { get; set; } =
        sort => Task.Run(sort);

    private async Task ApplyLocalSortAsync()
    {
        while (_localSortAttribute is { } attribute && _allRows.Count >= 2)
        {
            var descending = _localSortDescending;
            var culture = _services.Localizer.Culture;
            var schema = _session()?.Schema;
            var snapshot = _allRows.ToArray();
            var generation = _generation;
            var version = _rowsVersion;
            var sorted = snapshot.Length >= BackgroundSortThreshold
                ? await BackgroundSort(() => RowSorting.Sort(snapshot, attribute, descending, culture, schema)).ConfigureAwait(true)
                : RowSorting.Sort(snapshot, attribute, descending, culture, schema);
            var rowsChanged = false;
            await _services.Dispatcher.RunAsync(() =>
            {
                if (generation != _generation
                    || !string.Equals(attribute, _localSortAttribute, StringComparison.OrdinalIgnoreCase)
                    || descending != _localSortDescending)
                {
                    // A new load or another column click: that one sorts its own rows.
                    return;
                }

                if (version != _rowsVersion)
                {
                    // Rows were added or removed while the snapshot was sorted (live update, rename, delete): applying
                    // it would bring back removed rows and drop new ones - sort the current rows again.
                    rowsChanged = true;
                    return;
                }

                _allRows.Clear();
                _allRows.AddRange(sorted);
                ApplyLocalView();
            }).ConfigureAwait(true);
            if (!rowsChanged)
            {
                return;
            }
        }
    }

    private void SetSortIndicator(string? attribute, bool descending)
    {
        foreach (var column in Columns)
        {
            column.SortDirection = string.Equals(column.Attribute, attribute, StringComparison.OrdinalIgnoreCase)
                ? (descending ? SortDirection.Descending : SortDirection.Ascending)
                : SortDirection.None;
        }
    }

    private static async Task<List<(string Dn, DirectoryObject? Object)>> ReadObjectsAsync(
        IDirectorySession session,
        DirectorySearch listSearch,
        ListLocation location,
        IReadOnlyList<string> dns,
        CancellationToken cancellationToken)
    {
        var results = new (string Dn, DirectoryObject? Object)[dns.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, dns.Count),
            new ParallelOptions { MaxDegreeOfParallelism = RefreshParallelism, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var dn = dns[i];
                DirectoryObject? found = null;
                if (IsInScope(dn, listSearch.BaseDn, location.Scope))
                {
                    var single = listSearch with { BaseDn = dn, Scope = SearchScope.Base, SizeLimit = 1, Sort = null };
                    try
                    {
                        await foreach (var obj in session.SearchAsync(single, ct).ConfigureAwait(false))
                        {
                            found = obj;
                            break;
                        }
                    }
                    catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
                    {
                        found = null;
                    }
                }

                results[i] = (dn, found);
            }).ConfigureAwait(false);
        return [.. results];
    }

    private void ApplyRefreshedObjects(List<(string Dn, DirectoryObject? Object)> results, RowContext context, ListPostFilter postFilter)
    {
        var lockedOnly = postFilter == ListPostFilter.LockedOnly || ChipsRequireLockedPostFilter;
        var selected = Selection.ToHashSet();
        var selectionAffected = false;
        var removed = new HashSet<ObjectRowViewModel>();
        var updated = new HashSet<ObjectRowViewModel>();
        var added = new List<ObjectRowViewModel>();
        foreach (var (dn, obj) in results)
        {
            var key = DnKey.For(dn);
            _rowsByDn.TryGetValue(key, out var existing);
            var keep = obj is not null;
            ObjectRowViewModel? candidate = null;
            selectionAffected |= existing is not null && selected.Contains(existing);
            if (obj is not null)
            {
                if (existing is not null)
                {
                    existing.Update(obj);
                    candidate = existing;
                }
                else
                {
                    candidate = new ObjectRowViewModel(obj, context);
                }

                keep = !lockedOnly || candidate.LockState != LockState.NotLocked;
            }

            if (!keep)
            {
                if (existing is not null)
                {
                    _rowsByDn.Remove(key);
                    removed.Add(existing);
                    ForgetSelected(existing);
                }

                continue;
            }

            if (existing is null && candidate is not null)
            {
                _rowsByDn[key] = candidate;
                added.Add(candidate);
            }
            else if (existing is not null)
            {
                updated.Add(existing);
            }
        }

        var structureChanged = removed.Count > 0 || added.Count > 0;
        var reordered = false;
        if (_localSortAttribute is { } sortAttribute && (structureChanged || updated.Count > 0))
        {
            reordered = MergeIntoSortedRows(sortAttribute, removed, updated, added);
        }
        else if (structureChanged)
        {
            if (removed.Count > 0)
            {
                _allRows.RemoveAll(removed.Contains);
            }

            _allRows.AddRange(added);
        }

        if (structureChanged || reordered)
        {
            _rowsVersion++;
            ApplyLocalView();
        }

        if (structureChanged)
        {
            LoadedCount = _allRows.Count;
            OnPropertyChanged(nameof(TotalCount));
            TrySelectPending(_allRows);
        }

        if (selectionAffected)
        {
            // A selected row changed state (disabled, unlocked, renamed...) or vanished: selection dependent commands
            // (Enable/Disable/Unlock...) and the details pane must re-evaluate.
            RaiseSelectionChanged();
        }
    }

    /// <summary>
    /// Applies removed, updated and new rows to the locally sorted <see cref="_allRows"/> without sorting all of them on
    /// the UI thread (a live update in a 50 000 row list must not freeze it): one pass drops removed rows and takes out
    /// updated ones, then updated and new rows are put back by binary search. The result is exactly what a stable sort
    /// of the changed list gives: equal keys keep their previous order, new rows follow the existing ones.
    /// </summary>
    /// <returns>True when the order of the rows changed (beyond removing rows and appending new ones).</returns>
    private bool MergeIntoSortedRows(
        string attribute,
        HashSet<ObjectRowViewModel> removed,
        HashSet<ObjectRowViewModel> updated,
        List<ObjectRowViewModel> added)
    {
        var comparer = RowSorting.CreateComparer(attribute, _localSortDescending, _services.Localizer.Culture, _session()?.Schema);
        var remaining = new List<ObjectRowViewModel>(_allRows.Count);
        var remainingOrigins = new List<int>(_allRows.Count);
        var inserts = new List<(ObjectRowViewModel Row, int Origin)>(updated.Count + added.Count);
        for (var i = 0; i < _allRows.Count; i++)
        {
            var row = _allRows[i];
            if (removed.Contains(row))
            {
                continue;
            }

            if (updated.Contains(row))
            {
                inserts.Add((row, i));
            }
            else
            {
                remaining.Add(row);
                remainingOrigins.Add(i);
            }
        }

        var origin = _allRows.Count;
        foreach (var row in added)
        {
            inserts.Add((row, origin++));
        }

        // Total order (key, previous position): the inserts in result order, and each one's place among the others.
        int Compare(ObjectRowViewModel row, int rowOrigin, ObjectRowViewModel other, int otherOrigin) =>
            comparer.Compare(row, other) is var byKey and not 0 ? byKey : rowOrigin.CompareTo(otherOrigin);

        inserts.Sort((a, b) => Compare(a.Row, a.Origin, b.Row, b.Origin));
        var result = new List<ObjectRowViewModel>(remaining.Count + inserts.Count);
        var next = 0;
        foreach (var (row, rowOrigin) in inserts)
        {
            // First remaining row after this one (the inserts are ordered, so the search starts where the previous ended).
            int low = next, high = remaining.Count;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (Compare(remaining[middle], remainingOrigins[middle], row, rowOrigin) > 0)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            result.AddRange(remaining.GetRange(next, low - next));
            result.Add(row);
            next = low;
        }

        result.AddRange(remaining.GetRange(next, remaining.Count - next));
        var reordered = false;
        if (removed.Count == 0 && added.Count == 0)
        {
            for (var i = 0; i < result.Count && !reordered; i++)
            {
                reordered = !ReferenceEquals(result[i], _allRows[i]);
            }
        }

        _allRows.Clear();
        _allRows.AddRange(result);
        return reordered;
    }

    /// <summary>Drops a removed row from <see cref="SelectedItem"/> (the view's multi-selection follows the Items reset).</summary>
    private void ForgetSelected(ObjectRowViewModel row)
    {
        if (ReferenceEquals(SelectedItem, row))
        {
            SelectedItem = null;
        }
    }

    private static bool IsInScope(string dn, string baseDn, SearchScope scope)
    {
        if (!DistinguishedName.TryParse(dn, out var target) || !DistinguishedName.TryParse(baseDn, out var root))
        {
            return false;
        }

        return scope switch
        {
            SearchScope.Base => target.Equals(root),
            SearchScope.OneLevel => target.Parent is { } parent && parent.Equals(root),
            _ => target.Equals(root) || target.IsDescendantOf(root),
        };
    }

    private void OnDirectoryChanged(object? sender, DirectoryChangesEventArgs e) =>
        _ = _services.Dispatcher.RunAsync(() => _ = HandleChangesAsync(e.Changes));

    /// <summary>Applies changes made by this application: removes deleted/moved-out rows, re-reads modified/new ones.</summary>
    internal async Task HandleChangesAsync(IReadOnlyList<DirectoryChange> changes)
    {
        var location = Location;
        var search = _lastSearch;
        if (location is null || search is null || _disposed)
        {
            return;
        }

        if (await HandleLocationChangeAsync(changes, location, search.BaseDn).ConfigureAwait(true))
        {
            return;
        }

        var refresh = new List<string>();
        var removed = false;
        var unlisted = 0;
        foreach (var change in changes)
        {
            // One change is published for the root of a moved/renamed/deleted subtree: rows listed below that root (a
            // subtree view like "Disabled users" listing users of a moved OU) changed their DN or are gone.
            if (change.Kind is not (DirectoryChangeKind.Deleted or DirectoryChangeKind.Moved)
                || !(change.ObjectKind == ObjectKind.Unknown || change.ObjectKind.IsContainer())
                || !DistinguishedName.TryParse(change.DistinguishedName, out var root)
                || RowKeysBelow(root) is not { Count: > 0 } below)
            {
                continue;
            }

            if (change.Kind == DirectoryChangeKind.Moved)
            {
                if (DistinguishedName.TryParse(change.CurrentDistinguishedName, out var target))
                {
                    await ReloadAfterMoveAsync(root, target).ConfigureAwait(true);
                    return;
                }

                continue;
            }

            foreach (var key in below)
            {
                removed |= RemoveRow(key);
            }
        }

        foreach (var change in changes)
        {
            var oldKey = DnKey.For(change.DistinguishedName);
            switch (change.Kind)
            {
                case DirectoryChangeKind.Deleted:
                    removed |= RemoveRow(oldKey);
                    break;
                case DirectoryChangeKind.Moved:
                    var wasSelected = _rowsByDn.TryGetValue(oldKey, out var movedRow) && ReferenceEquals(SelectedItem, movedRow);
                    removed |= RemoveRow(oldKey);
                    if (IsInScope(change.CurrentDistinguishedName, search.BaseDn, location.Scope))
                    {
                        refresh.Add(change.CurrentDistinguishedName);
                        if (wasSelected)
                        {
                            // Renamed in place (F2): keep the object selected under its new DN.
                            _pendingSelectionKey = DnKey.For(change.CurrentDistinguishedName);
                        }
                    }

                    break;
                case DirectoryChangeKind.Created:
                    if (IsInScope(change.DistinguishedName, search.BaseDn, location.Scope))
                    {
                        refresh.Add(change.DistinguishedName);
                    }

                    break;
                default:
                    if (_rowsByDn.ContainsKey(oldKey))
                    {
                        refresh.Add(change.DistinguishedName);
                    }
                    else if (unlisted < MaxUnlistedRefresh && IsInScope(change.DistinguishedName, search.BaseDn, location.Scope))
                    {
                        // Not listed, but it may match the filter now (the base read applies the list filter).
                        refresh.Add(change.DistinguishedName);
                        unlisted++;
                    }

                    break;
            }
        }

        if (removed)
        {
            LoadedCount = _allRows.Count;
            OnPropertyChanged(nameof(TotalCount));
            ApplyLocalView();
        }

        if (refresh.Count > 0)
        {
            try
            {
                await RefreshObjectsAsync(refresh).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is DirectoryException or OperationCanceledException)
            {
                // The rows stay as they were; the next refresh (F5) shows the current state.
            }
        }
    }

    /// <summary>
    /// The listed container (search base) itself or a container above it was deleted, moved or renamed - one change
    /// is published for the root of a subtree: a deleted base shows the "no longer exists" state, a moved one is shown
    /// at its new DN (old prefix replaced by the new one) with the same chips, sort and columns.
    /// </summary>
    /// <param name="changes">The published changes.</param>
    /// <param name="location">The shown location.</param>
    /// <param name="baseDn">Resolved base DN of the last search (saved views may hold a domain placeholder).</param>
    private async Task<bool> HandleLocationChangeAsync(IReadOnlyList<DirectoryChange> changes, ListLocation location, string baseDn)
    {
        if (!DistinguishedName.TryParse(baseDn, out var current))
        {
            return false;
        }

        foreach (var change in changes)
        {
            if (change.Kind is not (DirectoryChangeKind.Deleted or DirectoryChangeKind.Moved)
                || !DistinguishedName.TryParse(change.DistinguishedName, out var changed)
                || !(current.Equals(changed) || current.IsDescendantOf(changed)))
            {
                continue;
            }

            if (change.Kind == DirectoryChangeKind.Deleted)
            {
                _loads.Cancel();
                ClearRows();
                Error = _services.Localizer[L.List.LocationDeleted];
                return true;
            }

            if (!DistinguishedName.TryParse(change.CurrentDistinguishedName, out var target))
            {
                continue;
            }

            var rebased = Rebase(current, changed, target).ToString();

            // A renamed container gets its new name as title; otherwise only the base DN changes.
            var moved = location.Kind == ListLocationKind.Container && current.Equals(changed)
                ? location with { BaseDn = rebased, Title = ListLocation.ForContainer(rebased).Title }
                : location with { BaseDn = rebased };
            _history.ReplaceCurrent(moved);
            Location = moved;
            NotifyNavigationCommands();
            Navigated?.Invoke(this, EventArgs.Empty);
            await ReloadAfterMoveAsync(changed, target).ConfigureAwait(true);
            return true;
        }

        return false;
    }

    /// <summary>Reloads the list after <paramref name="oldDn"/> moved to <paramref name="newDn"/>; the selected object stays selected.</summary>
    private Task ReloadAfterMoveAsync(DistinguishedName oldDn, DistinguishedName newDn)
    {
        if (SelectedItem is { } selected && DistinguishedName.TryParse(selected.Dn, out var dn) && dn.IsDescendantOf(oldDn))
        {
            _pendingSelectionKey = DnKey.For(Rebase(dn, oldDn, newDn).ToString());
        }

        return LoadAsync();
    }

    /// <summary><paramref name="dn"/> (equal to or below <paramref name="oldAncestor"/>) with that prefix replaced by <paramref name="newAncestor"/>.</summary>
    private static DistinguishedName Rebase(DistinguishedName dn, DistinguishedName oldAncestor, DistinguishedName newAncestor)
    {
        var result = newAncestor;
        for (var i = dn.Rdns.Count - oldAncestor.Rdns.Count - 1; i >= 0; i--)
        {
            result = result.Child(dn.Rdns[i].Type, dn.Rdns[i].Value);
        }

        return result;
    }

    /// <summary>Keys of the listed rows strictly below <paramref name="root"/>.</summary>
    private List<string> RowKeysBelow(DistinguishedName root)
    {
        var suffix = "," + DnKey.For(root.ToString());
        var below = new List<string>();
        foreach (var key in _rowsByDn.Keys)
        {
            // The suffix test is cheap; parsing confirms it (an escaped comma in a name could fake the suffix).
            if (key.EndsWith(suffix, StringComparison.Ordinal) && DistinguishedName.TryParse(key, out var dn) && dn.IsDescendantOf(root))
            {
                below.Add(key);
            }
        }

        return below;
    }

    private bool RemoveRow(string key)
    {
        if (!_rowsByDn.Remove(key, out var row))
        {
            return false;
        }

        _rowsVersion++;
        _allRows.Remove(row);
        ForgetSelected(row);
        return true;
    }

    /// <param name="session">Session of the load.</param>
    /// <param name="location">The loaded location.</param>
    /// <param name="baseDn">Resolved base of the load's own search (never the shared last search: a newer load may have replaced it).</param>
    /// <param name="generation">The load's generation; a stale watch is disposed right away.</param>
    private async Task StartWatchingAsync(IDirectorySession session, ListLocation location, string baseDn, int generation)
    {
        if (location.Kind != ListLocationKind.Container || !_services.Settings.Current.LiveUpdates || _disposed || generation != _generation)
        {
            return;
        }

        try
        {
            var watch = await Task.Run(() => session.WatchAsync(baseDn, OnServerChange, CancellationToken.None)).ConfigureAwait(true);
            if (generation == _generation && !_disposed)
            {
                _watch = watch;
            }
            else if (watch is not null)
            {
                await Task.Run(() => watch.DisposeAsync().AsTask()).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is DirectoryException or NotSupportedException or InvalidOperationException)
        {
            // Live updates are optional; the list stays a snapshot ("as of" time in the status bar).
        }
    }

    private async Task StopWatchingAsync()
    {
        var watch = Interlocked.Exchange(ref _watch, null);
        if (watch is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => watch.DisposeAsync().AsTask()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or ObjectDisposedException or InvalidOperationException)
        {
            // Ignored: the subscription is gone either way.
        }
    }

    /// <summary>Change notification from the DC (background thread): debounce and refresh the reported objects.</summary>
    private void OnServerChange(string distinguishedName)
    {
        lock (_liveGate)
        {
            _liveChanged.Add(distinguishedName);
        }

        _ = _services.Dispatcher.RunAsync(() => _ = _liveRefresh.RunAsync(async _ =>
        {
            string[] pending;
            lock (_liveGate)
            {
                pending = [.. _liveChanged];
                _liveChanged.Clear();
            }

            try
            {
                // Not the debouncer's token: a newer notification cancels only the pending delay, never a refresh that
                // already took its DNs out of the queue (those changes would be lost until F5).
                await RefreshObjectsAsync(pending, _lifetime.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is DirectoryException or OperationCanceledException)
            {
                // Best effort.
            }
        }));
    }

    private void TrySelectPending(IEnumerable<ObjectRowViewModel> candidates)
    {
        if (_pendingSelectionKey is null)
        {
            return;
        }

        var match = candidates.FirstOrDefault(r => r.Key == _pendingSelectionKey);
        if (match is not null)
        {
            _pendingSelectionKey = null;
            Select(match);
        }
    }

    private void Select(ObjectRowViewModel row)
    {
        SelectedItem = row;
        ScrollIntoViewRequested?.Invoke(this, row);
    }

    private ListLocation? UpLocation()
    {
        var location = Location;
        var session = _session();
        if (location?.UpDn is not { } up || session is null)
        {
            return null;
        }

        var resolved = ResolveBaseDn(up, session.Info.DefaultNamingContext);
        if (!DistinguishedName.TryParse(resolved, out var upDn)
            || !DistinguishedName.TryParse(session.Info.DefaultNamingContext, out var domain)
            || !(upDn.Equals(domain) || upDn.IsDescendantOf(domain)))
        {
            return null;
        }

        return upDn.Equals(domain)
            ? ListLocation.ForContainer(resolved, session.Info.DomainDnsName, ObjectIcon.Domain)
            : ListLocation.ForContainer(resolved);
    }

    private void NotifyNavigationCommands()
    {
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
    }

    private void BuildColumns(ListLocation location)
    {
        var settings = _services.Settings.Current;
        IEnumerable<(string Attribute, double Width)> columns =
            settings.ColumnLayouts.TryGetValue(location.ColumnLayoutKey, out var layout) && layout.Count > 0
                ? layout.Select(e => (e.Attribute, e.Width))
                : location.DefaultColumns.Select(a => (a, 0d));
        ReplaceColumns(columns);
    }

    private void ReplaceColumns(IEnumerable<(string Attribute, double Width)> columns)
    {
        foreach (var column in Columns)
        {
            column.PropertyChanged -= OnColumnChanged;
        }

        Columns.Clear();
        foreach (var (attribute, width) in columns.DistinctBy(c => c.Attribute, StringComparer.OrdinalIgnoreCase))
        {
            var column = new ColumnViewModel(attribute, HeaderFor(attribute), width);
            column.PropertyChanged += OnColumnChanged;
            Columns.Add(column);
        }

        OnPropertyChanged(nameof(VisibleColumns));
    }

    /// <summary>Localized column header for an attribute or pseudo column.</summary>
    public string HeaderFor(string attribute)
    {
        var l = _services.Localizer;
        if (string.Equals(attribute, ColumnSets.TypeColumn, StringComparison.OrdinalIgnoreCase))
        {
            return l[L.Columns.Type];
        }

        if (string.Equals(attribute, ColumnSets.LocationColumn, StringComparison.OrdinalIgnoreCase))
        {
            return l[L.Columns.Location];
        }

        return string.Equals(attribute, Attr.Name, StringComparison.OrdinalIgnoreCase)
            ? l[L.Columns.Name]
            : l.AttributeName(attribute, _session()?.Schema);
    }

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ColumnViewModel.Width) or nameof(ColumnViewModel.IsVisible))
        {
            if (e.PropertyName == nameof(ColumnViewModel.IsVisible))
            {
                OnPropertyChanged(nameof(VisibleColumns));

                // Only visible columns are requested: a column shown again needs its attribute from the DC.
                if (sender is ColumnViewModel { IsVisible: true } column && !ColumnSets.IsPseudo(column.Attribute)
                    && _lastSearch is { } search && !search.Attributes.Contains(column.Attribute, StringComparer.OrdinalIgnoreCase))
                {
                    LoadAsync().Forget();
                }
            }

            _layoutSaver.RunAsync(_ => SaveLayoutAsync()).Forget();
        }
    }

    private Task SaveLayoutAsync()
    {
        var key = Location?.ColumnLayoutKey;
        if (key is null)
        {
            return Task.CompletedTask;
        }

        var entries = Columns.Where(c => c.IsVisible).Select(c => new ColumnLayoutEntry(c.Attribute, c.Width)).ToList();
        return _services.Settings.UpdateAsync(s => s.ColumnLayouts[key] = entries);
    }

    private void OnChipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterChipViewModel.IsActive) && Location is not null && !_settingChips)
        {
            LoadAsync().Forget();
        }
    }

    private void SetChipsSilently(IReadOnlyList<QuickFilterKind> active)
    {
        _settingChips = true;
        try
        {
            foreach (var chip in FilterChips)
            {
                chip.IsActive = active.Contains(chip.Kind);
            }
        }
        finally
        {
            _settingChips = false;
        }
    }

    private string ChipTitle(QuickFilterKind kind) => kind == QuickFilterKind.Inactive
        ? _services.Localizer.Format(DisplayNames.ChipKey(kind), _services.Settings.Current.InactiveDays)
        : _services.Localizer[DisplayNames.ChipKey(kind)];

    private void OnSettingsChanged(object? sender, EventArgs e) => _ = _services.Dispatcher.RunAsync(() =>
    {
        var days = _services.Settings.Current.InactiveDays;
        if (_disposed || days == _inactiveDays)
        {
            // Most settings changes (column widths, recent containers...) do not concern the chips.
            return;
        }

        _inactiveDays = days;
        var inactive = FilterChips.Single(c => c.Kind == QuickFilterKind.Inactive);
        inactive.Title = ChipTitle(QuickFilterKind.Inactive);
        if (inactive.IsActive && Location is not null)
        {
            // The rows were read with the previous threshold: the chip's title and the list must agree.
            LoadAsync().Forget();
        }
    });

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (var column in Columns)
        {
            column.Header = HeaderFor(column.Attribute);
        }

        foreach (var chip in FilterChips)
        {
            chip.Title = ChipTitle(chip.Kind);
        }

        if (_rowContext is { } context)
        {
            var updated = new RowContext(_services.Localizer, _services.Sessions.Formatter, context.Policy, context.Now);
            _rowContext = updated;
            foreach (var row in _allRows)
            {
                row.Reformat(updated);
            }
        }

        // Built-in views, search and Find results: tab header and address text in the new language.
        if (Location is { } location && location.Relocalize(_services.Localizer) is var localized && !ReferenceEquals(localized, location))
        {
            _history.ReplaceCurrent(localized);
            Location = localized;
        }

        // Collation and the localized Type/Location texts changed: keep a local sort exact (live updates insert rows
        // by binary search into the sorted list).
        if (_localSortAttribute is not null && !IsLoading)
        {
            ApplyLocalSortAsync().Forget();
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(QuickFilterStatus));
        OnPropertyChanged(nameof(SelectionText));
    }

    private void OnSelectedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RaiseSelectionChanged();

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(SelectionText));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Columns and rows to export (rows are formatted off the UI thread with <see cref="ObjectRowViewModel.FormatUncached"/>).</summary>
public sealed record ListExportSnapshot(IReadOnlyList<(string Attribute, string Header)> Columns, IReadOnlyList<ObjectRowViewModel> Rows)
{
    /// <summary>Formats all cells (call on a background thread for large lists).</summary>
    public IEnumerable<IReadOnlyList<string>> FormatRows() =>
        Rows.Select(r => (IReadOnlyList<string>)[.. Columns.Select(c => r.FormatUncached(c.Attribute))]);

    /// <summary>
    /// Columns whose values are secrets (passwords, LAPS... and the confidential attributes of <paramref name="schema"/>):
    /// they are exported only after the operator confirmed it.
    /// </summary>
    public IReadOnlyList<(string Attribute, string Header)> SecretColumns(ISchemaCatalog? schema) =>
        [.. Columns.Where(c => SecretAttributes.IsSecret(c.Attribute, schema))];

    /// <summary>The same rows without the given columns.</summary>
    public ListExportSnapshot WithoutColumns(IEnumerable<string> attributes)
    {
        var excluded = new HashSet<string>(attributes, StringComparer.OrdinalIgnoreCase);
        return this with { Columns = [.. Columns.Where(c => !excluded.Contains(c.Attribute))] };
    }

    /// <summary>Default export file name from a list title (invalid file name characters removed).</summary>
    public static string SuggestFileName(string title, DateTimeOffset now)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string([.. (title ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c)]).Trim();
        return $"{(clean.Length == 0 ? "export" : clean)}_{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.csv";
    }
}
