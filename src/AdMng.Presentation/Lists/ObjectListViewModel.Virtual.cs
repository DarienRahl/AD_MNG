using System.Collections;
using System.ComponentModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using SortKey = AdMng.Core.Directory.SortKey;

namespace AdMng.Presentation.Lists;

/// <summary>
/// Virtual mode of very large containers (docs/dev/phase1-navigation.md): a container listing whose streamed rows reach
/// <see cref="VirtualThreshold"/> switches to a server sorted virtual list (VLV, <see cref="VirtualRowList"/>) when the
/// DC serves one; otherwise it keeps streaming. The location is then remembered as large, so reloads (F5, sort, chips,
/// columns, quick filter on the server) use VLV right away until the operator navigates elsewhere.
/// </summary>
public sealed partial class ObjectListViewModel
{
    /// <summary>Streamed rows of a container at which the list asks the DC for a virtual list.</summary>
    internal const int DefaultVirtualThreshold = 5000;

    private static readonly TimeSpan ServerFilterDelay = TimeSpan.FromMilliseconds(400);

    // Multi-selections up to this size get the rows of their replaced placeholders (the view's selection is a list:
    // a range selection over a whole huge container is not searched once per arriving row).
    private const int MaxReselectedPlaceholders = 1000;

    private VirtualRowList? _virtualRows;
    private Debouncer? _serverFilter;
    private List<string>? _virtualSelection;

    // Type-to-find requests: only the newest one selects (an older, shorter prefix may be answered last).
    private int _findRequest;

    // Selected placeholders whose page arrived (Replace): their rows are selected once all handlers saw the Replace.
    private List<(ObjectRowViewModel Placeholder, ObjectRowViewModel Row)>? _replacedSelection;

    // The shown location is large: loads use VLV. Reset when another location is shown.
    private bool _virtualLocation;

    // Sort attribute the DC refused a virtual list for (this location): such loads stream.
    private string? _virtualRefusedSort;

    /// <summary>Streamed rows at which a container switches to the virtual mode (5000; tests lower it).</summary>
    public int VirtualThreshold { get; set; } = DefaultVirtualThreshold;

    /// <summary>Rows per VLV request.</summary>
    public int VirtualPageSize { get; set; } = VirtualRowList.DefaultPageSize;

    /// <summary>Pages a virtual list keeps in memory.</summary>
    public int VirtualMaxCachedPages { get; set; } = VirtualRowList.DefaultMaxCachedPages;

    /// <summary>True while the list shows a very large container as a server sorted virtual list (VLV).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rows), nameof(StatusText), nameof(IsFullyLoaded), nameof(QuickFilterStatus))]
    public partial bool IsVirtual { get; private set; }

    /// <summary>
    /// What the view shows: <see cref="Items"/>, or the data-virtualized <see cref="VirtualRows"/> in the virtual mode.
    /// Both raise collection and <c>Count</c> notifications.
    /// </summary>
    public IList Rows => (IList?)_virtualRows ?? Items;

    /// <summary>The virtual list of the virtual mode (null otherwise).</summary>
    public VirtualRowList? VirtualRows => _virtualRows;

