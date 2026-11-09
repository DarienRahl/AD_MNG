using System.Collections.Concurrent;
using AdMng.Core.Directory;

namespace AdMng.Core.Batch.Creation;

/// <summary>
/// The lookup cache of one wizard window (docs/dev/phase3-bulk-create.md 2.4, 2.5): taken values, owners, resolved managers
/// and groups, possible existing accounts, the forest, the password policy and the run reads, so that a recompute after an
/// edit only reads values not seen before. Thread-safe; directory values in memory only (rule 1), owned and dropped by the
/// wizard (2.8). The contract has the constructor, <see cref="ForestReader"/>, <see cref="Refresh"/> and
/// <see cref="ToString"/>; the caches are B2's.
/// </summary>
/// <param name="forestReader">
/// The read-only global-catalog reader of the window's DC (2.2) for the forest-wide UPN, <c>mail</c> and
/// <c>proxyAddresses</c> searches of a multi-domain forest; null = no forest reader (scope <see cref="UniquenessScope.Domain"/>
/// or <see cref="UniquenessScope.ForestSingleDomain"/>).
/// </param>
public sealed class BulkCreateLookups(IDirectoryReader? forestReader = null)
{
    private readonly Lock _gate = new();
    private int _generation;
    private ConcurrentDictionary<string, object> _reads = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, LookupIndex> _indexes = new(StringComparer.Ordinal);
    private int _queries;

    /// <summary>The global-catalog reader (never a session: it is used for reads only, rule 4).</summary>
    public IDirectoryReader? ForestReader { get; } = forestReader;

    /// <summary>How often <see cref="Refresh"/> ran (a cache filled in an older generation is stale).</summary>
    public int Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>The directory searches and reads the caches ran since they were created (counts only; diagnostics and tests).</summary>
    public int QueryCount => Volatile.Read(ref _queries);

    /// <summary>Forgets every cached result (<em>Sprawdź ponownie</em>, and always before the plan is built).</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _generation++;
            _reads = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
            _indexes = new ConcurrentDictionary<string, LookupIndex>(StringComparer.Ordinal);
        }
    }

    /// <summary>The generation and whether a forest reader is set – never a cached value.</summary>
    public override string ToString() => $"Bulk create lookups (generation {Generation}, forest reader={(ForestReader is not null)})";

    /// <summary>
    /// The cached result of a run read (<paramref name="key"/>: a constant plus DNs or names, never printed), computed once per
    /// generation; a failed or cancelled read is forgotten, so the next call reads again.
    /// </summary>
    internal async Task<T> ReadOnceAsync<T>(string key, Func<Task<T>> read)
    {
        var reads = Reads;
        var lazy = (Lazy<Task<T>>)reads.GetOrAdd(key, _ => new Lazy<Task<T>>(() => CountedAsync(read)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            reads.TryRemove(new KeyValuePair<string, object>(key, lazy));
            throw;
        }
    }

    /// <summary>The value index of one uniqueness namespace (an attribute in a scope), created on first use in this generation.</summary>
    internal LookupIndex Index(string key) => Indexes.GetOrAdd(key, _ => new LookupIndex());

    /// <summary>Counts a search or read run outside <see cref="ReadOnceAsync{T}"/>.</summary>
    internal void CountQuery() => Interlocked.Increment(ref _queries);

    private ConcurrentDictionary<string, object> Reads
    {
        get
        {
            lock (_gate)
            {
                return _reads;
            }
        }
    }

    private ConcurrentDictionary<string, LookupIndex> Indexes
    {
        get
        {
            lock (_gate)
            {
                return _indexes;
            }
        }
    }

    private async Task<T> CountedAsync<T>(Func<Task<T>> read)
    {
        Interlocked.Increment(ref _queries);
        return await read().ConfigureAwait(false);
    }
}

/// <summary>An object that holds a value (owner of a taken name): its DN, name and deletion state – memory only, never printed.</summary>
internal sealed class LookupOwner(string distinguishedName, string name, bool isDeleted, bool isRecycled)
{
    public string DistinguishedName { get; } = distinguishedName;

    /// <summary>The RDN value (for a deleted object its last known RDN).</summary>
    public string Name { get; } = name;

    public bool IsDeleted { get; } = isDeleted;

    public bool IsRecycled { get; } = isRecycled;

