namespace AdMng.Watchdog.Service.Storage.Migrations;

/// <summary>
/// Schema version 7 (U1c): <c>outbox.image_bytes</c> – the bytes of the inline images in the payload of a message to a
/// user, written on insert and set to 0 whenever the payload is dropped (sent) or the row purged. Their sum is capped by
/// <c>userMessages.maxStoredImageMb</c>; the images themselves live only in the payload (ADR-0008 as amended for U1c).
/// </summary>
internal static class Migration0007UserMessageImages
{
    public const int Version = 7;

    public const string Name = "0007_user_message_images";

    public const string Sql = """
        -- AdMng Watchdog database, schema version 7: bytes of inline images held in outbox payloads.
        ALTER TABLE outbox ADD COLUMN image_bytes INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX ix_outbox_image_bytes ON outbox(image_bytes) WHERE image_bytes > 0;
        """;
}
