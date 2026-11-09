using System.Runtime.CompilerServices;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Testing;

/// <summary>
/// The global catalog of a fake forest (F3, docs/dev/phase3-bulk-create.md 2.2, 2.13): a read-only reader over several fake
/// domains with <see cref="SessionInfo.IsGlobalCatalog"/> = true. A search with the empty base (the whole GC) runs on every
/// domain (subtree of its naming context) and concatenates the results; a search, base read or ranged read below a
/// domain's naming context goes to that domain. It is not a session (no writes), like the GC session the wizard keeps as
/// <see cref="IDirectoryReader"/>. The partial attribute set is not modelled: every attribute is returned.
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
        if (search.BaseDn.Length == 0)
        {
            foreach (var domain in _domains)
            {
                var scoped = search with { BaseDn = domain.Info.DefaultNamingContext, Scope = SearchScope.Subtree };
                await foreach (var item in domain.SearchAsync(scoped, cancellationToken).ConfigureAwait(false))
                {
                    yield return item;
                }
            }

            yield break;
        }

        await foreach (var item in DomainOf(search.BaseDn).SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken) =>
        DomainOf(distinguishedName).GetAsync(distinguishedName, attributes, cancellationToken);

    public IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, CancellationToken cancellationToken) =>
        DomainOf(distinguishedName).GetAllValuesAsync(distinguishedName, attribute, cancellationToken);

    /// <summary>The domain count only.</summary>
    public override string ToString() => $"FakeGlobalCatalogReader ({_domains.Count} domains)";

    private FakeDirectorySession DomainOf(string distinguishedName)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        if (DistinguishedName.TryParse(distinguishedName, out var dn))
        {
            foreach (var domain in _domains)
            {
                var root = DistinguishedName.Parse(domain.Info.DefaultNamingContext);
                if (dn == root || dn.IsDescendantOf(root))
                {
                    return domain;
                }
            }
        }

        throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The object is in no domain of the fake forest.")
        {
            LdapResultCode = 32,
            DistinguishedName = distinguishedName,
        };
    }
}
