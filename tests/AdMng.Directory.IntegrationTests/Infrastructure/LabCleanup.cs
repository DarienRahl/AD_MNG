using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// Removes test trees from the shared Samba lab without a server-side tree delete. Samba gives one LDAP request at most
/// 300 s and runs one write transaction at a time: a tree delete of a test OU with thousands of objects (two groups of
/// 1600 members and their 3200 contacts take ~5 minutes) fails with timeLimitExceeded, leaves the objects behind and
/// blocks every other suite's writes while it runs. Here the tree is listed with a paged search and deleted object by
/// object, deepest first, after the groups lost their members in one modify each – every request is short (~0.1 s on
/// the lab), so other suites' writes interleave. Also used by <c>AdMng.Watchdog.IntegrationTests</c> (linked file).
/// </summary>
internal static class LabCleanup
{
    /// <summary>Request timeout of cleanup sessions: generous, because another suite may be holding the DC.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Passes over a tree that keeps changing (a test still adding objects) before giving up.</summary>
    private const int MaxPasses = 5;

    /// <summary>Deletes <paramref name="dn"/> and everything below it. Returns the number of objects deleted (0 when missing).</summary>
    public static async Task<int> DeleteTreeAsync(IDirectorySession session, string dn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(dn);
        var deleted = 0;
        for (var pass = 1; pass <= MaxPasses; pass++)
        {
            var tree = await ListAsync(session, dn, cancellationToken);
            if (tree.Count == 0)
            {
                return deleted;
            }

            await ClearGroupsAsync(session, dn, cancellationToken);
            var blocked = false;

            // Deepest first: an object is deleted only after everything below it.
            foreach (var entry in tree.OrderByDescending(e => e.Rdns.Count))
            {
                try
                {
                    await session.DeleteAsync(entry.ToString(), subtree: false, cancellationToken);
                    deleted++;
                }
                catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
                {
                    // Deleted meanwhile (another cleanup).
                }
                catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NotAllowedOnNonLeaf && pass < MaxPasses)
                {
                    // A child appeared meanwhile (a test still finishing): the next pass lists the tree again.
                    blocked = true;
                }
            }

            if (!blocked)
            {
                return deleted;
            }
        }

        // Unreachable: the last pass lets NotAllowedOnNonLeaf through.
        throw new InvalidOperationException($"The test tree {dn} kept changing while it was deleted.");
    }

    /// <summary>The DNs of the tree (root included), from a paged subtree search; empty when the root does not exist.</summary>
    private static async Task<List<DistinguishedName>> ListAsync(IDirectorySession session, string dn, CancellationToken cancellationToken)
    {
        var tree = new List<DistinguishedName>();
        try
        {
            await foreach (var entry in session.SearchAsync(new DirectorySearch { BaseDn = dn, Scope = SearchScope.Subtree, PageSize = 1000 }, cancellationToken))
            {
                tree.Add(DistinguishedName.Parse(entry.DistinguishedName));
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            return [];
        }

        return tree;
    }

    /// <summary>
    /// Removes all members of the groups in the tree with one modify per group: deleting a member makes Samba rewrite the
    /// group's links, so a 1600-member group would be rewritten 1600 times.
    /// </summary>
    private static async Task ClearGroupsAsync(IDirectorySession session, string dn, CancellationToken cancellationToken)
    {
        var groups = new List<string>();
        await foreach (var group in session.SearchAsync(new DirectorySearch
        {
            BaseDn = dn,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Group), LdapFilter.Present(Attr.Member)),
            PageSize = 1000,
        }, cancellationToken))
        {
            groups.Add(group.DistinguishedName);
        }

        foreach (var group in groups)
        {
            try
            {
                await session.ModifyAsync(group, [AttributeModification.Delete(Attr.Member)], null, cancellationToken);
            }
            catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.NoSuchAttribute)
            {
                // Deleted or emptied meanwhile.
            }
        }
    }
}
