using AdMng.Core.Computers.Remote;
using AdMng.Core.EventLogs;

namespace AdMng.EventLogs;

/// <summary>
/// The hosts an event log session may be opened to (design note e-event-logs.md 1): a fully qualified DNS name – the
/// K1 rules of <see cref="HostNames.IsValid"/> (no IP literal in any form, only letters, digits, hyphens and
/// underscores in the labels) plus at least one dot. Kerberos needs the host's name (its SPN); an IP literal or a short
/// name would leave the target to whatever the resolver or NTLM makes of it, so they are refused before any connection,
/// as the Watchdog's <c>WindowsSecurityEventSource</c> refuses them.
/// </summary>
internal static class EventLogHosts
{
    /// <summary>Win32 ERROR_INVALID_NAME (123): the host is not a DNS host name (the Watchdog's code for the same refusal).</summary>
    public const int InvalidHostNameError = 123;

    /// <summary>True for a fully qualified DNS host name (<c>dc01.contoso.test</c>, <c>krk-pc101.contoso.test.</c>).</summary>
    public static bool IsValid(string? host) =>
        HostNames.IsValid(host) && HostNames.Normalize(host!).Contains('.', StringComparison.Ordinal);

    /// <summary>The normalized name (lower case, no trailing dot).</summary>
    /// <exception cref="EventLogAccessException">Not a fully qualified DNS host name (NotSupported, 123).</exception>
    public static string Require(string? host) => IsValid(host)
        ? HostNames.Normalize(host!)
        : throw new EventLogAccessException(EventLogAccessError.NotSupported) { NativeErrorCode = InvalidHostNameError };
}