    /// <summary>
    /// Type-to-find of the virtual mode: positions on the first row whose value of the sort column starts with
    /// <paramref name="prefix"/> (VLV seek, like a phone book) and selects it. False when nothing matches or the list is
    /// not virtual (the view's own text search covers loaded lists). A newer call supersedes a running one: the older one
    /// then returns false without selecting (typed characters arrive faster than the DC answers).
    /// </summary>
    public async Task<bool> FindByPrefixAsync(string prefix)
    {
        var request = ++_findRequest;
        if (_virtualRows is not { } rows || string.IsNullOrWhiteSpace(prefix) || _lastSearch?.Sort is not { } sort)
        {
            return false;
        }

        int? index;
        try
        {
            // The sort column like a phone book: the server seeks, the rows before the target are checked too.
            index = await rows.FindPrefixAsync(prefix, row => SortValue(row, sort.Attribute)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }

        if (request != _findRequest || !ReferenceEquals(rows, _virtualRows) || index is not { } match || rows.GetLoaded(match) is not { } row)
        {
            return false;
        }

        SelectVirtual(row);
        return true;
    }

    private static string? SortValue(ObjectRowViewModel row, string attribute) =>
        string.Equals(attribute, Attr.Name, StringComparison.OrdinalIgnoreCase) ? row.Name : row.Object.GetString(attribute);

    /// <summary>The location may become a virtual list: a one-level container listing without client-side post filter.</summary>
    private bool CanVirtualize(ListLocation location, IDirectorySession session) =>
        location.Kind == ListLocationKind.Container
        && location.Scope == SearchScope.OneLevel
        && location.PostFilter == ListPostFilter.None
        && !ChipsRequireLockedPostFilter
        && (session.Info.SupportedControls.Count == 0 || session.Info.Supports(LdapControls.VirtualListView));

    /// <summary>The search of a virtual list: the list's search with the server sort (current sort column, else name).</summary>
    private DirectorySearch VirtualSearch(DirectorySearch search)
    {
        var sort = _serverSort
            ?? (_localSortAttribute is { } local && ColumnSets.IsServerSortable(local) ? new SortKey(local, _localSortDescending) : null)
            ?? new SortKey(Attr.Name);
        return search with
        {
            Sort = sort,
            SizeLimit = 0,
            Attributes = [.. search.Attributes.Append(sort.Attribute).Distinct(StringComparer.OrdinalIgnoreCase)],
        };
    }

    private bool IsVirtualRefused(DirectorySearch search) =>
        _virtualRefusedSort is { } refused && string.Equals(refused, search.Sort?.Attribute, StringComparison.OrdinalIgnoreCase);

    /// <summary>The quick filter of a large location runs on the server (rows that are not loaded cannot be filtered).</summary>
    private string? ServerTextFilter()
    {
        var text = QuickFilterText.Trim();
        return !_virtualLocation || text.Length == 0
            ? null
            : LdapFilter.Or(
                LdapFilter.Contains(Attr.Name, text),
                LdapFilter.Contains(Attr.DisplayName, text),
                LdapFilter.Contains(Attr.SamAccountName, text),
                LdapFilter.Contains(Attr.Description, text));
    }

    /// <summary>
    /// Loads a known large location as a virtual list directly (no streaming). False when the DC refused it (the caller
    /// streams); the refusal is remembered for this sort.
    /// </summary>
    private async Task<bool> TryShowVirtualAsync(IDirectorySession session, DirectorySearch search, RowContext context, int generation, CancellationToken token)
    {
        VirtualListPage first;
        try
        {
            first = await Task.Run(
                () => session.GetVirtualListPageAsync(search, new VirtualListRequest { Count = VirtualPageSize }, token),
                token).ConfigureAwait(true);
        }
        catch (DirectoryException)
        {
            _virtualRefusedSort = search.Sort?.Attribute;
            return false;
        }

        if (generation != _generation || token.IsCancellationRequested)
        {
            return false;
        }

        ShowVirtual(session, search, first, context);
        return true;
    }

    /// <summary>
    /// Called by the stream (background thread) when a container reached <see cref="VirtualThreshold"/>: asks the DC for
    /// the first window of the virtual list and switches to it. False keeps the stream going.
    /// </summary>
    private async Task<bool> ProbeVirtualAsync(IDirectorySession session, DirectorySearch search, RowContext context, int generation, CancellationToken token)
    {
        VirtualListPage first;
        try
        {
            first = await session.GetVirtualListPageAsync(search, new VirtualListRequest { Count = VirtualPageSize }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DirectoryException or NotSupportedException)
        {
            await _services.Dispatcher.InvokeAsync(() =>
            {
                if (generation == _generation)
                {
                    _virtualRefusedSort = search.Sort?.Attribute;
                }
            }).ConfigureAwait(false);
            return false;
        }

        var switched = false;
        await _services.Dispatcher.InvokeAsync(() =>
        {
            if (generation == _generation && !token.IsCancellationRequested)
            {
                ShowVirtual(session, search, first, context);
                switched = true;
            }
        }).ConfigureAwait(false);
        return switched;
    }

    /// <summary>Switches the list to the virtual mode (UI thread): the streamed rows are dropped.</summary>
    private void ShowVirtual(IDirectorySession session, DirectorySearch search, VirtualListPage first, RowContext context)
    {
        var selected = _pendingSelectionKey ?? (SelectedItem is { IsPlaceholder: false } row ? row.Key : null);
        DisposeVirtual();
        ClearRows();
        _virtualLocation = true;
        _serverSort = search.Sort;
        _localSortAttribute = null;
        SetSortIndicator(search.Sort?.Attribute, search.Sort?.Descending ?? false);
        _lastSearch = search;
        var rows = new VirtualRowList(
            first,
            VirtualPageSize,
            (request, ct) => Task.Run(() => session.GetVirtualListPageAsync(search, request, ct), ct),
            obj => new ObjectRowViewModel(obj, _rowContext ?? context),
            () => ObjectRowViewModel.CreatePlaceholder(_rowContext ?? context),
            _services.Dispatcher,
            VirtualMaxCachedPages);
        rows.PropertyChanged += OnVirtualRowsPropertyChanged;
        rows.CollectionChanged += OnVirtualRowsCollectionChanged;
        rows.FetchFailed += OnVirtualFetchFailed;
        rows.Reloaded += OnVirtualReloaded;
        rows.PlaceholdersReplaced += OnVirtualPlaceholdersReplaced;
        _virtualRows = rows;
        IsVirtual = true;
        LoadedCount = rows.Count;
        OnPropertyChanged(nameof(TotalCount));
        _pendingSelectionKey = selected;
    }

    /// <summary>Leaves the virtual mode (a new load, disposal).</summary>
    private void DisposeVirtual()
    {
        if (_virtualRows is not { } rows)
        {
            return;
        }

        rows.PropertyChanged -= OnVirtualRowsPropertyChanged;
        rows.CollectionChanged -= OnVirtualRowsCollectionChanged;
        rows.FetchFailed -= OnVirtualFetchFailed;
        rows.Reloaded -= OnVirtualReloaded;
        rows.PlaceholdersReplaced -= OnVirtualPlaceholdersReplaced;
        rows.Dispose();
        _virtualRows = null;
        _virtualSelection = null;
        _replacedSelection = null;
        IsVirtual = false;
    }

    /// <summary>Re-reads the shown pages and the count of the virtual list (live updates, own changes).</summary>
    private async Task RefreshVirtualAsync()
    {
        if (_virtualRows is not { } rows)
        {
            return;
        }

        try
        {
            await rows.RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or OperationCanceledException or ObjectDisposedException)
        {
            // The rows stay as they were; F5 shows the current state.
            return;
        }

        if (!ReferenceEquals(rows, _virtualRows))
        {
            return;
        }

        LoadedAt = _services.Time.GetLocalNow();
        await SelectPendingVirtualAsync().ConfigureAwait(true);
    }

    /// <summary>Changes made by this application in a virtual list: one refresh of the shown pages when any concerns it.</summary>
    private async Task HandleVirtualChangesAsync(IReadOnlyList<DirectoryChange> changes, string baseDn)
    {
        var affected = false;
        foreach (var change in changes)
        {
            var listed = IsInScope(change.DistinguishedName, baseDn, SearchScope.OneLevel);
            var movedIn = change.Kind == DirectoryChangeKind.Moved && IsInScope(change.CurrentDistinguishedName, baseDn, SearchScope.OneLevel);
            affected |= listed || movedIn;
            if (change.Kind == DirectoryChangeKind.Moved && movedIn && SelectedItem is { IsPlaceholder: false } selected
                && selected.Key == DnKey.For(change.DistinguishedName))
            {
                // Renamed in place (F2): keep the object selected under its new DN (its position changed with the name).
                _pendingSelectionKey = DnKey.For(change.CurrentDistinguishedName);
            }
        }

        if (affected)
        {
            await RefreshVirtualAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Selects the pending object (locate, new object, renamed selection) in the virtual list. One attempt: an object that
    /// cannot be found (deleted, moved out) is not sought again on every later refresh - each attempt costs a seek and a
    /// page read.
    /// </summary>
    private async Task SelectPendingVirtualAsync()
    {
        if (_pendingSelectionKey is not { } key || _virtualRows is not { } rows)
        {
            return;
        }

        await LocateVirtualAsync(rows, key).ConfigureAwait(true);
        if (ReferenceEquals(_pendingSelectionKey, key) && ReferenceEquals(rows, _virtualRows))
        {
            _pendingSelectionKey = null;
        }
    }

    /// <summary>
    /// Finds an object in the virtual list by DN: loaded rows first, else a seek on its sort value (the RDN value for
    /// <c>name</c>, otherwise read from the DC) and a scan of the rows with that value.
    /// </summary>
    private async Task<bool> LocateVirtualAsync(VirtualRowList rows, string key)
    {
        if (rows.IndexOfDn(key) is >= 0 and var loaded && rows.GetLoaded(loaded) is { } known)
        {
            SelectVirtual(known);
            return true;
        }

        if (_lastSearch?.Sort is not { } sort || _session() is not { } session || !DistinguishedName.TryParse(key, out var dn) || dn.IsRoot)
        {
            return false;
        }

        try
        {
            var value = string.Equals(sort.Attribute, Attr.Name, StringComparison.OrdinalIgnoreCase)
                ? dn.Name
                : (await Task.Run(() => session.GetAsync(key, [sort.Attribute], CancellationToken.None)).ConfigureAwait(true))?.GetString(sort.Attribute);
            if (value is null || await rows.SeekAsync(value).ConfigureAwait(true) is not { } target)
            {
                return false;
            }

            // Equal sort values: the object is among the next rows.
            for (var i = target; i < Math.Min(rows.Count, target + rows.PageSize); i++)
            {
                await rows.EnsureLoadedAsync(i).ConfigureAwait(true);
                if (!ReferenceEquals(rows, _virtualRows))
                {
                    return false;
                }

                if (rows.GetLoaded(i) is { } row && row.Key == key)
                {
                    SelectVirtual(row);
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is DirectoryException or OperationCanceledException or ObjectDisposedException)
        {
            // Not found now; the operator still sees the list.
        }

        return false;
    }

    private void SelectVirtual(ObjectRowViewModel row)
    {
        SelectedItem = row;
        ScrollIntoViewRequested?.Invoke(this, row);
    }

    private string VirtualStatusText()
    {
        var l = _services.Localizer;
        var sort = _lastSearch?.Sort?.Attribute ?? Attr.Name;
        var time = LoadedAt?.ToString("T", l.Culture) ?? string.Empty;
        return l.Format(L.ListVirtual.Summary, LoadedCount.ToString("N0", l.Culture), HeaderFor(sort), time);
    }

    /// <summary>The quick filter text changed on a large location: reload with the text as a server filter (debounced).</summary>
    private void ScheduleServerFilter()
    {
        _serverFilter ??= new Debouncer(_services.Time, ServerFilterDelay);
        _serverFilter.RunAsync(_ => LoadAsync()).Forget();
    }

    private void OnVirtualRowsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is VirtualRowList rows && ReferenceEquals(rows, _virtualRows) && e.PropertyName == nameof(VirtualRowList.Count))
        {
            LoadedCount = rows.Count;
            OnPropertyChanged(nameof(QuickFilterStatus));
        }
    }

    private void OnVirtualRowsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _virtualRows))
        {
            return;
        }

        // A Reset may drop the view's selection: remember it (by DN) to restore it after the reload.
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            _virtualSelection ??= [.. SelectionKeys()];
        }

        // A selected placeholder is swapped out (End / PageDown onto rows not read yet): the list control unselects it,
        // its row is selected instead once every handler has seen the Replace (this handler runs before the view's).
        else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace
            && e.OldItems is [ObjectRowViewModel { IsPlaceholder: true } placeholder]
            && e.NewItems is [ObjectRowViewModel row]
            && (ReferenceEquals(SelectedItem, placeholder) || _selectedItems is { Count: <= MaxReselectedPlaceholders } selected && selected.Contains(placeholder)))
        {
            (_replacedSelection ??= []).Add((placeholder, row));
        }
    }

    /// <summary>Selects the rows of the selected placeholders a page just replaced (see <see cref="OnVirtualRowsCollectionChanged"/>).</summary>
    private void OnVirtualPlaceholdersReplaced(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _virtualRows) || _replacedSelection is not { Count: > 0 } replaced)
        {
            return;
        }

        _replacedSelection = null;
        if (SelectedItem is null || replaced.Any(r => ReferenceEquals(r.Placeholder, SelectedItem)))
        {
            // Nothing else is selected any more (or no view unselected it): the arrived row is the selected item.
            SelectedItem = replaced.FirstOrDefault(r => ReferenceEquals(r.Placeholder, SelectedItem)).Row ?? replaced[0].Row;
        }

        if (_selectedItems is { IsReadOnly: false, IsFixedSize: false } items)
        {
            foreach (var (placeholder, row) in replaced)
            {
                items.Remove(placeholder);
                if (!items.Contains(row))
                {
                    items.Add(row);
                }
            }
        }
    }

    private IEnumerable<string> SelectionKeys()
    {
        var keys = Selection.Select(r => r.Key).ToList();
        if (SelectedItem is { IsPlaceholder: false } selected && !keys.Contains(selected.Key))
        {
            keys.Insert(0, selected.Key);
        }

        return keys;
    }

    private void OnVirtualFetchFailed(object? sender, Exception e)
    {
        if (ReferenceEquals(sender, _virtualRows))
        {
            Error = _services.Errors.Translate(e);
        }
    }

    /// <summary>After a Reset: the selected rows (by DN) are selected again; one that is not shown any more is located.</summary>
    private void OnVirtualReloaded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _virtualRows) || _virtualRows is not { } rows)
        {
            return;
        }

        var keys = _virtualSelection ?? [];
        _virtualSelection = null;
        if (keys.Count == 0)
        {
            return;
        }

        var restored = keys.Select(k => rows.IndexOfDn(k) is >= 0 and var i ? rows.GetLoaded(i) : null).OfType<ObjectRowViewModel>().ToList();
        if (restored.Count == 0)
        {
            // The selected object is not among the re-read rows (renamed away, scrolled far): find it by its sort value.
            _pendingSelectionKey ??= keys[0];
            if (!IsLoading)
            {
                SelectPendingVirtualAsync().Forget();
            }

            return;
        }

        if (SelectedItem is null || SelectedItem.IsPlaceholder || !restored.Contains(SelectedItem))
        {
            SelectedItem = restored[0];
        }

        if (_selectedItems is { IsReadOnly: false, IsFixedSize: false } items)
        {
            foreach (var row in restored.Where(r => !items.Contains(r)))
            {
                items.Add(row);
            }
        }
    }
}
