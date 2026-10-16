namespace AdMng.Presentation.Watchdog;

/// <summary>"Historia zmian (Watchdog)" of one object.</summary>
/// <param name="ObjectGuid">objectGUID of the object (the event filter).</param>
/// <param name="Name">Its name for the filter chip (display only).</param>
public sealed record WatchdogHistoryRequest(Guid ObjectGuid, string? Name);
