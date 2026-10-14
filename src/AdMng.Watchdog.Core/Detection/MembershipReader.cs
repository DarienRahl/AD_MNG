using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Reads a group's <c>member</c> values (Extended DN) into <see cref="MemberReference"/>s (section 10.8): the values of
/// the search entry, or – when the server returned a ranged attribute (<c>member;range=0-1499</c>, Windows beyond
/// MaxValRange) – all values through ranged retrieval with Extended DN.
/// </summary>
internal sealed class MembershipReader
{
    /// <summary>The members of the group in <paramref name="entry"/>.</summary>
    public async Task<IReadOnlyList<MemberReference>> ReadAsync(IDirectorySession session, DirectoryObject entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.TryGet(Attr.Member, out var values))
        {
            return [];
        }

        IEnumerable<string> all = values.Strings;
        if (values.Name.Contains(";range=", StringComparison.OrdinalIgnoreCase))
        {
            var ranged = new List<string>();
            await foreach (var value in session.GetAllValuesAsync(entry.DistinguishedName, Attr.Member, new AttributeReadOptions { ExtendedDn = true }, cancellationToken)
                .ConfigureAwait(false))
            {
                ranged.Add(value);
            }

            all = ranged;
        }

        return all.Select(ValueCanonicalizer.ToMemberReference).DistinctBy(m => m.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>True when both lists contain the same keys and DNs (no write needed).</summary>
    public static bool AreSame(IReadOnlyList<MemberReference> a, IReadOnlyList<MemberReference> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var byKey = a.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        return b.All(m => byKey.TryGetValue(m.Key, out var other) && string.Equals(other.Dn, m.Dn, StringComparison.Ordinal));
    }

    /// <summary>True when both lists contain the same keys.</summary>
    public static bool HaveSameKeys(IReadOnlyList<MemberReference> a, IReadOnlyList<MemberReference> b) =>
        a.Count == b.Count && a.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.Select(m => m.Key));
}
