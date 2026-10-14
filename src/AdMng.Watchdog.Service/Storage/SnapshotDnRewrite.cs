using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Model;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// Applies a <see cref="DnRewrite"/> (a container was moved or renamed, spec 10.7): snapshot rows and member DNs strictly
/// below the old suffix get the new suffix; rows outside it (also look-alikes such as <c>OU=Sales Abroad</c>) stay.
/// Snapshot rows are prefiltered by their normalized DN suffix; member DNs are not normalized, so every member row is
/// checked (rewrites are rare – container moves and renames only).
/// </summary>
internal static class SnapshotDnRewrite
{
    public static void Apply(SqliteConnection connection, SqliteTransaction transaction, DnRewrite rewrite)
    {
        ArgumentNullException.ThrowIfNull(rewrite);
        if (!DistinguishedName.TryParse(rewrite.OldSuffix, out var oldBase) || oldBase.IsRoot
            || !DistinguishedName.TryParse(rewrite.NewSuffix, out var newBase) || newBase.IsRoot)
        {
            return;
        }

        var suffix = "," + SqliteValues.NormalizeDn(rewrite.OldSuffix);
        var snapshots = SqliteValues.Query(
            connection,
            transaction,
            "SELECT object_guid, dn FROM snapshot_objects WHERE substr(dn_norm, -length($suffix)) = $suffix;",
            r => ((byte[])r.GetValue(0), r.GetString(1)),
            ("$suffix", suffix));
        foreach (var (guid, dn) in snapshots)
        {
            if (DistinguishedName.TryParse(dn, out var parsed) && parsed.IsDescendantOf(oldBase))
            {
                var rebased = Rebase(parsed, oldBase, newBase);
                SqliteValues.Execute(
                    connection,
                    transaction,
                    "UPDATE snapshot_objects SET dn = $dn, dn_norm = $dnNorm WHERE object_guid = $guid;",
                    ("$guid", guid),
                    ("$dn", rebased),
                    ("$dnNorm", SqliteValues.NormalizeDn(rebased)));
            }
        }

        var members = SqliteValues.Query(
            connection,
            transaction,
            "SELECT group_guid, member_key, member_dn FROM snapshot_members;",
            r => ((byte[])r.GetValue(0), r.GetString(1), r.GetString(2)));
        foreach (var (group, key, dn) in members)
        {
            if (DistinguishedName.TryParse(dn, out var parsed) && parsed.IsDescendantOf(oldBase))
            {
                SqliteValues.Execute(
                    connection,
                    transaction,
                    "UPDATE snapshot_members SET member_dn = $dn WHERE group_guid = $group AND member_key = $key;",
                    ("$group", group),
                    ("$key", key),
                    ("$dn", Rebase(parsed, oldBase, newBase)));
            }
        }
    }

    /// <summary>The DN with the RDNs below <paramref name="oldBase"/> placed under <paramref name="newBase"/>.</summary>
    public static string Rebase(DistinguishedName dn, DistinguishedName oldBase, DistinguishedName newBase)
    {
        var result = newBase;
        for (var i = dn.Rdns.Count - oldBase.Rdns.Count - 1; i >= 0; i--)
        {
            result = result.Child(dn.Rdns[i].Type, dn.Rdns[i].Value);
        }

        return result.ToString();
    }
}
