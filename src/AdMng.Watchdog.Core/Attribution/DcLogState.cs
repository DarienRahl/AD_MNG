namespace AdMng.Watchdog.Core.Attribution;

/// <summary>In-memory read, lookup and coverage history of one DC log (<see cref="DcLogHealthTracker"/>).</summary>
internal sealed class DcLogState
{
    public DateTimeOffset? LastReadAt { get; set; }

    public long? LastRecordId { get; set; }

    public string? ErrorCode { get; set; }

    public DateTimeOffset? LastFailureAt { get; set; }

    public DateTimeOffset? OverwrittenAt { get; set; }

    /// <summary>Time of the latest 1100 (event log service shut down – a reboot): logon ids before it are stale.</summary>
    public DateTimeOffset? ShutdownAt { get; set; }

    /// <summary>System/Computer as the DC writes it (store lookups by computer).</summary>
    public string? Computer { get; set; }

    public List<(DateTimeOffset At, TimeSpan Duration, bool TimedOut)> Lookups { get; } = [];

    public List<(DateTimeOffset At, bool Attributed)> Outcomes { get; } = [];

    public void Prune(DateTimeOffset since)
    {
        Lookups.RemoveAll(l => l.At < since);
        Outcomes.RemoveAll(o => o.At < since);
    }
}
