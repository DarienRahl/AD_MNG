using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>Paged searches of the scanners (domain NC, subtree) and the tags derived from a search result.</summary>
internal static class ScanQueries
{
    public const int PageSize = 1000;

    /// <summary>Subtree search of the domain NC.</summary>
    public static IAsyncEnumerable<DirectoryObject> SearchAsync(ScanContext context, string filter, IEnumerable<string> attributes, CancellationToken cancellationToken) =>
        context.Session.SearchAsync(new DirectorySearch
        {
            BaseDn = context.Facts.DomainDn,
            Filter = filter,
            Attributes = [.. ScanEventWriter.IdentityAttributes.Concat(attributes).Distinct(StringComparer.OrdinalIgnoreCase)],
            PageSize = PageSize,
        }, cancellationToken);

    /// <summary>Counts the results of a search (objectGUID only).</summary>
    public static async Task<int> CountAsync(ScanContext context, string filter, CancellationToken cancellationToken)
    {
        var count = 0;
        await foreach (var _ in context.Session.SearchAsync(new DirectorySearch
        {
            BaseDn = context.Facts.DomainDn,
            Filter = filter,
            Attributes = [Attr.ObjectGuid],
            PageSize = PageSize,
        }, cancellationToken).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    /// <summary>privileged (PrivilegeState) and serviceAccount (MSA kinds, SPN, ServiceAccountSubtrees) of a result.</summary>
    public static IReadOnlyList<string> Tags(ScanContext context, DirectoryObject entry, ObjectRef target)
    {
        var tags = new List<string>(2);
        if (target.ObjectGuid is { } guid && context.IsPrivileged(guid.ToString("N")))
        {
            tags.Add(EventTags.Privileged);
        }

        if (IsServiceAccount(context, entry, target))
        {
            tags.Add(EventTags.ServiceAccount);
        }

        return tags;
    }

    /// <summary>MSA/gMSA/dMSA, a user with a servicePrincipalName, or an object below ServiceAccountSubtrees.</summary>
    public static bool IsServiceAccount(ScanContext context, DirectoryObject entry, ObjectRef target) =>
        target.Kind == WatchObjectKind.ServiceAccount
        || entry.GetStrings(Attr.ServicePrincipalName).Count > 0
        || (DistinguishedName.TryParse(target.Dn, out var dn) && context.Settings.ServiceAccountSubtrees.Any(s => dn == s || dn.IsDescendantOf(s)));
}
