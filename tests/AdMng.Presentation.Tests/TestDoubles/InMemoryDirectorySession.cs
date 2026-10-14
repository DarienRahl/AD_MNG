using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Presentation.Tests.TestDoubles;

/// <summary>
/// Small in-memory <see cref="IDirectorySession"/> supporting what the presentation tests need: scoped searches with
/// filter evaluation, size limit, server sort, attribute projection, base reads, optional result throttling (to test
/// streaming and cancellation) and change notification callbacks. Writes are applied by test code via
/// <see cref="Put"/> / <see cref="Remove"/> (the change pipeline itself is faked by the planner/executor doubles).
/// </summary>
internal sealed class InMemoryDirectorySession : IDirectorySession
{
    private readonly ConcurrentDictionary<string, DirectoryObject> _objects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<DirectorySearch> _searches = new();
    private readonly List<(string Container, Action<string> Callback)> _watchers = [];
    private int _getCount;

    public InMemoryDirectorySession(SessionInfo? info = null, ISchemaCatalog? schema = null)
    {
        Info = info ?? DefaultInfo;
        Schema = schema ?? TestSchema.Create();
    }

    public static SessionInfo DefaultInfo { get; } = new()
    {
        Server = "dc01.contoso.test",
        Port = 389,
        DomainDnsName = "contoso.test",
        DomainNetBiosName = "CONTOSO",
        DefaultNamingContext = "DC=contoso,DC=test",
        ConfigurationNamingContext = "CN=Configuration,DC=contoso,DC=test",
        SchemaNamingContext = "CN=Schema,CN=Configuration,DC=contoso,DC=test",
        BoundIdentity = "CONTOSO\\helpdesk",
        BoundUserDn = "CN=Helpdesk,CN=Users,DC=contoso,DC=test",
        Authentication = AuthenticationMode.Credentials,
        Security = TransportSecurity.SignAndSeal,
    };

    public SessionInfo Info { get; }

    public ISchemaCatalog Schema { get; }

    /// <summary>Every search executed, in order.</summary>
    public IReadOnlyList<DirectorySearch> Searches => [.. _searches];

    /// <summary>Delay before each streamed result (simulates paging latency).</summary>
    public TimeSpan ResultDelay { get; set; }

    /// <summary>
    /// Evaluated when a search starts; a returned task makes that search pause after its first result until the task
    /// completes (or the search is cancelled). Lets tests hold one specific load in flight.
    /// </summary>
    public Func<DirectorySearch, Task?>? Hold { get; set; }

    /// <summary>
    /// Like <see cref="Hold"/>, but the search pauses before its first result - for base reads, whose callers stop
    /// enumerating after one result.
    /// </summary>
    public Func<DirectorySearch, Task?>? HoldBeforeResults { get; set; }

    /// <summary>Thrown by the next search (then cleared) - e.g. to simulate an unsupported sort control.</summary>
    public Func<DirectorySearch, Exception?>? FailSearch { get; set; }

    /// <summary>
    /// Thrown after the last result of a matching search (not cleared) - like AD ending a search with
    /// "size limit exceeded" after it returned the first entries.
    /// </summary>
    public Func<DirectorySearch, Exception?>? FailAfterResults { get; set; }

    public bool SupportsWatch { get; set; }

    /// <summary>Effective permissions per DN (null or no entry: unknown, like a DC that cannot evaluate them).</summary>
    public Func<string, EffectivePermissions?>? Permissions { get; set; }

    /// <summary>DNs whose effective permissions were read, in order.</summary>
    public ConcurrentQueue<string> PermissionReads { get; } = new();

    /// <summary>
    /// When set, disposing a watch waits for this task first (the real watcher abandons its notification search,
    /// which can take a while) - lets tests start new loads while the previous watch is still being disposed.
    /// </summary>
    public Task? WatchDisposeGate { get; set; }

    /// <summary>What a search with more matches than <see cref="DirectorySearch.SizeLimit"/> does.</summary>
    public SizeLimitBehavior SizeLimitBehavior { get; set; } = SizeLimitBehavior.Truncate;

    public bool Disposed { get; private set; }

