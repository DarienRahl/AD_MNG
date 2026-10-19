namespace AdMng.Watchdog.Service.Storage.Migrations;

/// <summary>
/// Schema version 2 (spec 16.2): the event's own severity (<c>WatchEvent.OwnSeverity</c>, 12.1/12.2 – the input of every
/// rule match) is kept apart from the evaluated <c>severity</c> (own severity raised by rule overrides – feed, digest,
/// counts). The payload blobs are deflated and cannot be rewritten in SQL, so the own severity lives in its own column.
/// Existing rows keep it NULL: <see cref="EventRows.Read"/> then takes the payload's severity, which every write stores
/// together with the <c>severity</c> column – their current severity, as the own severity of an evaluated row is not
/// known any more. The migration only changes the schema (no row is rewritten): a <c>UPDATE events SET own_severity =
/// severity</c> would copy the whole table into the WAL at service start (≈ its size on disk, kept until the next
/// truncating checkpoint) and fail the start on a nearly full volume, for exactly the value the read already falls back to.
/// </summary>
internal static class Migration0002OwnSeverity
{
    public const int Version = 2;

    public const string Name = "0002_own_severity";

    public const string Sql = """
        -- AdMng Watchdog database, schema version 2: events.own_severity (the event's own severity, 12.2).
        -- Rows of schema 1 stay NULL (read as their payload severity); the column is filled by the next attribution update.
        ALTER TABLE events ADD COLUMN own_severity INTEGER;
        """;
}
