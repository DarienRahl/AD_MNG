using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Net.Sockets;
using AdMng.Core.Computers.Remote;
using AdMng.Core.EventLogs;

namespace AdMng.EventLogs;

/// <summary>What failed, as far as the exception type tells (the input of <see cref="EventLogErrors.Classify"/>).</summary>
internal enum EventLogFailureKind
{
    /// <summary><see cref="EventLogException"/> of no more specific type, or a <see cref="Win32Exception"/>.</summary>
    EventLog,

    /// <summary><see cref="EventLogNotFoundException"/> (Win32 2, 3, 15002, 15007, 15027, 15028).</summary>
    NotFound,

    /// <summary><see cref="EventLogInvalidDataException"/> (Win32 13, 15005).</summary>
    InvalidData,

    /// <summary><see cref="EventLogReadingException"/> (stale results, 15011 / 15012).</summary>
    Reading,

    /// <summary><see cref="EventLogProviderDisabledException"/> (15037).</summary>
    ProviderDisabled,

    /// <summary><see cref="UnauthorizedAccessException"/> (Win32 5).</summary>
    AccessDenied,

    /// <summary><see cref="IOException"/> (a file).</summary>
    Io,

    /// <summary><see cref="SocketException"/> (name resolution, connection).</summary>
    Network,

    /// <summary>Not possible here (<see cref="PlatformNotSupportedException"/>, a window without a Windows logon).</summary>
    NotSupported,
}

/// <summary>
/// Event log failures → <see cref="EventLogAccessError"/> (design note e-event-logs.md 3.2). <see cref="Classify"/> is a
/// pure function of the failure kind, the Windows error code and whether a file or a computer was read, so it is tested
/// on every platform; <see cref="Translate"/> takes an exception apart. No message text is kept – the exception of the
/// API may name the host, the channel or the file – only the kind and the number (<see cref="EventLogAccessException.NativeErrorCode"/>).
/// </summary>
internal static class EventLogErrors
{
    private const int AccessDeniedCode = 5;

    /// <summary>The account may not read the log, or its logon was refused by the target.</summary>
    private static readonly HashSet<int> AccessDenied =
    [
        5, // ERROR_ACCESS_DENIED
        1314, // ERROR_PRIVILEGE_NOT_HELD
        1326, // ERROR_LOGON_FAILURE
        1327, // ERROR_ACCOUNT_RESTRICTION
        1330, // ERROR_PASSWORD_EXPIRED
        1331, // ERROR_ACCOUNT_DISABLED
        1385, // ERROR_LOGON_TYPE_NOT_GRANTED
        1793, // ERROR_ACCOUNT_EXPIRED
        1907, // ERROR_PASSWORD_MUST_CHANGE
        1909, // ERROR_ACCOUNT_LOCKED_OUT
    ];

    /// <summary>
    /// No RPC connection to the event log service: the endpoint is blocked (firewall group "Remote Event Log Management")
    /// or not registered (service not running). RPC_S_SERVER_UNAVAILABLE (1722) is also all a computer that is off or cut
    /// off gives once its name resolved (the only check before the connection) – a probe of port 135 could not tell it
    /// from a firewall that drops, so the text of this kind names both causes.
    /// </summary>
    private static readonly HashSet<int> RpcUnavailable =
    [
        1225, // ERROR_CONNECTION_REFUSED
        1717, // RPC_S_UNKNOWN_IF: the interface is not registered (Event Log service not running)
        1722, // RPC_S_SERVER_UNAVAILABLE
        1723, // RPC_S_SERVER_TOO_BUSY
        1726, // RPC_S_CALL_FAILED
        1727, // RPC_S_CALL_FAILED_DNE
        1753, // EPT_S_NOT_REGISTERED: the endpoint mapper has no endpoint
        10061, // WSAECONNREFUSED
    ];

    /// <summary>The host does not answer: the name does not resolve, no route, timeout.</summary>
    private static readonly HashSet<int> Unreachable =
    [
        53, // ERROR_BAD_NETPATH
        67, // ERROR_BAD_NET_NAME
        121, // ERROR_SEM_TIMEOUT
        1214, // ERROR_INVALID_NETNAME
        1231, // ERROR_NETWORK_UNREACHABLE
        1232, // ERROR_HOST_UNREACHABLE
        1460, // ERROR_TIMEOUT
        1707, // RPC_S_INVALID_NET_ADDR
        10051, // WSAENETUNREACH
        10060, // WSAETIMEDOUT
        10065, // WSAEHOSTUNREACH
        11001, // WSAHOST_NOT_FOUND
        11002, // WSATRY_AGAIN
        11004, // WSANO_DATA
    ];

