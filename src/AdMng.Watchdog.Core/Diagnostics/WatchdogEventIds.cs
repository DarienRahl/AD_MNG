namespace AdMng.Watchdog.Core.Diagnostics;

/// <summary>
/// The stable Application-log event ids (source "AdMng Watchdog", spec 19.8) for SCOM / Zabbix / SIEM. The Core jobs
/// detect most of these conditions and log them with these ids; the service's <c>EventLogIds</c> are the same
/// constants. Messages never carry directory values (codes, channel ids, DC hosts and counts only).
/// </summary>
public static class WatchdogEventIds
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

    /// <summary>Information: SHA-256 fingerprint of the TLS certificate, at every start (stage B design 6.3; not directory data).</summary>
    public const int CertificateFingerprint = 1008;

    /// <summary>Warning/Error: a check of the service account or its keytab failed (code) – stage B design 4.7, 15.</summary>
    public const int ServiceAccountProblem = 1011;

    /// <summary>Warning/Error: WEF collector problem (DC host, code) – stage B design 7.9, 15.</summary>
    public const int CollectorProblem = 1012;

    /// <summary>Warning/Error: GPO content problem (GPO GUID, code) – stage B design 8, 15.</summary>
    public const int GpoContentProblem = 1013;

    /// <summary>Error: fatal start-up check failed (exit code in the message).</summary>
    public const int FatalStartupCheck = 1010;
}