    public int Count => _objects.Count;

    public void Put(DirectoryObject obj) => _objects[DnKey(obj.DistinguishedName)] = obj;

    public void Remove(string dn) => _objects.TryRemove(DnKey(dn), out _);

    public DirectoryObject? Find(string dn) => _objects.TryGetValue(DnKey(dn), out var o) ? o : null;

    /// <summary>Simulates a server change notification for the watched container of <paramref name="dn"/>.</summary>
    public void RaiseChange(string dn)
    {
        List<Action<string>> callbacks;
        lock (_watchers)
        {
            callbacks = [.. _watchers.Select(w => w.Callback)];
        }

        foreach (var callback in callbacks)
        {
            callback(dn);
        }
    }

    public async IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _searches.Enqueue(search);
        var hold = Hold?.Invoke(search);
        var holdBefore = HoldBeforeResults?.Invoke(search);
        if (FailSearch?.Invoke(search) is { } failure)
        {
            FailSearch = null;
            throw failure;
        }

        await Task.Yield();
        if (holdBefore is not null)
        {
            await holdBefore.WaitAsync(cancellationToken);
        }

        var all = Query(search).ToList();
        var exceeded = search.SizeLimit > 0 && all.Count > search.SizeLimit;
        var results = exceeded ? all.Take(search.SizeLimit).ToList() : all;
        if (exceeded && SizeLimitBehavior == SizeLimitBehavior.ThrowBeforeEntries)
        {
            throw SizeLimitExceeded();
        }

        var count = 0;
        foreach (var obj in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ResultDelay > TimeSpan.Zero)
            {
                await Task.Delay(ResultDelay, cancellationToken);
            }

