using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
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

    private readonly ObjectListServices _services;
    private readonly List<ObjectRowViewModel> _allRows = [];
    private readonly Dictionary<string, ObjectRowViewModel> _rowsByDn = new(StringComparer.Ordinal);
    private readonly SerialCancellation _loads = new();
    private readonly NavigationHistory<ListLocation> _history = new();
    private readonly Debouncer _layoutSaver;
    private readonly Debouncer _liveRefresh;
    private readonly HashSet<string> _liveChanged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _liveGate = new();
    private int _generation;
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

    public ObjectListViewModel(ObjectListServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _layoutSaver = new Debouncer(services.Time, LayoutSaveDelay);
        _liveRefresh = new Debouncer(services.Time, LiveUpdateDelay);
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
    [RelayCommand]
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
        var session = _services.Sessions.Current;
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
    [RelayCommand]
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

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task GoBackAsync() => _history.GoBack() is { } previous ? ShowLocationAsync(previous) : Task.CompletedTask;

    private bool CanGoBack() => _history.CanGoBack;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task GoForwardAsync() => _history.GoForward() is { } next ? ShowLocationAsync(next) : Task.CompletedTask;

    private bool CanGoForward() => _history.CanGoForward;

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private Task GoUpAsync() => UpLocation() is { } up ? NavigateAsync(up) : Task.CompletedTask;

    private bool CanGoUp() => UpLocation() is not null;

    /// <summary>Column chooser pre-filled with the current columns and all schema attributes of users, computers, groups and contacts.</summary>
    public ColumnChooserViewModel CreateColumnChooser() =>
        new(_services.Localizer, _services.Sessions.Current?.Schema, [.. Columns.Select(c => c.Attribute)], Location?.DefaultColumns ?? ColumnSets.Container);

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
        await StopWatchingAsync().ConfigureAwait(true);
        _allRows.Clear();
        _rowsByDn.Clear();
        Items.ReplaceAll([]);
        Error = null;
        Partial = false;
        LoadedCount = 0;
        IsLoading = true;
        OnPropertyChanged(nameof(TotalCount));
        var started = _services.Time.GetTimestamp();

        var session = _services.Sessions.Current;
        if (session is null)
        {
            Error = _services.Localizer[L.Errors.NotConnected];
            IsLoading = false;
            return;
        }

        try
        {
            var policy = await _services.Sessions.GetDomainPolicyAsync(token).ConfigureAwait(true);
            var context = new RowContext(_services.Localizer, _services.Sessions.Formatter, policy, _services.Time.GetUtcNow());
            _rowContext = context;
            var search = BuildSearch(location, session.Info.DefaultNamingContext);
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

            await StartWatchingAsync(session, location, generation).ConfigureAwait(true);
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

    private DirectorySearch BuildSearch(ListLocation location, string defaultNamingContext)
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
            filter = QuickFilters.Combine(filter, chips, _services.Time.GetUtcNow(), settings.InactiveDays);
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

        var filter = TextFolding.Fold(QuickFilterText);
        Items.AddRange(filter.Length == 0 ? added : added.Where(r => r.SearchKey.Contains(filter, StringComparison.Ordinal)));
        LoadedCount = _allRows.Count;
        OnPropertyChanged(nameof(TotalCount));
        TrySelectPending(added);
    }

    private void ClearRows()
    {
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

    private async Task ApplyLocalSortAsync()
    {
        if (_localSortAttribute is null || _allRows.Count < 2)
        {
            return;
        }

        var attribute = _localSortAttribute;
        var descending = _localSortDescending;
        var culture = _services.Localizer.Culture;
        var schema = _services.Sessions.Current?.Schema;
        var snapshot = _allRows.ToArray();
        var generation = _generation;
        var sorted = snapshot.Length >= BackgroundSortThreshold
            ? await Task.Run(() => RowSorting.Sort(snapshot, attribute, descending, culture, schema)).ConfigureAwait(true)
            : RowSorting.Sort(snapshot, attribute, descending, culture, schema);
        await _services.Dispatcher.RunAsync(() =>
        {
            if (generation != _generation || _allRows.Count != snapshot.Length)
            {
                return;
            }

            _allRows.Clear();
            _allRows.AddRange(sorted);
            ApplyLocalView();
        }).ConfigureAwait(true);
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
        var structureChanged = false;
        foreach (var (dn, obj) in results)
        {
            var key = DnKey.For(dn);
            _rowsByDn.TryGetValue(key, out var existing);
            var keep = obj is not null;
            ObjectRowViewModel? candidate = null;
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
                    _allRows.Remove(existing);
                    structureChanged = true;
                }

                continue;
            }

            if (existing is null && candidate is not null)
            {
                _rowsByDn[key] = candidate;
                _allRows.Add(candidate);
                structureChanged = true;
            }
        }

        if (!structureChanged)
        {
            return;
        }

        LoadedCount = _allRows.Count;
        OnPropertyChanged(nameof(TotalCount));
        if (_localSortAttribute is not null)
        {
            var sorted = RowSorting.Sort(_allRows, _localSortAttribute, _localSortDescending, _services.Localizer.Culture, _services.Sessions.Current?.Schema);
            _allRows.Clear();
            _allRows.AddRange(sorted);
        }

        ApplyLocalView();
        TrySelectPending(_allRows);
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

        if (await HandleLocationChangeAsync(changes, location).ConfigureAwait(true))
        {
            return;
        }

        var refresh = new List<string>();
        var removed = false;
        foreach (var change in changes)
        {
            var oldKey = DnKey.For(change.DistinguishedName);
            switch (change.Kind)
            {
                case DirectoryChangeKind.Deleted:
                    removed |= RemoveRow(oldKey);
                    break;
                case DirectoryChangeKind.Moved:
                    removed |= RemoveRow(oldKey);
                    if (IsInScope(change.CurrentDistinguishedName, search.BaseDn, location.Scope))
                    {
                        refresh.Add(change.CurrentDistinguishedName);
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

    private async Task<bool> HandleLocationChangeAsync(IReadOnlyList<DirectoryChange> changes, ListLocation location)
    {
        var baseKey = DnKey.For(location.BaseDn);
        var own = changes.FirstOrDefault(c => c.Kind is DirectoryChangeKind.Deleted or DirectoryChangeKind.Moved
            && DnKey.For(c.DistinguishedName) == baseKey);
        if (own is null)
        {
            return false;
        }

        if (own.Kind == DirectoryChangeKind.Deleted)
        {
            _loads.Cancel();
            ClearRows();
            Error = _services.Localizer[L.List.LocationDeleted];
            return true;
        }

        var moved = location.Kind == ListLocationKind.Container
            ? ListLocation.ForContainer(own.CurrentDistinguishedName) with { Icon = location.Icon }
            : location with { BaseDn = own.CurrentDistinguishedName };
        _history.ReplaceCurrent(moved);
        await ShowLocationAsync(moved).ConfigureAwait(true);
        return true;
    }

    private bool RemoveRow(string key)
    {
        if (!_rowsByDn.Remove(key, out var row))
        {
            return false;
        }

        _allRows.Remove(row);
        return true;
    }

    private async Task StartWatchingAsync(IDirectorySession session, ListLocation location, int generation)
    {
        if (location.Kind != ListLocationKind.Container || !_services.Settings.Current.LiveUpdates || _disposed)
        {
            return;
        }

        try
        {
            var baseDn = _lastSearch?.BaseDn ?? location.BaseDn;
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

        _ = _services.Dispatcher.RunAsync(() => _ = _liveRefresh.RunAsync(async ct =>
        {
            string[] pending;
            lock (_liveGate)
            {
                pending = [.. _liveChanged];
                _liveChanged.Clear();
            }

            try
            {
                await RefreshObjectsAsync(pending, ct).ConfigureAwait(true);
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
        var session = _services.Sessions.Current;
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
            : l.AttributeName(attribute, _services.Sessions.Current?.Schema);
    }

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ColumnViewModel.Width) or nameof(ColumnViewModel.IsVisible))
        {
            if (e.PropertyName == nameof(ColumnViewModel.IsVisible))
            {
                OnPropertyChanged(nameof(VisibleColumns));
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
        var inactive = FilterChips.Single(c => c.Kind == QuickFilterKind.Inactive);
        inactive.Title = ChipTitle(QuickFilterKind.Inactive);
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

    /// <summary>Default export file name from a list title (invalid file name characters removed).</summary>
    public static string SuggestFileName(string title, DateTimeOffset now)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string([.. (title ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c)]).Trim();
        return $"{(clean.Length == 0 ? "export" : clean)}_{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.csv";
    }
}
