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

    /// <summary>Forgets every cached result (<em>Sprawdź ponownie</em>, and always before the plan is built).</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _generation++;
        }
    }

    /// <summary>The generation and whether a forest reader is set – never a cached value.</summary>
    public override string ToString() => $"Bulk create lookups (generation {Generation}, forest reader={(ForestReader is not null)})";
}