    private static readonly HashSet<int> ChannelNotFound =
    [
        2, // ERROR_FILE_NOT_FOUND
        3, // ERROR_PATH_NOT_FOUND
        15000, // ERROR_EVT_INVALID_CHANNEL_PATH
        15007, // ERROR_EVT_CHANNEL_NOT_FOUND
        15036, // ERROR_EVT_CANNOT_OPEN_CHANNEL_OF_QUERY
    ];

    /// <summary>The target rejected the query (syntax, unsupported operator, too complex).</summary>
    private static readonly HashSet<int> InvalidQuery =
    [
        15001, // ERROR_EVT_INVALID_QUERY
        15008, // ERROR_EVT_MALFORMED_XML_TEXT
        15014, // ERROR_EVT_FILTER_ALREADYSCOPED
        15015, // ERROR_EVT_FILTER_NOTELTSET
        15016, // ERROR_EVT_FILTER_INVARG
        15017, // ERROR_EVT_FILTER_INVTEST
        15018, // ERROR_EVT_FILTER_INVTYPE
        15019, // ERROR_EVT_FILTER_PARSEERR
        15020, // ERROR_EVT_FILTER_UNSUPPORTEDOP
        15021, // ERROR_EVT_FILTER_UNEXPECTEDTOKEN
        15026, // ERROR_EVT_FILTER_TOO_COMPLEX
        15038, // ERROR_EVT_FILTER_OUT_OF_RANGE
    ];

    /// <summary>A file that is missing, locked, damaged or not an event log.</summary>
    private static readonly HashSet<int> FileUnreadable =
    [
        2, // ERROR_FILE_NOT_FOUND
        3, // ERROR_PATH_NOT_FOUND
        5, // ERROR_ACCESS_DENIED
        13, // ERROR_INVALID_DATA
        15, // ERROR_INVALID_DRIVE
        21, // ERROR_NOT_READY
        32, // ERROR_SHARING_VIOLATION
        33, // ERROR_LOCK_VIOLATION
        53, // ERROR_BAD_NETPATH
        67, // ERROR_BAD_NET_NAME
        123, // ERROR_INVALID_NAME
        1392, // ERROR_FILE_CORRUPT
        1393, // ERROR_DISK_CORRUPT
        1500, // ERROR_EVENTLOG_FILE_CORRUPT
        1503, // ERROR_EVENTLOG_FILE_CHANGED
        15000, // ERROR_EVT_INVALID_CHANNEL_PATH
        15005, // ERROR_EVT_INVALID_EVENT_DATA
        15007, // ERROR_EVT_CHANNEL_NOT_FOUND
        15036, // ERROR_EVT_CANNOT_OPEN_CHANNEL_OF_QUERY
    ];

    /// <summary>
    /// The error of a failure: by the Windows code when it is known (a computer: access, RPC, reachability, channel,
    /// query; a file: unreadable or query), otherwise by the kind of the exception.
    /// </summary>
    /// <param name="kind">What the exception type says.</param>
    /// <param name="code">The Win32 error code (see <see cref="Win32Code"/>), null when unknown.</param>
    /// <param name="file">A file was read (an <c>.evtx</c> the operator chose), not a computer.</param>
    public static EventLogAccessError Classify(EventLogFailureKind kind, int? code, bool file)
    {
        if (kind == EventLogFailureKind.NotSupported)
        {
            return EventLogAccessError.NotSupported;
        }

        if (code is { } known && known != 0)
        {
            if (InvalidQuery.Contains(known))
            {
                return EventLogAccessError.InvalidQuery;
            }

            if (file)
            {
                if (FileUnreadable.Contains(known))
                {
                    return EventLogAccessError.FileUnreadable;
                }
            }
            else if (AccessDenied.Contains(known))
            {
                return EventLogAccessError.AccessDenied;
            }
            else if (RpcUnavailable.Contains(known))
            {
                return EventLogAccessError.RpcUnavailable;
            }
            else if (Unreachable.Contains(known))
            {
                return EventLogAccessError.Unreachable;
            }
            else if (ChannelNotFound.Contains(known))
            {
                return EventLogAccessError.ChannelNotFound;
            }
        }

        return kind switch
        {
            EventLogFailureKind.NotFound or EventLogFailureKind.InvalidData or EventLogFailureKind.AccessDenied or EventLogFailureKind.Io when file
                => EventLogAccessError.FileUnreadable,
            EventLogFailureKind.NotFound => EventLogAccessError.ChannelNotFound,
            EventLogFailureKind.AccessDenied => EventLogAccessError.AccessDenied,
            EventLogFailureKind.Network => file ? EventLogAccessError.FileUnreadable : EventLogAccessError.Unreachable,
            _ => EventLogAccessError.Failed,
        };
    }

