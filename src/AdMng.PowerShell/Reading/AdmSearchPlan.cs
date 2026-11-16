using AdMng.Core.Directory;
using AdMng.Core.Model;

namespace AdMng.PowerShell.Reading;

/// <summary>The searches the read commands share.</summary>
internal static class AdmSearchPlan
{
    /// <summary>
    /// The search that finds the object named by <paramref name="identity"/> of <paramref name="kind"/>: a DN is read where it is
    /// (base scope, <paramref name="searchBase"/> does not apply), a GUID, SID or account name is searched below the base.
    /// </summary>
    public static DirectorySearch ForIdentity(
        IDirectoryReader reader,
        AdmIdentity identity,
        ObjectKind kind,
        IReadOnlyList<string> attributes,
        string? searchBase,
        SearchScope scope)
    {
        var kindFilter = AdmFilterBuilder.ForKind(kind);
        if (identity.Kind == AdmIdentityKind.DistinguishedName)
        {
            return new DirectorySearch
            {
                BaseDn = identity.Value,
                Scope = SearchScope.Base,
                Filter = kindFilter ?? AdmFilterBuilder.AnyObject,
                Attributes = attributes,
            };
        }

        return new DirectorySearch
        {
            BaseDn = searchBase ?? reader.Info.DefaultNamingContext,
            Scope = scope,
            Filter = AdmFilterBuilder.All(kindFilter, AdmFilterBuilder.ForIdentity(identity, kind)),
            Attributes = attributes,
        };
    }

    /// <summary>The first object of a search, or null; the search is abandoned after it.</summary>
    public static async Task<DirectoryObject?> FirstAsync(IAsyncEnumerable<DirectoryObject> source, CancellationToken cancellationToken)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            return item;
        }

        return null;
    }
}
