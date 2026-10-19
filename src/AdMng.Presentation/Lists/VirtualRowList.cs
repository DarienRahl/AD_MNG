using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Lists;

/// <summary>
/// The rows of a very large container as a data-virtualized list: <see cref="Count"/> is the server's content count of a
/// server sorted virtual list view (VLV) and rows are read page by page when the view asks for them. The indexer returns
/// the cached row or a per-index placeholder ("Loading…") and queues the page; loaded rows replace their placeholders
/// (one Replace notification each), a changed count raises Reset. At most <see cref="MaxPendingPages"/> pages wait (the
/// newest first, so a full enumeration cannot queue the whole directory) and <see cref="MaxCachedPages"/> pages stay
/// cached (least recently shown evicted) - memory stays bounded and nothing is written anywhere (CLAUDE.md rule 1).
/// </summary>
/// <remarks>
/// Members are meant for the UI thread (notifications are raised on the thread that applies a page - the UI thread
/// through <see cref="IUiDispatcher"/>); the state is locked anyway, so tests can drive it from any thread. The server
/// context id of the last response is sent with the next request (AD keeps the sorted result set); a content count
/// estimate is never sent, so offsets are taken as they are (no scaling). Refreshes start without the context so that
/// the server sorts the current content again.
/// </remarks>
public sealed class VirtualRowList : IList, IReadOnlyList<ObjectRowViewModel>, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable
{
    public const int DefaultPageSize = 200;
    public const int DefaultMaxCachedPages = 12;

    /// <summary>Pages waiting to be read at most; older wishes are dropped (they are asked again when shown again).</summary>
    public const int MaxPendingPages = 4;

    /// <summary>Most recently shown pages re-read by <see cref="RefreshAsync"/>.</summary>
    public const int RefreshPages = 3;

    /// <summary>Pages <see cref="FindPrefixAsync"/> walks back from the seek target at most.</summary>
    public const int MaxSeekBackPages = 5;

    private readonly Func<VirtualListRequest, CancellationToken, Task<VirtualListPage>> _fetch;
    private readonly Func<DirectoryObject, ObjectRowViewModel> _createRow;
    private readonly Func<ObjectRowViewModel> _createPlaceholder;
    private readonly IUiDispatcher _dispatcher;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, ObjectRowViewModel[]> _pages = [];
    private readonly LinkedList<int> _recent = new();
    private readonly Dictionary<int, LinkedListNode<int>> _recentNodes = [];
    private readonly Dictionary<string, int> _indexByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ObjectRowViewModel> _placeholders = [];
    private readonly Dictionary<ObjectRowViewModel, int> _placeholderIndexes = new(ReferenceEqualityComparer.Instance);
    private readonly List<int> _wanted = [];

    // Pages the view asked for most recently (loaded or not), newest last: what a refresh re-reads.
    private readonly List<int> _shown = [];
    private readonly CancellationTokenSource _lifetime = new();

    // Rows of dropped pages (refresh, count change): reused by DN so that a row keeps its identity (WPF selection).
    private Dictionary<string, ObjectRowViewModel> _reuse = new(StringComparer.Ordinal);
    private byte[]? _contextId;
    private int _count;
    private int _generation;
    private Task _pump = Task.CompletedTask;
    private bool _pumping;
    private bool _disposed;

