namespace AdMng.Watchdog.Service.Storage.Migrations;

/// <summary>
/// Schema version 3 (spec 16.2): how each event was shown in an alert – its notification severity and whether its actor
/// was still pending (<c>OutboxMessage.EventStates</c>). An aggregated alert carries the maximum severity of its events,
/// so whether a later plan for one event and target is a repeat (13.2 #1) is decided per event. Links of schema 2 and
/// older stay NULL: their message severity and <c>sent_with_pending</c> stand for every event.
/// </summary>
internal static class Migration0003OutboxEventState
{
    public const int Version = 3;

    public const string Name = "0003_outbox_event_state";

    public const string Sql = """
        -- AdMng Watchdog database, schema version 3: per-event state of alerts (outbox_events.severity / pending).
        ALTER TABLE outbox_events ADD COLUMN severity INTEGER;
        ALTER TABLE outbox_events ADD COLUMN pending INTEGER;
        """;
}
