using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Net.Sockets;
using AdMng.Core.Directory;
using DirectoryException = AdMng.Core.Directory.DirectoryException;

namespace AdMng.Directory;

/// <summary>
/// The request never left the client (the send itself failed), so repeating it cannot apply a change twice.
/// Internal marker used for the transparent retry on a fresh connection.
/// </summary>
internal sealed class RequestNotSentException : Exception
{
    public RequestNotSentException(Exception innerException)
        : base("The LDAP request could not be sent.", innerException)
    {
    }

    public RequestNotSentException()
        : base("The LDAP request could not be sent.")
    {
    }

    public RequestNotSentException(string message)
        : base(message)
    {
    }

    public RequestNotSentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Translates S.DS.P exceptions to <see cref="DirectoryException"/>.</summary>
/// <remarks>
/// Exception messages never contain DNs or server diagnostic text (which may quote DNs), so they are safe to log;
/// the details are available in <see cref="DirectoryException.ServerMessage"/> and
/// <see cref="DirectoryException.DistinguishedName"/>.
/// </remarks>
internal static class LdapErrors
{
    /// <summary>LDAP_SERVER_DOWN - the connection is gone.</summary>
    public const int ServerDown = 81;

    /// <summary>LDAP_TIMEOUT - client side time limit exceeded.</summary>
    public const int ClientTimeout = 85;

    /// <summary>LDAP_FILTER_ERROR - malformed search filter.</summary>
    public const int FilterError = 87;

    /// <summary>LDAP_USER_CANCELLED.</summary>
    public const int UserCancelled = 88;

    /// <summary>LDAP_PARAM_ERROR.</summary>
    public const int ParameterError = 89;

    /// <summary>LDAP_CONNECT_ERROR.</summary>
    public const int ConnectError = 91;

    /// <summary>LDAP_NOT_SUPPORTED.</summary>
    public const int NotSupported = 92;

    /// <summary>
    /// True when the exception means the TCP connection is unusable and should be dropped from the pool.
    /// </summary>
    public static bool IsConnectionBroken(Exception exception) => Unwrap(exception) switch
    {
        LdapException le => le.ErrorCode is ServerDown or ConnectError,
        _ => false,
    };

    /// <summary>
    /// True for a client side timeout: the server did not answer within the request timeout. The connection is
    /// suspect (silently dropped by a firewall or VPN change, or a hung DC) and should be replaced; the request is
    /// not repeated automatically because it may just be expensive.
    /// </summary>
    public static bool IsClientTimeout(Exception exception) => Unwrap(exception) switch
    {
        TimeoutException => true,
        LdapException le => le.ErrorCode == ClientTimeout,
        _ => false,
    };

    /// <summary>True when the request provably did not reach the server.</summary>
    public static bool WasNotSent(Exception exception) => exception is RequestNotSentException;

    /// <summary>
    /// Converts an exception thrown by S.DS.P into a <see cref="DirectoryException"/>. Returns null for exceptions
    /// that are not directory errors (argument errors, disposal, cancellation) - the caller rethrows those.
    /// </summary>
    public static DirectoryException? Translate(Exception exception, OperationKind operation, string? distinguishedName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var inner = Unwrap(exception);
        switch (inner)
        {
            case DirectoryException directoryException:
                return directoryException;

            case DirectoryOperationException doe:
                {
                    var resultCode = doe.Response is { } response ? (int)response.ResultCode : 0;
                    var serverMessage = doe.Response?.ErrorMessage;
                    if (resultCode == 0 && string.IsNullOrEmpty(serverMessage))
                    {
                        // Bind failures without a response object carry no result code - treat as unknown.
                        serverMessage = doe.Message;
                    }

                    var win32 = DirectoryException.ParseWin32Error(serverMessage);
                    var kind = DirectoryException.Classify(resultCode, win32);
                    return new DirectoryException(kind, Describe(operation, resultCode, win32, kind), doe)
                    {
                        LdapResultCode = resultCode,
                        Win32Error = win32,
                        ServerMessage = string.IsNullOrEmpty(serverMessage) ? null : serverMessage,
                        DistinguishedName = distinguishedName,
                    };
                }

            case LdapException le:
                {
                    var serverMessage = string.IsNullOrWhiteSpace(le.ServerErrorMessage) ? null : le.ServerErrorMessage;
                    var win32 = DirectoryException.ParseWin32Error(serverMessage);
                    var kind = ClassifyClientError(le.ErrorCode, win32);
                    return new DirectoryException(kind, Describe(operation, le.ErrorCode, win32, kind), le)
                    {
                        LdapResultCode = le.ErrorCode,
                        Win32Error = win32,
                        ServerMessage = serverMessage,
                        DistinguishedName = distinguishedName,
                    };
                }

            case TimeoutException te:
                return new DirectoryException(DirectoryErrorKind.Timeout, $"{operation} timed out.", te)
                {
                    DistinguishedName = distinguishedName,
                };

            case SocketException se:
                return new DirectoryException(
                    DirectoryErrorKind.ServerUnavailable,
                    $"{operation} failed: the server is not reachable ({se.SocketErrorCode}).",
                    se)
                {
                    DistinguishedName = distinguishedName,
                };

            default:
                return null;
        }
    }

    /// <summary>Maps result codes of LdapException (server codes 0-80 and client library codes 81+).</summary>
    public static DirectoryErrorKind ClassifyClientError(int errorCode, int? win32) => errorCode switch
    {
        ServerDown or ConnectError or 82 /* LDAP_LOCAL_ERROR */ or 83 /* LDAP_ENCODING_ERROR */ or 84 /* LDAP_DECODING_ERROR */
            => DirectoryErrorKind.ServerUnavailable,
        ClientTimeout => DirectoryErrorKind.Timeout,
        FilterError => DirectoryErrorKind.InvalidSyntax,
        UserCancelled => DirectoryErrorKind.Cancelled,
        NotSupported or 93 /* LDAP_CONTROL_NOT_FOUND */ => DirectoryErrorKind.UnsupportedControl,
        _ => DirectoryException.Classify(errorCode, win32),
    };

    /// <summary>English, value-free description of a failure.</summary>
    public static string Describe(OperationKind operation, int resultCode, int? win32, DirectoryErrorKind kind)
    {
        var name = resultCode < ServerDown && Enum.IsDefined((ResultCode)resultCode)
            ? ((ResultCode)resultCode).ToString()
            : kind.ToString();
        var detail = win32 is { } w ? string.Create(CultureInfo.InvariantCulture, $", Win32 0x{w:X}") : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{operation} failed: {name} (LDAP {resultCode}{detail}).");
    }

    private static Exception Unwrap(Exception exception) =>
        exception is RequestNotSentException { InnerException: { } inner } ? inner : exception;
}
