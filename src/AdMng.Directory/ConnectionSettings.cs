using System.DirectoryServices.Protocols;
using System.Net;
using AdMng.Core.Directory;
using DirectoryException = AdMng.Core.Directory.DirectoryException;

namespace AdMng.Directory;

/// <summary>
/// Validated connection parameters plus the credential used for every pooled connection of a session.
/// The credential lives in memory only and is dropped by <see cref="ClearCredential"/> when the session ends.
/// </summary>
internal sealed class ConnectionSettings
{
    private NetworkCredential? _credential;

    private ConnectionSettings(ConnectionOptions options, AuthType authType, NetworkCredential? credential, AccountName? account)
    {
        Options = options;
        AuthType = authType;
        _credential = credential;
        Account = account;
    }

    public ConnectionOptions Options { get; }

    /// <summary>S.DS.P authentication mechanism (Negotiate or Basic).</summary>
    public AuthType AuthType { get; }

    /// <summary>The typed account (null for <see cref="AuthenticationMode.CurrentUser"/>).</summary>
    public AccountName? Account { get; }

    /// <summary>Credential for binds; null means the Windows identity / Kerberos ticket of the process.</summary>
    public NetworkCredential? Credential => _credential;

    public int Port => Options.EffectivePort;

    /// <summary>True when the bind or the transport protects the traffic against eavesdropping.</summary>
    public bool UsesTransportEncryption => Options.Security is TransportSecurity.Tls or TransportSecurity.StartTls;

    /// <summary>
    /// Validates <paramref name="options"/> and builds the bind credential.
    /// </summary>
    /// <param name="options">Options from the connection dialog.</param>
    /// <param name="credential">Password (and optionally the user name) typed by the operator.</param>
    /// <param name="isWindows">Platform switch (injected for unit tests).</param>
    /// <exception cref="ArgumentException">Invalid or incomplete options.</exception>
    /// <exception cref="DirectoryException">The combination is refused by policy (clear-text simple bind).</exception>
    /// <exception cref="PlatformNotSupportedException">Explicit Negotiate credentials on a non-Windows OS.</exception>
    public static ConnectionSettings Create(ConnectionOptions options, NetworkCredential? credential, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateNumbers(options);

        if (options.Server is { } server && string.IsNullOrWhiteSpace(server))
        {
            throw new ArgumentException("The server name is empty.", nameof(options));
        }

        if (options.Server is { } hostAndPort && hostAndPort.Count(c => c == ':') == 1)
        {
            // "dc01:636" would bypass Port/Security (and an IPv6 literal has more than one colon).
            throw new ArgumentException("Specify the port in ConnectionOptions.Port, not in the server name.", nameof(options));
        }

        if (options.Domain is { } domainName && string.IsNullOrWhiteSpace(domainName))
        {
            throw new ArgumentException("The domain name is empty.", nameof(options));
        }

        switch (options.Authentication)
        {
            case AuthenticationMode.CurrentUser:
                // The process identity is used; a credential passed by mistake is ignored on purpose.
                return new ConnectionSettings(options, AuthType.Negotiate, null, null);

            case AuthenticationMode.Credentials:
                {
                    if (!isWindows)
                    {
                        throw new PlatformNotSupportedException(
                            "Negotiate with explicit credentials is not supported by the LDAP client on this platform. " +
                            "Use SimpleBind over TLS, or CurrentUser with a Kerberos ticket (kinit).");
                    }

                    var account = ParseAccount(options, credential);
                    if (account.Form == AccountNameForm.DistinguishedName)
                    {
                        throw new ArgumentException("A distinguished name can only be used with simple bind.", nameof(options));
                    }

                    var password = RequirePassword(credential);
                    var (user, domain) = account.ToNegotiateParts(options.Domain);
                    return new ConnectionSettings(options, AuthType.Negotiate, new NetworkCredential(user, password, domain), account);
                }

            case AuthenticationMode.SimpleBind:
                {
                    switch (options.Security)
                    {
                        case TransportSecurity.SignAndSeal:
                            throw new ArgumentException(
                                "Simple bind cannot sign or seal the connection; use Tls or StartTls.", nameof(options));
                        case TransportSecurity.None when !options.AllowInsecureSimpleBind:
                            throw new DirectoryException(
                                DirectoryErrorKind.StrongAuthRequired,
                                "Simple bind over an unprotected connection would send the password in clear text. " +
                                "Use Tls or StartTls (the insecure combination is only allowed for the test lab).");
                    }

                    var account = ParseAccount(options, credential);
                    var password = RequirePassword(credential);
                    var bindName = account.ToSimpleBindName(options.Domain);
                    return new ConnectionSettings(options, AuthType.Basic, new NetworkCredential(bindName, password), account);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Authentication, "Unknown authentication mode.");
        }
    }

    /// <summary>Drops the reference to the credential (session disposed).</summary>
    public void ClearCredential() => _credential = null;

    /// <summary>Never includes the credential.</summary>
    public override string ToString() =>
        $"{Options.Server ?? Options.Domain ?? "(default)"}:{Port} {Options.Authentication}/{Options.Security}";

    private static void ValidateNumbers(ConnectionOptions options)
    {
        if (options.Port is { } port && (port < 1 || port > 65535))
        {
            throw new ArgumentOutOfRangeException(nameof(options), port, "The port must be between 1 and 65535.");
        }

        if (options.MaxConnections < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxConnections, "At least one connection is required.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout.TotalSeconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.RequestTimeout, "The request timeout must be positive.");
        }

        if (options.ConnectTimeout <= TimeSpan.Zero || options.ConnectTimeout.TotalSeconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ConnectTimeout, "The connect timeout must be positive.");
        }
    }

    private static AccountName ParseAccount(ConnectionOptions options, NetworkCredential? credential)
    {
        var userName = !string.IsNullOrWhiteSpace(options.UserName) ? options.UserName : credential?.UserName;
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new ArgumentException("A user name is required for this authentication mode.", nameof(options));
        }

        var account = AccountName.Parse(userName);
        if (account.Form == AccountNameForm.Plain && !string.IsNullOrWhiteSpace(credential?.Domain))
        {
            // NetworkCredential("user", password, "CONTOSO") - honour the separately supplied domain.
            account = new AccountName(AccountNameForm.DownLevel, account.User, credential.Domain);
        }

        return account;
    }

    private static string RequirePassword(NetworkCredential? credential)
    {
        // An empty password turns an LDAP simple bind into an unauthenticated (anonymous) bind - refuse it.
        if (credential is null || string.IsNullOrEmpty(credential.Password))
        {
            throw new ArgumentException("A password is required for this authentication mode.", nameof(credential));
        }

        return credential.Password;
    }
}
