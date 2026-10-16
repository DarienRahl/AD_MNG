namespace AdMng.Watchdog.Service.Storage.Migrations;

/// <summary>
/// Schema version 1 (spec 16.2). The migration scripts live in C# (instead of embedded <c>.sql</c> resources) because
/// the service project file is scaffold-owned; <see cref="MigrationRunner"/> applies <see cref="Sql"/> in one
/// transaction followed by <c>PRAGMA user_version = 1</c>.
/// </summary>
internal static class Migration0001Initial
{
    public const int Version = 1;

    public const string Name = "0001_initial";

    public const string Sql = """
        -- AdMng Watchdog database, schema version 1 (spec section 16.2).
        -- Applied by MigrationRunner in one transaction followed by PRAGMA user_version = 1.
        -- Times are INTEGER Unix milliseconds UTC, GUIDs BLOB(16) (Guid.ToByteArray()), enums INTEGER (stable values),
        -- payloads BLOB = Deflate(JSON) (BlobCodec). Secret values exist only as Data Protection blobs in `secrets`.
        -- Lookup columns (sid, actor_sid, member_sid, target_sid, subject_sid) hold upper-case SIDs, *_norm columns
        -- normalized upper-case DNs, tags / matched_rules / target_user lower-case text – the payloads keep the originals.

        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);                -- installation id, last backup …

        CREATE TABLE config (
          id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL, json TEXT NOT NULL,   -- never contains secret values
          updated_at INTEGER NOT NULL, updated_by TEXT NOT NULL);
        CREATE TABLE config_audit (
          id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, by_sid TEXT, by_account TEXT NOT NULL,
          version INTEGER NOT NULL, summary TEXT NOT NULL);
        CREATE TABLE secrets (name TEXT PRIMARY KEY, protected BLOB NOT NULL,
          binding TEXT NOT NULL,                           -- destination fingerprint (section 18), not secret
          updated_at INTEGER NOT NULL);

        CREATE TABLE dc_state (
          domain TEXT PRIMARY KEY, dc_host TEXT NOT NULL, ds_service_name TEXT NOT NULL, invocation_id BLOB NOT NULL,
          highest_usn INTEGER NOT NULL, last_poll_at INTEGER, last_change_at INTEGER, last_full_sync_at INTEGER,
          baseline_completed_at INTEGER, memberships_changed_at INTEGER);
        CREATE TABLE dc_watermarks (                       -- failback without resync (10.3)
          ds_service_name_norm TEXT NOT NULL, invocation_id BLOB NOT NULL, highest_usn INTEGER NOT NULL,
          updated_at INTEGER NOT NULL, PRIMARY KEY (ds_service_name_norm, invocation_id)) WITHOUT ROWID;

        CREATE TABLE snapshot_objects (
          object_guid BLOB PRIMARY KEY, dn TEXT NOT NULL, dn_norm TEXT NOT NULL, kind INTEGER NOT NULL,
          sid TEXT, sam TEXT, name TEXT, display_name TEXT, primary_group_rid INTEGER,
          usn_created INTEGER NOT NULL, usn_changed INTEGER NOT NULL, when_created INTEGER,
          is_deleted INTEGER NOT NULL DEFAULT 0, deleted_at INTEGER, creation_pending_until INTEGER,
          attributes BLOB NOT NULL, updated_at INTEGER NOT NULL);
        CREATE INDEX ix_snapshot_pending ON snapshot_objects(creation_pending_until) WHERE creation_pending_until IS NOT NULL;
        CREATE INDEX ix_snapshot_kind ON snapshot_objects(kind) WHERE kind IN (11, 15, 16);   -- DC, PKI, Configuration rows
        CREATE INDEX ix_snapshot_sid ON snapshot_objects(sid) WHERE sid IS NOT NULL;
        CREATE INDEX ix_snapshot_dn ON snapshot_objects(dn_norm);
        CREATE INDEX ix_snapshot_pgr ON snapshot_objects(primary_group_rid)
          WHERE primary_group_rid IS NOT NULL AND primary_group_rid NOT IN (513, 514, 515);
        CREATE INDEX ix_snapshot_deleted ON snapshot_objects(deleted_at) WHERE is_deleted = 1;

        CREATE TABLE snapshot_members (
          group_guid BLOB NOT NULL, member_key TEXT NOT NULL, member_guid BLOB, member_sid TEXT, member_dn TEXT NOT NULL,
          PRIMARY KEY (group_guid, member_key)) WITHOUT ROWID;
        CREATE INDEX ix_members_key ON snapshot_members(member_key);

        CREATE TABLE privilege_state (id INTEGER PRIMARY KEY CHECK (id = 1), state BLOB NOT NULL, computed_at INTEGER NOT NULL);

        CREATE TABLE events (
          id INTEGER PRIMARY KEY AUTOINCREMENT,            -- AUTOINCREMENT: cursors never reuse ids after retention deletes
          event_id BLOB NOT NULL UNIQUE, change_set_id BLOB,
          kind INTEGER NOT NULL, severity INTEGER NOT NULL, object_kind INTEGER NOT NULL,
          object_guid BLOB, object_dn TEXT NOT NULL, object_dn_norm TEXT NOT NULL, previous_dn_norm TEXT,   -- EventQuery.Subtree
          search_text TEXT NOT NULL,                       -- lower-case name|sam|dn|actor|workstation|ip (updated with attribution)
          tags TEXT NOT NULL DEFAULT '',                   -- "|privileged|serviceaccount|" for EventQuery.Tags
          detected_at INTEGER NOT NULL, originating_at INTEGER, originating_dc TEXT, source INTEGER NOT NULL,
          actor_status INTEGER NOT NULL, actor_sid TEXT, next_attribution_at INTEGER,
          rules_state INTEGER NOT NULL DEFAULT 0, matched_rules TEXT,
          acked_at INTEGER, acked_by TEXT, ack_comment TEXT,
          payload BLOB NOT NULL);                          -- full WatchEvent (changes, attribution …)
        CREATE INDEX ix_events_detected ON events(detected_at);
        CREATE INDEX ix_events_kind ON events(kind, detected_at);
        CREATE INDEX ix_events_object ON events(object_guid, detected_at);
        CREATE INDEX ix_events_actor ON events(actor_sid, detected_at) WHERE actor_sid IS NOT NULL;
        CREATE INDEX ix_events_changeset ON events(change_set_id) WHERE change_set_id IS NOT NULL;
        CREATE INDEX ix_events_attr_due ON events(next_attribution_at) WHERE actor_status = 1;
        CREATE INDEX ix_events_unevaluated ON events(id) WHERE rules_state = 0;

        CREATE TABLE pending_notifications (
          id INTEGER PRIMARY KEY AUTOINCREMENT, event_id INTEGER NOT NULL REFERENCES events(id) ON DELETE CASCADE,
          rule_id TEXT NOT NULL, target BLOB NOT NULL, target_key TEXT NOT NULL, severity INTEGER NOT NULL,
          bucket_key TEXT NOT NULL, due_at INTEGER NOT NULL, hold_until INTEGER,
          is_follow_up INTEGER NOT NULL DEFAULT 0, reply_to TEXT, created_at INTEGER NOT NULL,
          UNIQUE (event_id, target_key, is_follow_up));
        CREATE INDEX ix_pending_bucket ON pending_notifications(bucket_key, due_at);

        CREATE TABLE outbox (
          id INTEGER PRIMARY KEY AUTOINCREMENT, message_id TEXT NOT NULL UNIQUE,
          channel_type INTEGER NOT NULL, channel_id TEXT NOT NULL, kind INTEGER NOT NULL, rule_id TEXT, severity INTEGER NOT NULL,
          status INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL,
          next_attempt_at INTEGER NOT NULL, lease_until INTEGER, sent_at INTEGER,
          last_error_code TEXT, last_error TEXT, subject TEXT, recipient_count INTEGER NOT NULL DEFAULT 0,
          recipients TEXT, target_key TEXT, sent_with_pending INTEGER NOT NULL DEFAULT 0,
          payload BLOB);                                   -- NULL after successful send (only metadata kept, 16.3)
        CREATE INDEX ix_outbox_due ON outbox(status, next_attempt_at);
        CREATE INDEX ix_outbox_sent ON outbox(channel_id, rule_id, sent_at);
        CREATE TABLE outbox_events (
          outbox_id INTEGER NOT NULL REFERENCES outbox(id) ON DELETE CASCADE,
          event_id INTEGER NOT NULL REFERENCES events(id) ON DELETE CASCADE,
          PRIMARY KEY (outbox_id, event_id)) WITHOUT ROWID;
        CREATE INDEX ix_outbox_events_event ON outbox_events(event_id);

        CREATE TABLE security_events (
          id INTEGER PRIMARY KEY, computer TEXT NOT NULL, record_id INTEGER NOT NULL, source TEXT NOT NULL,
          event_code INTEGER NOT NULL, time_created INTEGER NOT NULL,
          target_sid TEXT, object_guid BLOB, member_sid TEXT, target_user TEXT, object_dn_norm TEXT,
          subject_sid TEXT, subject_logon_id TEXT, data BLOB NOT NULL,
          UNIQUE (computer, record_id));
        CREATE INDEX ix_sec_target ON security_events(target_sid, time_created) WHERE target_sid IS NOT NULL;
        CREATE INDEX ix_sec_object ON security_events(object_guid, time_created) WHERE object_guid IS NOT NULL;
        CREATE INDEX ix_sec_code ON security_events(event_code, computer, time_created);
        CREATE INDEX ix_sec_time ON security_events(time_created);
        CREATE TABLE eventlog_cursors (
          source TEXT PRIMARY KEY, last_record_id INTEGER NOT NULL, bookmark_xml TEXT, oldest_seen INTEGER, updated_at INTEGER NOT NULL);

        CREATE TABLE expiry_notices (
          object_guid BLOB NOT NULL, kind INTEGER NOT NULL, expires_raw INTEGER NOT NULL, threshold_days INTEGER NOT NULL,
          notified_at INTEGER NOT NULL, PRIMARY KEY (object_guid, kind, expires_raw, threshold_days)) WITHOUT ROWID;
        CREATE TABLE upcoming_expiries (
          kind INTEGER NOT NULL, object_guid BLOB NOT NULL, expires_at INTEGER NOT NULL, payload BLOB NOT NULL,
          PRIMARY KEY (kind, object_guid));
        CREATE TABLE digest_runs (
          id INTEGER PRIMARY KEY AUTOINCREMENT, period_start INTEGER NOT NULL, period_end INTEGER NOT NULL,
          created_at INTEGER NOT NULL, event_count INTEGER NOT NULL, outbox_ids TEXT NOT NULL);
        """;
}
