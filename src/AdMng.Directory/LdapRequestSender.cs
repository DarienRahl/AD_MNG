using System.DirectoryServices.Protocols;

namespace AdMng.Directory;

/// <summary>
/// How <see cref="LdapConnectionPool"/> sends a request on one of its connections and wakes a request that waits for the
/// server. Production uses <see cref="LdapRequestSender.Instance"/>; the pool's unit tests replace it with requests they
/// end themselves.
/// </summary>
internal interface ILdapRequestSender
{
    /// <summary>Sends one request (see <see cref="LdapAsync.Send{TResponse}"/>).</summary>
    PendingResponse<TResponse> Send<TResponse>(
        LdapConnection connection,
        DirectoryRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TResponse : DirectoryResponse;

    /// <summary>
    /// Makes a request that waits for the server on <paramref name="connection"/> return soon, without disposing the
    /// connection (best effort; nothing happens where the platform has no such means).
    /// </summary>
    void Interrupt(LdapConnection connection);
}

/// <summary>The real sender: <see cref="LdapAsync"/>, and on Linux/macOS a shutdown of the connection's socket.</summary>
internal sealed class LdapRequestSender : ILdapRequestSender
{
    private LdapRequestSender()
    {
    }

    public static LdapRequestSender Instance { get; } = new();

    public PendingResponse<TResponse> Send<TResponse>(
        LdapConnection connection,
        DirectoryRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TResponse : DirectoryResponse =>
        connection.Send<TResponse>(request, timeout, cancellationToken);

    /// <remarks>
    /// Linux/macOS: a blocking request cannot be cancelled, and its <c>ldap_result</c> ignores the timeout while the server
    /// is silent; shutting the socket down (<see cref="OpenLdapTls.TryShutdownSocket"/>) makes it return with "server down".
    /// A request that already builds its response is not affected. Windows: WinLDAP requests are cancelled through their
    /// token (an LDAP abandon) - nothing to do here.
    /// </remarks>
    public void Interrupt(LdapConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!OperatingSystem.IsWindows())
        {
            OpenLdapTls.TryShutdownSocket(connection);
        }
    }
}
