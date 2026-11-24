using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;

namespace AdMng.Core.Directory;

/// <summary>
/// Reads many objects by DN in batches: one subtree search per <see cref="Size"/> names with an OR filter on
/// <c>distinguishedName</c> instead of one base read per object. The one form of this read - the program's lists (members,
/// memberships, batch targets) and the PowerShell window's <c>Get-AdmGroupMember</c> both build it here (audit ps-commands-9: the
/// command kept a copy).
/// </summary>
public static class DistinguishedNameBatch
{
    /// <summary>DNs per OR filter (keeps filters well below AD's 10 MB / MaxQueryDuration limits).</summary>
    public const int Size = 100;

    /// <summary>The search for the objects named by <paramref name="distinguishedNames"/> (at most <see cref="Size"/>) in the domain partition.</summary>
    public static DirectorySearch Search(IDirectoryReader reader, IEnumerable<string> distinguishedNames, IReadOnlyList<string> attributes)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        ArgumentNullException.ThrowIfNull(attributes);
        return new DirectorySearch
        {
            BaseDn = reader.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.Or(distinguishedNames.Select(dn => LdapFilter.Equal(Attr.DistinguishedName, dn))),
            Attributes = attributes,
            PageSize = Size,
        };
    }
}
