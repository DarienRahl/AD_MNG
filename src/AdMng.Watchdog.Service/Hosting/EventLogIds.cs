namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Stable Application-log event ids (source "AdMng Watchdog", spec 19.8) for SCOM / Zabbix / SIEM. The EventLog provider
/// writes <c>EventId.Id</c> as the event id, so only these ids are used for Warning/Error entries; Information-level
/// diagnostics use ids from 2000 up (console only – the EventLog provider logs Warning and above).
/// </summary>
public static class EventLogIds
{
    /// <summary>Information: service started (version, instance).</summary>
    public const int ServiceStarted = 1000;

    /// <summary>Error: directory unavailable (&gt; FailoverAfter); Warning: failover to another DC.</summary>
    public const int DirectoryUnavailable = 1001;

    /// <summary>Error: delivery failing (channel id, error code).</summary>
    public const int DeliveryFailing = 1002;

    /// <summary>Error: storage problem (free space, size cap, data directory ACL).</summary>
    public const int StorageProblem = 1003;

    /// <summary>Warning: permission gap (self-check code).</summary>
    public const int PermissionGap = 1004;

    /// <summary>Warning: attribution degraded / event-log gap (DC host, code).</summary>
    public const int AttributionDegraded = 1005;

    /// <summary>Information: configuration changed (version, caller account).</summary>
    public const int ConfigurationChanged = 1006;

    /// <summary>Warning: runtime outdated / certificate expiring within 30 days.</summary>
    public const int RuntimeOrCertificate = 1007;

    /// <summary>Error: fatal start-up check failed (exit code in the message).</summary>
    public const int FatalStartupCheck = 1010;
}
