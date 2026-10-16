namespace AdMng.Presentation.Watchdog;

/// <summary>State of the Watchdog tab's connection to the service (shown in the connection banner).</summary>
public enum WatchdogConnectionState
{
    /// <summary>No service address in the settings: the address editor is shown.</summary>
    NotConfigured,

    Connecting,

    Connected,

    /// <summary>401: Kerberos failed (SPN, DNS A record, clock).</summary>
    Unauthorized,

    /// <summary>403: authenticated, but in no AD group with a watchdog role (or on the deny list).</summary>
    Forbidden,

    /// <summary>The service cannot be reached (DNS, firewall, service stopped) or did not answer in time.</summary>
    Unreachable,

    /// <summary>TLS: the service certificate is not trusted or does not match the pin.</summary>
    CertificateInvalid,

    /// <summary>The service speaks another API version.</summary>
    VersionMismatch,

    /// <summary>Any other failure (server error, unexpected answer).</summary>
    Error,
}
