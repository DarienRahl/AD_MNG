using AdMng.Core.Computers.Remote;

namespace AdMng.Remote;

/// <summary>
/// CIM / WS-Management failures → <see cref="RemoteErrorKind"/>: the MI result code of the operation and the Windows,
/// WinRM or SSPI error code of its error instance (<c>error_Code</c>), normalized from an HRESULT. Pure, tested on every
/// platform. No message text of the remote side is kept (it may name the computer or the account).
/// </summary>
internal static class RemoteErrors
{
    // MI_Result (NativeErrorCode) values used here.
    private const int MiAccessDenied = 2;
    private const int MiInvalidNamespace = 3;
    private const int MiInvalidClass = 5;
    private const int MiNotSupported = 7;
    private const int MiMethodNotAvailable = 16;
    private const int MiMethodNotFound = 17;
    private const int MiInvalidOperationTimeout = 22;
    private const int MiServerIsShuttingDown = 28;

    private static readonly HashSet<uint> Unreachable =
    [
        53, // ERROR_BAD_NETPATH
        64, // ERROR_NETNAME_DELETED
        1219, // the remote host refused
        1231, // ERROR_NETWORK_UNREACHABLE
        1232, // ERROR_HOST_UNREACHABLE
        1722, // RPC_S_SERVER_UNAVAILABLE
        11001, // WSAHOST_NOT_FOUND
        12007, // ERROR_WINHTTP_NAME_NOT_RESOLVED
        12029, // ERROR_WINHTTP_CANNOT_CONNECT
        0x80338126, // ERROR_WSMAN_... "verify that the computer is accessible over the network and the firewall"
    ];

    private static readonly HashSet<uint> WinRmNotAvailable =
    [
        1225, // ERROR_CONNECTION_REFUSED
        12030, // ERROR_WINHTTP_CONNECTION_ERROR
        0x80338012, // ERROR_WSMAN_CANNOTCONNECT: the WinRM service is not listening
        0x8033803B, // ERROR_WSMAN_DESTINATION_UNREACHABLE (no listener for the address)
    ];

    private static readonly HashSet<uint> Authentication =
    [
        1311, // ERROR_NO_LOGON_SERVERS
        1326, // ERROR_LOGON_FAILURE
        1396, // ERROR_WRONG_TARGET_NAME (SPN)
        1398, // ERROR_TIME_SKEW
        0x80090303, // SEC_E_TARGET_UNKNOWN
        0x8009030C, // SEC_E_LOGON_DENIED
        0x8009030E, // SEC_E_NO_CREDENTIALS
        0x80090311, // SEC_E_NO_AUTHENTICATING_AUTHORITY
        0x80090322, // SEC_E_WRONG_PRINCIPAL
        0x80090324, // SEC_E_TIME_SKEW
        0x80090350, // SEC_E_DOWNGRADE_DETECTED
        0x803380E4, // ERROR_WSMAN_... default authentication / TrustedHosts (no Kerberos for the target)
        0x8033809D, // ERROR_WSMAN_CLIENT_KERBEROS_DISABLED
    ];

    private static readonly HashSet<uint> AccessDenied =
    [
        5, // ERROR_ACCESS_DENIED
        1314, // ERROR_PRIVILEGE_NOT_HELD
    ];

    private static readonly HashSet<uint> Timeout =
    [
        121, // ERROR_SEM_TIMEOUT
        1460, // ERROR_TIMEOUT
        12002, // ERROR_WINHTTP_TIMEOUT
        0x80338029, // ERROR_WSMAN_OPERATION_TIMEDOUT
    ];

    /// <summary>The kind of a failure from the MI result code and the error instance's code (either may be missing).</summary>
    public static RemoteErrorKind Classify(int? nativeErrorCode, uint? errorCode)
    {
        if (errorCode is { } raw && raw != 0)
        {
            var code = Normalize(raw);
            if (Authentication.Contains(code) || Authentication.Contains(raw))
            {
                return RemoteErrorKind.AuthenticationFailed;
            }

            if (AccessDenied.Contains(code))
            {
                return RemoteErrorKind.AccessDenied;
            }

            if (Timeout.Contains(code) || Timeout.Contains(raw))
            {
                return RemoteErrorKind.Timeout;
            }

            if (WinRmNotAvailable.Contains(code) || WinRmNotAvailable.Contains(raw))
            {
                return RemoteErrorKind.WinRmNotAvailable;
            }

            if (Unreachable.Contains(code) || Unreachable.Contains(raw))
            {
                return RemoteErrorKind.Unreachable;
            }
        }

        return nativeErrorCode switch
        {
            MiAccessDenied => RemoteErrorKind.AccessDenied,
            MiInvalidOperationTimeout => RemoteErrorKind.Timeout,
            MiServerIsShuttingDown => RemoteErrorKind.Unreachable,
            MiInvalidNamespace or MiInvalidClass or MiNotSupported or MiMethodNotAvailable or MiMethodNotFound => RemoteErrorKind.NotSupported,
            _ => RemoteErrorKind.Failed,
        };
    }

    /// <summary>The ReturnValue of a Win32 method (Win32_Process.Create, Win32ShutdownTracker, StdRegProv) ≠ 0.</summary>
    public static RemoteActionException FromReturnValue(uint returnValue) => new(
        returnValue switch
        {
            2 or 5 or 1314 => RemoteErrorKind.AccessDenied,
            _ => RemoteErrorKind.Failed,
        })
    {
        ErrorCode = unchecked((int)returnValue),
    };

    /// <summary>A Win32 error wrapped in an HRESULT (0x8007xxxx) → the Win32 code; anything else unchanged.</summary>
    public static uint Normalize(uint code) => (code & 0xFFFF0000) == 0x80070000 ? code & 0xFFFF : code;
}