            yield return Project(obj, search.Attributes);
            count++;
            if (count == 1 && hold is not null)
            {
                await hold.WaitAsync(cancellationToken);
            }
        }

        if (exceeded && SizeLimitBehavior == SizeLimitBehavior.ThrowAfterEntries)
        {
            throw SizeLimitExceeded();
        }

        if (FailAfterResults?.Invoke(search) is { } late)
        {
            throw late;
        }
    }

    public Task<VirtualListPage> GetVirtualListPageAsync(DirectorySearch search, VirtualListRequest window, CancellationToken cancellationToken)
    {
        var all = Query(search).Take(search.SizeLimit > 0 ? search.SizeLimit : int.MaxValue).ToList();
        var items = all.Skip(window.Offset).Take(window.Count).Select(o => Project(o, search.Attributes)).ToList();
        return Task.FromResult(new VirtualListPage(items, window.Offset, all.Count, null));
    }

    /// <summary>Number of base reads (<see cref="GetAsync"/>).</summary>
    public int GetCount => _getCount;

    public Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _getCount);
        var obj = Find(distinguishedName);
        return Task.FromResult(obj is null ? null : Project(obj, attributes));
    }

    public async IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var value in Find(distinguishedName)?.GetStrings(attribute) ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }

    public Task<EffectivePermissions> GetEffectivePermissionsAsync(string distinguishedName, CancellationToken cancellationToken)
    {
        PermissionReads.Enqueue(distinguishedName);
        return Task.FromResult(Permissions?.Invoke(distinguishedName) ?? EffectivePermissions.Unknown);
    }

    public Task ModifyAsync(string distinguishedName, IReadOnlyList<AttributeModification> modifications, ModifyOptions? options, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Writes go through the change pipeline doubles.");

    public Task AddAsync(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<object>> attributes, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Writes go through the change pipeline doubles.");

    public Task DeleteAsync(string distinguishedName, bool subtree, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Writes go through the change pipeline doubles.");

    public Task<string> MoveAsync(string distinguishedName, string? newParentDn, string? newRdn, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Writes go through the change pipeline doubles.");

    public Task ResetPasswordAsync(string distinguishedName, string newPassword, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Writes go through the change pipeline doubles.");

    public Task RestoreDeletedAsync(string deletedObjectDn, string targetDn, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IAsyncDisposable?> WatchAsync(string containerDn, Action<string> onChanged, CancellationToken cancellationToken)
    {
        if (!SupportsWatch)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        var entry = (containerDn, onChanged);
        lock (_watchers)
        {
            _watchers.Add(entry);
        }

        return Task.FromResult<IAsyncDisposable?>(new Subscription(async () =>
        {
            if (WatchDisposeGate is { } gate)
            {
                await gate;
            }

            lock (_watchers)
            {
                _watchers.Remove(entry);
            }
        }));
    }

    public int WatcherCount
    {
        get
        {
            lock (_watchers)
            {
                return _watchers.Count;
            }
        }
    }

    /// <summary>Containers of the active watches.</summary>
    public IReadOnlyList<string> WatchedContainers
    {
        get
        {
            lock (_watchers)
            {
                return [.. _watchers.Select(w => w.Container)];
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private IEnumerable<DirectoryObject> Query(DirectorySearch search)
    {
        if (!DistinguishedName.TryParse(search.BaseDn, out var baseDn))
        {
            throw new DirectoryException(DirectoryErrorKind.InvalidSyntax, "Invalid base DN.");
        }

        if (Find(search.BaseDn) is null)
        {
            throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "No such object.") { LdapResultCode = 32 };
        }

        var matches = _objects.Values.Where(o => InScope(o.Dn, baseDn, search.Scope) && LdapFilterEvaluator.Matches(search.Filter, o));
        if (search.Sort is { } sort)
        {
            var comparer = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);
            matches = sort.Descending
                ? matches.OrderByDescending(o => o.GetString(sort.Attribute) ?? string.Empty, comparer)
                : matches.OrderBy(o => o.GetString(sort.Attribute) ?? string.Empty, comparer);
        }
        else
        {
            // Directory order is not alphabetical: use insertion-independent but stable DN order.
            matches = matches.OrderBy(o => o.DistinguishedName.Length).ThenBy(o => o.DistinguishedName, StringComparer.Ordinal);
        }

        return matches;
    }

    private static DirectoryException SizeLimitExceeded() =>
        new(DirectoryErrorKind.SizeLimitExceeded, "Size limit exceeded.") { LdapResultCode = 4 };

    private static bool InScope(DistinguishedName dn, DistinguishedName baseDn, SearchScope scope) => scope switch
    {
        SearchScope.Base => dn.Equals(baseDn),
        SearchScope.OneLevel => dn.Parent is { } p && p.Equals(baseDn),
        _ => dn.Equals(baseDn) || dn.IsDescendantOf(baseDn),
    };

    private static DirectoryObject Project(DirectoryObject obj, IReadOnlyList<string> attributes)
    {
        if (attributes.Contains("*"))
        {
            return obj;
        }

        var wanted = new HashSet<string>(attributes, StringComparer.OrdinalIgnoreCase);
        return new DirectoryObject(obj.DistinguishedName, obj.Attributes.Values.Where(a => wanted.Contains(a.Name)));
    }

    private static string DnKey(string dn) =>
        (DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString() : dn).ToUpperInvariant();

    private sealed class Subscription(Func<Task> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(dispose());
    }
}

/// <summary>How the in-memory DC handles a search that matches more objects than its size limit.</summary>
internal enum SizeLimitBehavior
{
    /// <summary>Returns the first N objects and completes normally.</summary>
    Truncate,

    /// <summary>Returns the first N objects, then fails with sizeLimitExceeded (AD with a streaming client).</summary>
    ThrowAfterEntries,

    /// <summary>Fails with sizeLimitExceeded before returning anything (a client that throws on the page response).</summary>
    ThrowBeforeEntries,
}

/// <summary>Fluent creation of directory objects for tests.</summary>
internal static class Obj
{
    public static DirectoryObject Create(string dn, params (string Attribute, object[] Values)[] attributes)
    {
        var name = DistinguishedName.Parse(dn).Name;
        var list = new List<AttributeValues> { new(Attr.Name, [name]), new(Attr.DistinguishedName, [dn]) };
        list.AddRange(attributes.Select(a => new AttributeValues(a.Attribute, a.Values)));
        return new DirectoryObject(dn, list);
    }

    public static (string, object[]) A(string attribute, params object[] values) => (attribute, values);
}