    public override string ToString() => $"Owner (deleted={IsDeleted}, recycled={IsRecycled})";
}

/// <summary>
/// The values of one uniqueness namespace that the directory was asked about: the found owners per value (case-insensitive),
/// the values looked up and not found, and the prefixes (with suffixes) whose every value was read at once – a value under
/// such a prefix is known even when nobody holds it. Thread-safe.
/// </summary>
internal sealed class LookupIndex
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<LookupOwner>> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _absent = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Prefix, string Suffix)> _covered = [];
    private bool _complete;

    /// <summary>Every value of the namespace was read (a full listing, e.g. a container's children).</summary>
    public bool IsComplete
    {
        get
        {
            lock (_gate)
            {
                return _complete;
            }
        }
    }

    /// <summary>Whether the directory was asked about <paramref name="value"/> (directly, through a prefix or a full listing).</summary>
    public bool IsKnown(string value)
    {
        lock (_gate)
        {
            return _complete || _owners.ContainsKey(value) || _absent.Contains(value) || CoveredUnlocked(value);
        }
    }

    /// <summary>Whether the prefix query of (<paramref name="prefix"/>, <paramref name="suffix"/>) ran.</summary>
    public bool IsCovered(string prefix, string suffix)
    {
        lock (_gate)
        {
            return _complete || _covered.Any(c => string.Equals(c.Prefix, prefix, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Suffix, suffix, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The owners of <paramref name="value"/> (empty: free, when <see cref="IsKnown"/>).</summary>
    public IReadOnlyList<LookupOwner> Owners(string value)
    {
        lock (_gate)
        {
            return _owners.TryGetValue(value, out var owners) ? [.. owners] : [];
        }
    }

    /// <summary>The values held under (<paramref name="prefix"/>, <paramref name="suffix"/>) with their owners.</summary>
    public IReadOnlyList<(string Value, IReadOnlyList<LookupOwner> Owners)> ValuesUnder(string prefix, string suffix)
    {
        lock (_gate)
        {
            return [.. _owners
                .Where(p => p.Key.Length >= prefix.Length + suffix.Length
                    && p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && p.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(p => (p.Key, (IReadOnlyList<LookupOwner>)[.. p.Value]))];
        }
    }

    /// <summary>Records the owners found by a lookup and marks the other <paramref name="lookedUp"/> values as absent.</summary>
    public void Record(IEnumerable<string> lookedUp, IEnumerable<(string Value, LookupOwner Owner)> found)
    {
        lock (_gate)
        {
            AddUnlocked(found);
            foreach (var value in lookedUp)
            {
                if (!_owners.ContainsKey(value))
                {
                    _absent.Add(value);
                }
            }
        }
    }

    /// <summary>Records the result of a prefix query: every value under the prefix is known from now on.</summary>
    public void Cover(string prefix, string suffix, IEnumerable<(string Value, LookupOwner Owner)> found)
    {
        lock (_gate)
        {
            AddUnlocked(found);
            _covered.Add((prefix, suffix));
        }
    }

    /// <summary>Records a full listing of the namespace.</summary>
    public void Complete(IEnumerable<(string Value, LookupOwner Owner)> found)
    {
        lock (_gate)
        {
            AddUnlocked(found);
            _complete = true;
        }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            return $"Lookup index ({_owners.Count} taken, {_absent.Count} free, {_covered.Count} prefixes, complete={_complete})";
        }
    }

    private void AddUnlocked(IEnumerable<(string Value, LookupOwner Owner)> found)
    {
        foreach (var (value, owner) in found)
        {
            if (value.Length == 0)
            {
                continue;
            }

            if (!_owners.TryGetValue(value, out var owners))
            {
                _owners[value] = owners = [];
                _absent.Remove(value);
            }

            if (!owners.Any(o => string.Equals(o.DistinguishedName, owner.DistinguishedName, StringComparison.OrdinalIgnoreCase)))
            {
                owners.Add(owner);
            }
        }
    }

    private bool CoveredUnlocked(string value) =>
        _covered.Any(c => value.Length >= c.Prefix.Length + c.Suffix.Length
            && value.StartsWith(c.Prefix, StringComparison.OrdinalIgnoreCase)
            && value.EndsWith(c.Suffix, StringComparison.OrdinalIgnoreCase));
}
