namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Authenticity check of every parsed record (section 11.1): accepted only when <c>Provider/@Name</c> is
/// <c>Microsoft-Windows-Security-Auditing</c> (or <c>Microsoft-Windows-Eventlog</c> for 1100/1102/1104) and
/// <c>System/Computer</c> is a domain controller the watchdog knows – also in ForwardedEvents mode, where a collector
/// could otherwise relay records of any machine. Dropped records are counted by the engine (health Warning
/// <c>eventlog.&lt;source&gt;.foreignRecords</c>).
/// </summary>
public static class EventSourceGuard
{
    /// <summary>True when the record may be used.</summary>
    /// <param name="record">The parsed record.</param>
    /// <param name="isKnownDomainController">Whether a host name is a known DC (<see cref="IDomainControllerCatalog"/>).</param>
    public static bool Accept(ParsedSecurityRecord record, Func<string, bool> isKnownDomainController)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(isKnownDomainController);
        var expectedProvider = SecurityEventCatalog.EventLogServiceCodes.Contains(record.Event.EventCode)
            ? SecurityEventCatalog.EventLogProvider
            : SecurityEventCatalog.SecurityAuditingProvider;
        return string.Equals(record.Provider, expectedProvider, StringComparison.OrdinalIgnoreCase)
            && isKnownDomainController(record.Event.Computer);
    }
}