    /// <summary>
    /// The Win32 code of an HRESULT-like number: a plain code (1 – 0xFFFF, as the event log exceptions carry it) or the
    /// low word of a <c>0x8007xxxx</c> HRESULT; null for anything else (a .NET HRESULT says nothing about the cause).
    /// </summary>
    public static int? Win32Code(int value)
    {
        if (value is > 0 and <= 0xFFFF)
        {
            return value;
        }

        var raw = unchecked((uint)value);
        return (raw & 0xFFFF0000) == 0x80070000 ? (int)(raw & 0xFFFF) : null;
    }

    /// <summary>
    /// The exceptions <see cref="Translate"/> understands: the event log API's, access, I/O, sockets, Win32, platform and
    /// the K1 logon refusal. Anything else (a programming error) is not hidden behind an access error.
    /// </summary>
    public static bool IsTranslatable(Exception exception) => exception is EventLogException or UnauthorizedAccessException or IOException
        or SocketException or Win32Exception or PlatformNotSupportedException or RemoteActionException or EventLogAccessException;

    /// <summary>
    /// The failure as an <see cref="EventLogAccessException"/> (an existing one is returned as it is). The cause is kept
    /// only when its message cannot carry a value – the event log API's exceptions (the Windows text of the code) and K1's
    /// fixed texts; an I/O, socket or Win32 message may name the file or the host, so only its code is kept.
    /// </summary>
    public static EventLogAccessException Translate(Exception exception, bool file)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is EventLogAccessException access)
        {
            return access;
        }

        var (kind, code) = Describe(exception);
        var cause = exception is EventLogException or RemoteActionException ? exception : null;
        return new EventLogAccessException(Classify(kind, code, file), cause) { NativeErrorCode = code };
    }

    /// <summary>The failure kind and the Win32 code of <paramref name="exception"/>.</summary>
    public static (EventLogFailureKind Kind, int? Code) Describe(Exception exception) => exception switch
    {
        EventLogNotFoundException => (EventLogFailureKind.NotFound, Win32Code(exception.HResult)),
        EventLogInvalidDataException => (EventLogFailureKind.InvalidData, Win32Code(exception.HResult)),
        EventLogReadingException => (EventLogFailureKind.Reading, Win32Code(exception.HResult)),
        EventLogProviderDisabledException => (EventLogFailureKind.ProviderDisabled, Win32Code(exception.HResult)),
        EventLogException => (EventLogFailureKind.EventLog, Win32Code(exception.HResult)),
        UnauthorizedAccessException => (EventLogFailureKind.AccessDenied, AccessDeniedCode),
        SocketException socket => (EventLogFailureKind.Network, socket.SocketErrorCode is SocketError.Success or SocketError.SocketError ? null : (int)socket.SocketErrorCode),
        Win32Exception win32 => (EventLogFailureKind.EventLog, win32.NativeErrorCode == 0 ? null : win32.NativeErrorCode),
        IOException => (EventLogFailureKind.Io, Win32Code(exception.HResult)),
        PlatformNotSupportedException => (EventLogFailureKind.NotSupported, null),
        RemoteActionException remote => remote.Kind switch
        {
            RemoteErrorKind.NotSupported => (EventLogFailureKind.NotSupported, remote.ErrorCode),
            RemoteErrorKind.Unreachable or RemoteErrorKind.Timeout => (EventLogFailureKind.Network, remote.ErrorCode),
            RemoteErrorKind.AccessDenied => (EventLogFailureKind.AccessDenied, remote.ErrorCode),
            _ => (EventLogFailureKind.EventLog, remote.ErrorCode),
        },
        _ => (EventLogFailureKind.EventLog, null),
    };
}
