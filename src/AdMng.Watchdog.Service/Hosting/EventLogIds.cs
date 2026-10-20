using AdMng.Watchdog.Core.Diagnostics;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Stable Application-log event ids (source "AdMng Watchdog", spec 19.8) for SCOM / Zabbix / SIEM – the values of
/// <see cref="WatchdogEventIds"/>, which the Core jobs use for the conditions they detect. The EventLog provider writes
/// <c>EventId.Id</c> as the event id, so only these ids are used for Warning/Error entries; Information-level diagnostics
/// use ids from 2000 up (console only – the EventLog provider logs Warning and above plus the Information entries of
/// <see cref="LoggingSetup.EventLogInformationCategories"/>: 1000, 1006, 2301 and the access log 2600/2601).
/// </summary>
public static class EventLogIds
{
    /// <summary>Information: service started (version, instance).</summary>
    public const int ServiceStarted = WatchdogEventIds.ServiceStarted;

    /// <summary>Error: directory unavailable (&gt; FailoverAfter); Warning: failover to another DC.</summary>
    public const int DirectoryUnavailable = WatchdogEventIds.DirectoryUnavailable;

    /// <summary>Error: delivery failing (channel id, error code).</summary>
    public const int DeliveryFailing = WatchdogEventIds.DeliveryFailing;

    /// <summary>Error: storage problem (free space, size cap, data directory ACL).</summary>
    public const int StorageProblem = WatchdogEventIds.StorageProblem;

    /// <summary>Warning: permission gap (self-check code).</summary>
    public const int PermissionGap = WatchdogEventIds.PermissionGap;

    /// <summary>Warning: attribution degraded / event-log gap (DC host, code).</summary>
    public const int AttributionDegraded = WatchdogEventIds.AttributionDegraded;

    /// <summary>Information: configuration changed (version, caller account).</summary>
    public const int ConfigurationChanged = WatchdogEventIds.ConfigurationChanged;

    /// <summary>Warning: runtime outdated / certificate expiring within 30 days.</summary>
    public const int RuntimeOrCertificate = WatchdogEventIds.RuntimeOrCertificate;

    /// <summary>Error: fatal start-up check failed (exit code in the message).</summary>
    public const int FatalStartupCheck = WatchdogEventIds.FatalStartupCheck;

    /// <summary>Information: TLS certificate fingerprint at every start (stage B).</summary>
    public const int CertificateFingerprint = WatchdogEventIds.CertificateFingerprint;

    /// <summary>Warning/Error: service account or keytab check failed (stage B).</summary>
    public const int ServiceAccountProblem = WatchdogEventIds.ServiceAccountProblem;

    /// <summary>Warning/Error: WEF collector problem (stage B).</summary>
    public const int CollectorProblem = WatchdogEventIds.CollectorProblem;

    /// <summary>Warning/Error: GPO content problem (stage B).</summary>
    public const int GpoContentProblem = WatchdogEventIds.GpoContentProblem;
}
