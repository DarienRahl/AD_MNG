using System.Runtime.CompilerServices;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Testing;

/// <summary>
/// The global catalog of a fake forest (F3, docs/dev/phase3-bulk-create.md 2.2, 2.13): a read-only reader over several fake
/// domains with <see cref="SessionInfo.IsGlobalCatalog"/> = true, one namespace as a real GC presents it. A subtree search
/// runs in the domain that holds its base (the one with the longest naming context at or above it) and in every other
/// domain whose naming context is below the base (from that naming context), so the empty base – the whole GC – and the
/// forest root's DN – 2.2's fallback when a server refuses the empty base – both reach every domain; a one-level search
/// also returns the naming-context heads of the domains directly below the base. A base-scope search or a read with the
/// empty base is the RootDSE read of the forest root domain; any other base read or ranged read goes to the domain that
/// holds the DN. A non-empty base in no domain is <c>NoSuchObject</c>. It is not a session (no writes), like the GC
/// session the wizard keeps as <see cref="IDirectoryReader"/>. The partial attribute set is not modelled (every attribute
/// is returned) and only the domain naming contexts are searched (configuration and schema stay with their domain's base).
/// </summary>
public sealed class FakeGlobalCatalogReader : IDirectoryReader
{
    private readonly IReadOnlyList<FakeDirectorySession> _domains;
    private int _searches;

    /// <param name="domains">The domains of the forest; the first is the forest root (its facts and schema are reported).</param>
    public FakeGlobalCatalogReader(params FakeDirectorySession[] domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        if (domains.Length == 0)
        {
            throw new ArgumentException("A forest has at least one domain.", nameof(domains));
        }

        _domains = domains;
        Info = domains[0].Info with { IsGlobalCatalog = true };
    }

    public SessionInfo Info { get; }

    public ISchemaCatalog Schema => _domains[0].Schema;

    /// <summary>The domains of the forest.</summary>
    public IReadOnlyList<FakeDirectorySession> Domains => _domains;

    /// <summary>The number of searches run (query budgets of 2.4).</summary>
    public int SearchCount => Volatile.Read(ref _searches);

    public async IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        Interlocked.Increment(ref _searches);
        if (search.BaseDn.Length == 0 && search.Scope == SearchScope.Base)
        {
            // The RootDSE (a base read of ""), not the whole catalog.
            if (await _domains[0].GetAsync(string.Empty, search.Attributes, cancellationToken).ConfigureAwait(false) is { } rootDse)
            {
                yield return rootDse;
            }

            yield break;
        }

        var baseDn = ParseBase(search.BaseDn);
        var owner = search.BaseDn.Length == 0 ? null : Owner(baseDn);
        if (owner is null && !baseDn.IsRoot)
        {
            throw NoSuchObject(search.BaseDn);   // the base must exist; only the empty base is the whole catalog
        }

        var below = _domains.Where(d => !ReferenceEquals(d, owner) && NamingContext(d).IsDescendantOf(baseDn)).ToList();

        if (owner is not null)
        {
            await foreach (var item in owner.SearchAsync(search, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            if (search.Scope == SearchScope.Base)
            {
                yield break;
            }
        }

        foreach (var domain in below)
        {
            var head = NamingContext(domain);
            DirectorySearch? scoped = search.Scope switch
            {
                SearchScope.Subtree => search with { BaseDn = head.ToString(), Scope = SearchScope.Subtree },
                SearchScope.OneLevel when head.Parent == baseDn || (baseDn.IsRoot && !_domains.Any(d => head.IsDescendantOf(NamingContext(d)))) =>
                    search with { BaseDn = head.ToString(), Scope = SearchScope.Base },
                _ => null,
            };
            if (scoped is null)
            {
                continue;
            }

            await foreach (var item in domain.SearchAsync(scoped, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    public Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        return distinguishedName.Length == 0
            ? _domains[0].GetAsync(string.Empty, attributes, cancellationToken)
            : DomainOf(distinguishedName).GetAsync(distinguishedName, attributes, cancellationToken);
    }

    public IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, CancellationToken cancellationToken) =>
        DomainOf(distinguishedName).GetAllValuesAsync(distinguishedName, attribute, cancellationToken);

    /// <summary>The domain count only.</summary>
    public override string ToString() => $"FakeGlobalCatalogReader ({_domains.Count} domains)";

    private static DistinguishedName NamingContext(FakeDirectorySession domain) => DistinguishedName.Parse(domain.Info.DefaultNamingContext);

    private static DistinguishedName ParseBase(string baseDn) =>
        DistinguishedName.TryParse(baseDn, out var dn) ? dn : throw NoSuchObject(baseDn);

    /// <summary>The domain that holds <paramref name="dn"/>: the longest naming context at or above it (a child domain's
    /// objects are below the forest root's DN too).</summary>
    private FakeDirectorySession? Owner(DistinguishedName dn) => _domains
        .Where(d => NamingContext(d) is var root && (dn == root || dn.IsDescendantOf(root)))
        .OrderByDescending(d => NamingContext(d).Rdns.Count)
        .FirstOrDefault();

    private FakeDirectorySession DomainOf(string distinguishedName)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        return DistinguishedName.TryParse(distinguishedName, out var dn) && Owner(dn) is { } domain ? domain : throw NoSuchObject(distinguishedName);
    }

    private static DirectoryException NoSuchObject(string distinguishedName) =>
        new(DirectoryErrorKind.NoSuchObject, "The object is in no domain of the fake forest.")
        {
            LdapResultCode = 32,
            DistinguishedName = distinguishedName,
        };
}
