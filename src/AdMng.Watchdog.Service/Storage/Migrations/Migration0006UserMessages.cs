namespace AdMng.Watchdog.Service.Storage.Migrations;

/// <summary>
/// Schema version 6 (README U1b): the audit of messages to users submitted by AD Manager (<c>user_messages</c>, one row per
/// message id – the idempotency key): who and when, the recipient count, a SHA-256 of the recipient set and of the request
/// (idempotency check) and the outbox row. Never an address, a subject or a body: those live only in the outbox payload,
/// which is dropped once the message is sent (ADR-0008 as amended for U1b). Purged after
/// <c>retention.userMessageAuditDays</c>.
/// </summary>
internal static class Migration0006UserMessages
{
    public const int Version = 6;

    public const string Name = "0006_user_messages";

    public const string Sql = """
        -- AdMng Watchdog database, schema version 6: audit of messages to users.
        CREATE TABLE user_messages (
          message_id TEXT PRIMARY KEY, submitted_at INTEGER NOT NULL, by_sid TEXT, by_account TEXT NOT NULL,
          recipient_count INTEGER NOT NULL, recipients_hash TEXT NOT NULL, content_hash TEXT NOT NULL, outbox_id INTEGER);
        CREATE INDEX ix_user_messages_submitted ON user_messages(submitted_at);
        """;
}
