using System.Runtime.CompilerServices;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.PowerShell.Reading;

/// <summary>Members of a group that were listed but could not be read (another domain, deleted meanwhile, no permission).</summary>
internal sealed class AdmMemberReport
{
    private readonly List<string> _samples = [];

    /// <summary>How many members could not be read.</summary>
    public int Missing { get; private set; }

    /// <summary>The first few of their distinguished names.</summary>
    public IReadOnlyList<string> Samples => _samples;

    public void Add(string distinguishedName)
    {
        Missing++;
        if (_samples.Count < 3)
        {
            _samples.Add(distinguishedName);
        }
    }
}

/// <summary>
/// The members of a group, read the way the program's Members page reads them (docs/dev/r1-powershell-window.md 12.2): the direct
/// members are the values of <c>member</c> (ranged retrieval completes a list beyond 1500 values), looked up in batches with one
/// OR search per <see cref="BatchSize"/> DNs; the nested members are the one search the program's "All (nested)" list sends -
/// <c>(memberOf:1.2.840.113556.1.4.1941:=group)</c>, the nesting evaluated by the domain controller (cycles included), not by
/// a second engine here. Both stream: nothing is collected.
/// </summary>
internal static class AdmGroupMembers
{
    /// <summary>DNs per OR filter (the program's own batch size; it keeps the filter far below the DC's limits).</summary>
    public const int BatchSize = 100;

    /// <summary>The search for the direct and indirect members of <paramref name="groupDn"/>, groups included unless <paramref name="excludeGroups"/>.</summary>
    public static DirectorySearch NestedSearch(IDirectoryReader reader, string groupDn, IReadOnlyList<string> attributes, bool excludeGroups) => new()
    {
        BaseDn = reader.Info.DefaultNamingContext,
        Scope = SearchScope.Subtree,
        Filter = AdmFilterBuilder.All(
            LdapFilter.InChain(Attr.MemberOf, groupDn),
            excludeGroups ? LdapFilter.Not(ObjectKind.Group.ToLdapFilter()) : null),
        Attributes = attributes,
    };

    /// <summary>The direct members, as objects; members that could not be read go to <paramref name="report"/>.</summary>
    public static async IAsyncEnumerable<DirectoryObject> DirectAsync(
        IDirectoryReader reader,
        string groupDn,
        IReadOnlyList<string> attributes,
        bool excludeGroups,
        int limit,
        AdmMemberReport report,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batch = new List<string>(BatchSize);
        var yielded = 0;
        await foreach (var memberDn in reader.GetAllValuesAsync(groupDn, Attr.Member, cancellationToken).ConfigureAwait(false))
        {
            batch.Add(memberDn);
            if (batch.Count < BatchSize)
            {
                continue;
            }

            await foreach (var member in LookUpAsync(reader, batch, attributes, report, cancellationToken).ConfigureAwait(false))
            {
                if (excludeGroups && member.Kind == ObjectKind.Group)
                {
                    continue;
                }

                yield return member;
                if (limit > 0 && ++yielded >= limit)
                {
                    yield break;
                }
            }

            batch.Clear();
        }

        if (batch.Count == 0)
        {
            yield break;
        }

        await foreach (var member in LookUpAsync(reader, batch, attributes, report, cancellationToken).ConfigureAwait(false))
        {
            if (excludeGroups && member.Kind == ObjectKind.Group)
            {
                continue;
            }

            yield return member;
            if (limit > 0 && ++yielded >= limit)
            {
                yield break;
            }
        }
    }

    private static async IAsyncEnumerable<DirectoryObject> LookUpAsync(
        IDirectoryReader reader,
        IReadOnlyList<string> distinguishedNames,
        IReadOnlyList<string> attributes,
        AdmMemberReport report,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dn in distinguishedNames)
        {
            if (DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot)
            {
                wanted[Key(parsed.ToString())] = dn;
            }
            else
            {
                report.Add(dn);
            }
        }

        if (wanted.Count == 0)
        {
            yield break;
        }

        var search = new DirectorySearch
        {
            BaseDn = reader.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.Or(wanted.Values.Select(dn => LdapFilter.Equal(Attr.DistinguishedName, dn))),
            Attributes = attributes,
            PageSize = BatchSize,
        };
        await foreach (var member in reader.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            wanted.Remove(Key(DistinguishedName.TryParse(member.DistinguishedName, out var found) ? found.ToString() : member.DistinguishedName));
            yield return member;
        }

        foreach (var missing in wanted.Values)
        {
            report.Add(missing);
        }
    }

    private static string Key(string distinguishedName) => distinguishedName.ToUpperInvariant();
}
