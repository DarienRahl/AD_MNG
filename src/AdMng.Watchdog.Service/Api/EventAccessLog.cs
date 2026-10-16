namespace AdMng.Watchdog.Service.Api;

/// <summary>Accountability for viewing single events (spec 17.1, RODO): who opened which event – the numeric id or the
/// event GUID with the caller account, never a directory value.</summary>
public sealed partial class EventAccessLog(ILogger<EventAccessLog> logger)
{
    public void Viewed(long id, string account) => Log.ViewedById(logger, id, account);

    public void Viewed(Guid eventId, string account) => Log.ViewedByEventId(logger, eventId, account);

    private static partial class Log
    {
        [LoggerMessage(EventId = 2600, Level = LogLevel.Information, Message = "Event {Id} viewed by {Account}.")]
        public static partial void ViewedById(ILogger logger, long id, string account);

        [LoggerMessage(EventId = 2601, Level = LogLevel.Information, Message = "Event page {EventId} viewed by {Account}.")]
        public static partial void ViewedByEventId(ILogger logger, Guid eventId, string account);
    }
}
