using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;

namespace AdMng.Presentation.Infrastructure;

/// <summary>
/// Batched reads of many objects by DN (group members, memberships, batch targets): one subtree search per
/// <see cref="BatchSize"/> DNs with an OR filter on distinguishedName instead of one base read per object.
/// </summary>
internal static class DirectoryLookup
{
    /// <summary>DNs per OR filter (keeps filters well below AD's 10 MB / MaxQueryDuration limits).</summary>
    public const int BatchSize = 100;

    /// <summary>
    /// Reads the objects with the given DNs (duplicates ignored). Objects outside the domain partition (e.g. groups of
    /// another domain of the forest) or deleted meanwhile are missing from the result.
    /// </summary>
    /// <returns>Found objects keyed by <see cref="DnKey"/>.</returns>
    public static async Task<Dictionary<string, DirectoryObject>> ReadManyAsync(
        IDirectoryReader session,
        IEnumerable<string> distinguishedNames,
        IReadOnlyList<string> attributes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        var result = new Dictionary<string, DirectoryObject>(StringComparer.Ordinal);
        var unique = distinguishedNames
            .Where(dn => DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot)
            .DistinctBy(DnKey.For, StringComparer.Ordinal)
            .ToList();
        foreach (var chunk in unique.Chunk(BatchSize))
        {
            var search = new DirectorySearch
            {
                BaseDn = session.Info.DefaultNamingContext,
                Scope = SearchScope.Subtree,
                Filter = LdapFilter.Or(chunk.Select(dn => LdapFilter.Equal(Attr.DistinguishedName, dn))),
                Attributes = attributes,
                PageSize = BatchSize,
            };
            await foreach (var obj in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
            {
                result[DnKey.For(obj.DistinguishedName)] = obj;
            }
        }

        return result;
    }

    /// <summary>Reads all values of an attribute, completing ranged (>1500 values) results with ranged retrieval.</summary>
    public static async Task<IReadOnlyList<string>> AllValuesAsync(
        IDirectoryReader session,
        DirectoryObject obj,
        string attribute,
        CancellationToken cancellationToken)
    {
        if (!obj.TryGet(attribute, out var values))
        {
            return [];
        }

        if (!values.Name.Contains(";range=", StringComparison.OrdinalIgnoreCase) || values.Name.EndsWith("-*", StringComparison.Ordinal))
        {
            return values.Strings;
        }

        var all = new List<string>();
        await foreach (var value in session.GetAllValuesAsync(obj.DistinguishedName, attribute, cancellationToken).ConfigureAwait(false))
        {
            all.Add(value);
        }

        return all;
    }
}
