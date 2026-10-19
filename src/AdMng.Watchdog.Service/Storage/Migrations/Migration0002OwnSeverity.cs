namespace AdMng.Watchdog.Service.Storage.Migrations;

/// <summary>
/// Schema version 2 (spec 16.2): the event's own severity (<c>WatchEvent.OwnSeverity</c>, 12.1/12.2 – the input of every
/// rule match) is kept apart from the evaluated <c>severity</c> (own severity raised by rule overrides – feed, digest,
/// counts). The payload blobs are deflated and cannot be rewritten in SQL, so the own severity lives in its own column;
/// existing rows get their current severity (the own severity of an evaluated row is not known any more).
/// </summary>
internal static class Migration0002OwnSeverity
{
    public const int Version = 2;

    public const string Name = "0002_own_severity";

    public const string Sql = """
        -- AdMng Watchdog database, schema version 2: events.own_severity (the event's own severity, 12.2).
        ALTER TABLE events ADD COLUMN own_severity INTEGER;
        UPDATE events SET own_severity = severity;
        """;
}
