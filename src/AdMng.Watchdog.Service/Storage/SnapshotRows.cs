using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>The <c>snapshot_objects</c> table: identity columns plus the <see cref="StoredSnapshotPayload"/> blob.</summary>
internal static class SnapshotRows
{
    /// <summary>Columns to select for <see cref="Read"/>.</summary>
    public const string Columns =
        "object_guid, dn, kind, sid, sam, name, display_name, usn_created, usn_changed, when_created, is_deleted, deleted_at, " +
        "creation_pending_until, attributes";

    /// <summary>Inserts or replaces one snapshot row (an upsert of a deleted GUID makes it live again).</summary>
    public static void Upsert(SqliteConnection connection, SqliteTransaction transaction, SnapshotObject snapshot, DateTimeOffset now)
    {
        // SIDs are stored upper-case (canonical "S-1-…"), so FindBySid is case-insensitive through the index.
        SqliteValues.Execute(
            connection,
            transaction,
            """
            INSERT OR REPLACE INTO snapshot_objects (object_guid, dn, dn_norm, kind, sid, sam, name, display_name, primary_group_rid,
              usn_created, usn_changed, when_created, is_deleted, deleted_at, creation_pending_until, attributes, updated_at)
            VALUES ($guid, $dn, $dnNorm, $kind, $sid, $sam, $name, $displayName, $pgr, $usnCreated, $usnChanged, $whenCreated,
              $isDeleted, $deletedAt, $pending, $attributes, $now);
            """,
            ("$guid", SqliteValues.ToBlob(snapshot.ObjectGuid)),
            ("$dn", snapshot.Dn),
            ("$dnNorm", SqliteValues.NormalizeDn(snapshot.Dn)),
            ("$kind", (int)snapshot.Kind),
            ("$sid", SqliteValues.SidOrNull(snapshot.Sid)),
            ("$sam", SqliteValues.OrNull(snapshot.SamAccountName)),
            ("$name", SqliteValues.OrNull(snapshot.Name)),
            ("$displayName", SqliteValues.OrNull(snapshot.DisplayName)),
            ("$pgr", SqliteValues.OrNull(PrimaryGroupRid(snapshot))),
            ("$usnCreated", snapshot.UsnCreated),
            ("$usnChanged", snapshot.UsnChanged),
            ("$whenCreated", SqliteValues.ToUnixMsOrNull(snapshot.WhenCreated)),
            ("$isDeleted", snapshot.IsDeleted ? 1 : 0),
            ("$deletedAt", SqliteValues.ToUnixMsOrNull(snapshot.DeletedAt)),
            ("$pending", SqliteValues.ToUnixMsOrNull(snapshot.CreationPendingUntil)),
            ("$attributes", BlobCodec.EncodeSnapshot(snapshot)),
            ("$now", SqliteValues.ToUnixMs(now)));
    }

    /// <summary>Maps a row selected with <see cref="Columns"/> (SIDs come back in their canonical upper-case form).</summary>
    public static SnapshotObject Read(SqliteDataReader reader)
    {
        var payload = BlobCodec.DecodeSnapshot((byte[])reader.GetValue(13));
        return new SnapshotObject
        {
            ObjectGuid = SqliteValues.GuidFrom(reader, 0),
            Dn = reader.GetString(1),
            Kind = (WatchObjectKind)reader.GetInt32(2),
            Sid = SqliteValues.StringOrNull(reader, 3),
            SamAccountName = SqliteValues.StringOrNull(reader, 4),
            Name = SqliteValues.StringOrNull(reader, 5),
            DisplayName = SqliteValues.StringOrNull(reader, 6),
            UsnCreated = reader.GetInt64(7),
            UsnChanged = reader.GetInt64(8),
            WhenCreated = SqliteValues.FromUnixMsOrNull(reader, 9),
            IsDeleted = reader.GetInt32(10) != 0,
            DeletedAt = SqliteValues.FromUnixMsOrNull(reader, 11),
            CreationPendingUntil = SqliteValues.FromUnixMsOrNull(reader, 12),
            ObjectClasses = payload.ObjectClasses,
            Attributes = payload.Attributes,
        };
    }

    /// <summary>primaryGroupID RID from the snapshot attributes (users, computers, service accounts carry it).</summary>
    public static long? PrimaryGroupRid(SnapshotObject snapshot) =>
        snapshot.Attributes.TryGetValue(Attr.PrimaryGroupId, out var attribute) && attribute.Values.Count > 0
        && uint.TryParse(attribute.Values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rid)
            ? rid
            : null;
}