    /// <param name="first">The first window (offset 0) the caller read to decide on the virtual mode.</param>
    /// <param name="pageSize">Rows per page (the first window should have this size).</param>
    /// <param name="fetch">Reads one window (called off the UI thread by the caller's delegate).</param>
    /// <param name="createRow">Row of a directory object (formatting context of the list).</param>
    /// <param name="createPlaceholder">A new "Loading…" row.</param>
    /// <param name="dispatcher">Applies loaded pages on the UI thread.</param>
    /// <param name="maxCachedPages">Pages kept in memory.</param>
    public VirtualRowList(
        VirtualListPage first,
        int pageSize,
        Func<VirtualListRequest, CancellationToken, Task<VirtualListPage>> fetch,
        Func<DirectoryObject, ObjectRowViewModel> createRow,
        Func<ObjectRowViewModel> createPlaceholder,
        IUiDispatcher dispatcher,
        int maxCachedPages = DefaultMaxCachedPages)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCachedPages, 2);
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _createRow = createRow ?? throw new ArgumentNullException(nameof(createRow));
        _createPlaceholder = createPlaceholder ?? throw new ArgumentNullException(nameof(createPlaceholder));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        PageSize = pageSize;
        MaxCachedPages = maxCachedPages;
        _count = Math.Max(0, first.TotalCount);
        _contextId = first.ContextId;
        if (first.Offset == 0 && first.Items.Count > 0)
        {
            StorePage(0, first.Items);
        }
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A page could not be read (raised on the UI thread); the rows stay placeholders until a refresh.</summary>
    public event EventHandler<Exception>? FetchFailed;

    /// <summary>Raised after a Reset (refresh, count change) - the owner restores its selection by DN.</summary>
    public event EventHandler? Reloaded;

    public int PageSize { get; }

    public int MaxCachedPages { get; }

    /// <summary>Number of objects in the list (the server's content count).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Pages currently held in memory.</summary>
    public int CachedPageCount
    {
        get
        {
            lock (_gate)
            {
                return _pages.Count;
            }
        }
    }

    /// <summary>Placeholders currently handed out (tests: bounded memory).</summary>
    internal int PlaceholderCount
    {
        get
        {
            lock (_gate)
            {
                return _placeholders.Count;
            }
        }
    }

    /// <summary>Pages waiting to be read.</summary>
    internal IReadOnlyList<int> PendingPages
    {
        get
        {
            lock (_gate)
            {
                return [.. _wanted];
            }
        }
    }

    /// <summary>Completes when no queued page is being read any more.</summary>
    public Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _pump;
            }
        }
    }

    bool IList.IsReadOnly => true;

    bool IList.IsFixedSize => false;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot { get; } = new();

    /// <summary>The row at <paramref name="index"/>: loaded, or a placeholder while its page is read.</summary>
    public ObjectRowViewModel this[int index]
    {
        get
        {
            lock (_gate)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
                var page = index / PageSize;
                Shown(page);
                if (_pages.TryGetValue(page, out var rows) && index - (page * PageSize) < rows.Length)
                {
                    Touch(page);
                    return rows[index - (page * PageSize)];
                }

                if (!_placeholders.TryGetValue(index, out var placeholder))
                {
                    placeholder = _createPlaceholder();
                    _placeholders[index] = placeholder;
                    _placeholderIndexes[placeholder] = index;
                }

                Want(page);
                return placeholder;
            }
        }
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    /// <summary>The loaded row at <paramref name="index"/> without reading anything (null when not loaded).</summary>
    public ObjectRowViewModel? GetLoaded(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _count)
            {
                return null;
            }

            var page = index / PageSize;
            return _pages.TryGetValue(page, out var rows) && index - (page * PageSize) < rows.Length ? rows[index - (page * PageSize)] : null;
        }
    }

    /// <summary>Index of a loaded row by its DN, or -1.</summary>
    public int IndexOfDn(string distinguishedName)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        lock (_gate)
        {
            return _indexByKey.TryGetValue(DnKey.For(distinguishedName), out var index) ? index : -1;
        }
    }

    /// <summary>All loaded rows (selection restore, tests).</summary>
    public IReadOnlyList<ObjectRowViewModel> LoadedRows
    {
        get
        {
            lock (_gate)
            {
                return [.. _pages.OrderBy(p => p.Key).SelectMany(p => p.Value)];
            }
        }
    }

    public int IndexOf(object? value)
    {
        if (value is not ObjectRowViewModel row)
        {
            return -1;
        }

        lock (_gate)
        {
            if (_placeholderIndexes.TryGetValue(row, out var placeholderIndex))
            {
                return placeholderIndex;
            }

            // Only the very row object held at that index: WPF compares the items it gets back.
            return _indexByKey.TryGetValue(row.Key, out var index) && ReferenceEquals(GetLoaded(index), row) ? index : -1;
        }
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    /// <summary>
    /// Enumerates the rows without reading anything: loaded rows and fresh placeholders (an enumeration - UI Automation,
    /// LINQ - must not page through the whole directory).
    /// </summary>
    public IEnumerator<ObjectRowViewModel> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return Peek(i) ?? _createPlaceholder();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        foreach (var row in this)
        {
            array.SetValue(row, index++);
        }
    }

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    /// <summary>Reads the page holding <paramref name="index"/> now (locate, type-to-find); no-op when loaded.</summary>
    public async Task EnsureLoadedAsync(int index)
    {
        int page, generation;
        lock (_gate)
        {
            if (_disposed || index < 0 || index >= _count)
            {
                return;
            }

            page = index / PageSize;
            if (_pages.ContainsKey(page))
            {
                Touch(page);
                return;
            }

            generation = _generation;
        }

        await LoadPageAsync(page, generation, reportFailure: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Positions on the first row whose sort value is at or after <paramref name="value"/> in the list order (the VLV
    /// assertion value) and loads its page. Returns its index, or null when no row follows the value.
    /// </summary>
    public async Task<int?> SeekAsync(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[]? context;
        int generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            context = _contextId;
            generation = _generation;
        }

        var result = await _fetch(new VirtualListRequest { SeekValue = value, Count = 1, ContextId = context }, _lifetime.Token).ConfigureAwait(true);
        var changed = false;
        lock (_gate)
        {
            if (generation != _generation || _disposed)
            {
                return null;
            }

            _contextId = result.ContextId ?? _contextId;
            if (result.TotalCount != _count)
            {
                changed = true;
                DropAll(result.TotalCount);
            }
        }

        if (changed)
        {
            await RaiseReloadedAsync().ConfigureAwait(true);
        }

        if (result.Offset < 0 || result.Offset >= Count || result.Items.Count == 0)
        {
            return null;
        }

        await EnsureLoadedAsync(result.Offset).ConfigureAwait(true);
        return result.Offset;
    }

    /// <summary>
    /// Type-to-find: the index of the first row (in the list order) whose sort value starts with <paramref name="prefix"/>,
    /// or null. Seeks with the prefix, then walks back over the rows just before the target that still start with it:
    /// in a descending list they sort above the prefix ("mark" before "m"), and some servers place values that continue
    /// with a non-ASCII letter below the prefix (Samba: "Kowalska Żaneta" &lt; "Kowalska"). At most
    /// <see cref="MaxSeekBackPages"/> pages are walked.
    /// </summary>
    /// <param name="prefix">Typed text.</param>
    /// <param name="valueOf">Sort value of a loaded row.</param>
    public async Task<int?> FindPrefixAsync(string prefix, Func<ObjectRowViewModel, string?> valueOf)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(valueOf);
        var text = prefix.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        bool Matches(ObjectRowViewModel? row) =>
            row is { IsPlaceholder: false } && valueOf(row) is { } value
            && System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IsPrefix(value, text, System.Globalization.CompareOptions.IgnoreCase);

        var target = await SeekAsync(text).ConfigureAwait(true);
        var start = target ?? Count;
        var found = target is { } t && Matches(GetLoaded(t)) ? t : (int?)null;
        for (var i = start - 1; i >= 0 && i >= start - (MaxSeekBackPages * PageSize); i--)
        {
            await EnsureLoadedAsync(i).ConfigureAwait(true);
            if (_disposed || !Matches(GetLoaded(i)))
            {
                break;
            }

            found = i;
        }

        return found;
    }

    /// <summary>
    /// Re-reads the most recently shown pages and the count (live updates, own changes) without the old server context.
    /// Rows keep their identity by DN (updated in place); the view gets one Reset.
    /// </summary>
    public async Task RefreshAsync()
    {
        int[] pages;
        int generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            generation = ++_generation;
            pages = [.. _shown.Order()];
            if (pages.Length == 0)
            {
                pages = [0];
            }

            _wanted.Clear();
        }

        var results = new List<(int Page, VirtualListPage Result)>(pages.Length);
        byte[]? context = null;
        foreach (var page in pages)
        {
            var result = await _fetch(
                new VirtualListRequest { Offset = page * PageSize, Count = PageSize, ContextId = context },
                _lifetime.Token).ConfigureAwait(true);
            context = result.ContextId ?? context;
            results.Add((page, result));
        }

        await _dispatcher.RunAsync(() =>
        {
            lock (_gate)
            {
                if (generation != _generation || _disposed)
                {
                    return;
                }

                _contextId = context ?? _contextId;
                DropAll(results[^1].Result.TotalCount);
                foreach (var (page, result) in results)
                {
                    if (result.Offset == page * PageSize && result.TotalCount == _count && result.Items.Count > 0)
                    {
                        StorePage(page, result.Items);
                    }
                }

                RaiseReset();
            }
        }).ConfigureAwait(true);
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            _wanted.Clear();
        }

        _lifetime.Cancel();
    }

    /// <summary>The loaded row or the handed-out placeholder at <paramref name="index"/>, without queueing anything.</summary>
    private ObjectRowViewModel? Peek(int index)
    {
        lock (_gate)
        {
            return GetLoaded(index) ?? (_placeholders.TryGetValue(index, out var placeholder) ? placeholder : null);
        }
    }

    private void Shown(int page)
    {
        if (_shown.Count > 0 && _shown[^1] == page)
        {
            return;
        }

        _shown.Remove(page);
        _shown.Add(page);
        if (_shown.Count > RefreshPages)
        {
            _shown.RemoveAt(0);
        }
    }

    private void Touch(int page)
    {
        if (_recentNodes.TryGetValue(page, out var node))
        {
            if (!ReferenceEquals(_recent.First, node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
            }
        }
        else
        {
            _recentNodes[page] = _recent.AddFirst(page);
        }
    }

    private void Want(int page)
    {
        if (_disposed)
        {
            return;
        }

        _wanted.Remove(page);
        _wanted.Add(page);
        if (_wanted.Count > MaxPendingPages)
        {
            _wanted.RemoveAt(0);
        }

        if (!_pumping)
        {
            _pumping = true;
            _pump = PumpAsync();
        }
    }

    private async Task PumpAsync()
    {
        // Leave the indexer (and the WPF layout pass that called it) before the first request.
        await Task.Yield();
        try
        {
            while (true)
            {
                int page, generation;
                lock (_gate)
                {
                    if (_disposed || _wanted.Count == 0)
                    {
                        _pumping = false;
                        return;
                    }

                    page = _wanted[^1];
                    _wanted.RemoveAt(_wanted.Count - 1);
                    if (_pages.ContainsKey(page))
                    {
                        continue;
                    }

                    generation = _generation;
                }

                await LoadPageAsync(page, generation, reportFailure: true).ConfigureAwait(true);
            }
        }
        catch
        {
            lock (_gate)
            {
                _pumping = false;
            }

            throw;
        }
    }

    private async Task LoadPageAsync(int page, int generation, bool reportFailure)
    {
        byte[]? context;
        lock (_gate)
        {
            context = _contextId;
        }

        VirtualListPage result;
        try
        {
            var request = new VirtualListRequest { Offset = page * PageSize, Count = PageSize, ContextId = context };
            try
            {
                result = await _fetch(request, _lifetime.Token).ConfigureAwait(true);
            }
            catch (DirectoryException) when (context is not null && !_lifetime.IsCancellationRequested)
            {
                // The server may have dropped its context (timeout, another connection): start a new one.
                result = await _fetch(request with { ContextId = null }, _lifetime.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is DirectoryException or ObjectDisposedException or InvalidOperationException)
        {
            if (reportFailure && !_lifetime.IsCancellationRequested)
            {
                await _dispatcher.RunAsync(() => FetchFailed?.Invoke(this, ex)).ConfigureAwait(true);
            }

            return;
        }

        var reloaded = false;
        await _dispatcher.RunAsync(() =>
        {
            lock (_gate)
            {
                if (generation != _generation || _disposed)
                {
                    return;
                }

                _contextId = result.ContextId ?? _contextId;
                var start = page * PageSize;
                if (result.TotalCount != _count || (result.Offset != start && start < result.TotalCount))
                {
                    // The container changed on the server meanwhile: every cached position may be off.
                    DropAll(result.TotalCount);
                    if (result.Offset == start && result.Items.Count > 0)
                    {
                        StorePage(page, result.Items);
                    }

                    RaiseReset();
                    reloaded = true;
                    return;
                }

                if (result.Offset != start || _pages.ContainsKey(page))
                {
                    return;
                }

                var rows = StorePage(page, result.Items);
                for (var i = 0; i < rows.Length; i++)
                {
                    if (_placeholders.Remove(start + i, out var placeholder))
                    {
                        _placeholderIndexes.Remove(placeholder);
                        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, rows[i], placeholder, start + i));
                    }
                }
            }
        }).ConfigureAwait(true);
        if (reloaded)
        {
            Reloaded?.Invoke(this, EventArgs.Empty);
        }
    }

    private ObjectRowViewModel[] StorePage(int page, IReadOnlyList<DirectoryObject> items)
    {
        var start = page * PageSize;
        var length = Math.Min(items.Count, Math.Max(0, _count - start));
        var rows = new ObjectRowViewModel[length];
        for (var i = 0; i < length; i++)
        {
            var obj = items[i];
            var key = DnKey.For(obj.DistinguishedName);
            if (_reuse.Remove(key, out var existing))
            {
                existing.Update(obj);
                rows[i] = existing;
            }
            else
            {
                rows[i] = _createRow(obj);
            }

            _indexByKey[rows[i].Key] = start + i;
        }

        _pages[page] = rows;
        Touch(page);
        Evict(page);
        return rows;
    }

    /// <summary>Drops the least recently shown pages beyond <see cref="MaxCachedPages"/> (never <paramref name="keep"/>).</summary>
    private void Evict(int keep)
    {
        while (_pages.Count > MaxCachedPages && _recent.Last is { } last)
        {
            var page = last.Value;
            _recent.RemoveLast();
            _recentNodes.Remove(page);
            if (page == keep)
            {
                _recentNodes[page] = _recent.AddFirst(page);
                continue;
            }

            if (_pages.Remove(page, out var rows))
            {
                var start = page * PageSize;
                for (var i = 0; i < rows.Length; i++)
                {
                    if (_indexByKey.TryGetValue(rows[i].Key, out var index) && index == start + i)
                    {
                        _indexByKey.Remove(rows[i].Key);
                    }
                }
            }
        }
    }

    /// <summary>Forgets every cached page and placeholder (their rows stay reusable by DN) and takes the new count.</summary>
    private void DropAll(int count)
    {
        var reuse = new Dictionary<string, ObjectRowViewModel>(StringComparer.Ordinal);
        foreach (var row in _pages.Values.SelectMany(r => r))
        {
            reuse[row.Key] = row;
        }

        _reuse = reuse;
        _pages.Clear();
        _recent.Clear();
        _recentNodes.Clear();
        _indexByKey.Clear();
        _placeholders.Clear();
        _placeholderIndexes.Clear();
        _wanted.Clear();
        _count = Math.Max(0, count);
    }

    private void RaiseReset()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private Task RaiseReloadedAsync() => _dispatcher.RunAsync(() =>
    {
        lock (_gate)
        {
            RaiseReset();
        }

        Reloaded?.Invoke(this, EventArgs.Empty);
    });
}
