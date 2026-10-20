namespace AdMng.Watchdog.Service.Storage.Migrations;

/// <summary>
/// Schema version 5 (stage B design 8.8): the latest content snapshot per GPO (<c>gpo_content</c>, keyed by the GUID of the
/// GPC's <c>cn</c>; inventory and settings as deflated JSON – masked settings such as GPP <c>cpassword</c> carry no value,
/// ADR-0008 as amended by ADR-0009) and the read queue (<c>gpo_content_queue</c>, one row per GPO). Beyond the columns of
/// the design the queue keeps the GPC <c>objectGUID</c>, the triggering event's change set (the content event's
/// <c>ChangeSetId</c>), the enqueue time and the first attempt (start of the SYSVOL lag tolerance).
/// </summary>
internal static class Migration0005GpoContent
{
    public const int Version = 5;

    public const string Name = "0005_gpo_content";

    public const string Sql = """
        -- AdMng Watchdog database, schema version 5: GPO content snapshots and read queue.
        CREATE TABLE gpo_content (
          gpo_guid BLOB PRIMARY KEY, dc TEXT NOT NULL, gpc_version INTEGER, gpt_version INTEGER, read_at INTEGER NOT NULL,
          state INTEGER NOT NULL, inventory BLOB NOT NULL, settings BLOB NOT NULL, deleted_at INTEGER);
        CREATE INDEX ix_gpo_content_deleted ON gpo_content(deleted_at) WHERE deleted_at IS NOT NULL;
        CREATE TABLE gpo_content_queue (
          gpo_guid BLOB PRIMARY KEY, reason INTEGER NOT NULL, due_at INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
          object_guid BLOB, change_set_id BLOB, enqueued_at INTEGER NOT NULL, first_attempt_at INTEGER);
        """;
}
