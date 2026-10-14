namespace AdMng.Watchdog.Service.Hosting;

/// <summary>How API callers authenticate (spec 17.3).</summary>
public enum WatchdogAuthenticationMode
{
    /// <summary>Kerberos through Negotiate (Kestrel) or HTTP.sys Kerberos – production.</summary>
    Negotiate,

    /// <summary>Fixed identity for loopback calls; only in the Development environment, never as a Windows service.</summary>
    Development,

    /// <summary>Header-based test identities, registered by the service tests only.</summary>
    Test,
}
