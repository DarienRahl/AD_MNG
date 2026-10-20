namespace AdMng.Watchdog.Service.Storage.Migrations;

/// <summary>
/// Schema version 4 (stage B design 7.7): <c>security_events</c> rebuilt for the collector mode – the logon session id of
/// a 4624 (<c>logon_id</c>, normalized <c>0x…</c>) and uniqueness on <c>(computer, record_id, time_created)</c> instead of
/// <c>(computer, record_id)</c>: OpenWEC may deliver a record twice, and a cleared log can restart the record numbers. The
/// table holds at most 30 days of reduced records on an upgraded 0.2.0 database and nothing on a new install, so it is
/// copied (forward-only, in the migration's transaction). Indexes for the stored lookups: the logon session per DC and
/// the failures per account and event id.
/// </summary>
internal static class Migration0004SecurityEventsV2
{
    public const int Version = 4;

    public const string Name = "0004_security_events_v2";

    public const string Sql = """
        -- AdMng Watchdog database, schema version 4: security_events v2 (logon_id, unique per computer + record id + time).
        CREATE TABLE security_events_v2 (
          id INTEGER PRIMARY KEY, computer TEXT NOT NULL, record_id INTEGER NOT NULL, source TEXT NOT NULL,
          event_code INTEGER NOT NULL, time_created INTEGER NOT NULL,
          target_sid TEXT, object_guid BLOB, member_sid TEXT, target_user TEXT, object_dn_norm TEXT,
          subject_sid TEXT, subject_logon_id TEXT, logon_id TEXT, data BLOB NOT NULL,
          UNIQUE (computer, record_id, time_created));
        INSERT INTO security_events_v2 (id, computer, record_id, source, event_code, time_created, target_sid, object_guid,
          member_sid, target_user, object_dn_norm, subject_sid, subject_logon_id, logon_id, data)
          SELECT id, computer, record_id, source, event_code, time_created, target_sid, object_guid,
            member_sid, target_user, object_dn_norm, subject_sid, subject_logon_id, NULL, data
          FROM security_events;
        DROP TABLE security_events;
        ALTER TABLE security_events_v2 RENAME TO security_events;
        CREATE INDEX ix_sec_target ON security_events(target_sid, time_created) WHERE target_sid IS NOT NULL;
        CREATE INDEX ix_sec_object ON security_events(object_guid, time_created) WHERE object_guid IS NOT NULL;
        CREATE INDEX ix_sec_code ON security_events(event_code, computer, time_created);
        CREATE INDEX ix_sec_time ON security_events(time_created);
        CREATE INDEX ix_sec_logon ON security_events(computer, logon_id) WHERE logon_id IS NOT NULL;
        CREATE INDEX ix_sec_user_code ON security_events(target_user, event_code, time_created) WHERE target_user IS NOT NULL;
        """;
}
