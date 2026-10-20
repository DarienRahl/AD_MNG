using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AdMng.Core.Directory;
using Microsoft.Extensions.Logging;
using DirectoryException = AdMng.Core.Directory.DirectoryException;

namespace AdMng.Directory;

/// <summary>Opens and binds LDAP connections to one domain controller with the session's settings.</summary>
/// <remarks>
/// On Linux/macOS a Kerberos (SASL GSSAPI) bind with <see cref="TransportSecurity.SignAndSeal"/> must have installed a
/// confidential SASL security layer: libldap enforces <c>SASL_SECPROPS minssf=56</c> from <c>LDAPCONF</c>, and after
/// every bind the factory reads the negotiated security strength factor itself and refuses a connection below
/// <see cref="OpenLdapSasl.MinimumSealedSsf"/> (<see cref="DirectoryErrorKind.StrongAuthRequired"/>) – a misconfigured
/// or missing <c>LDAPCONF</c> must not silently yield an unsealed session (AdMng Watchdog Linux design 5.1).
/// </remarks>
internal sealed class LdapConnectionFactory
{
    /// <summary>
    /// Lab tests only (fault injection, Linux): SASL SSF limits set on every connection opened in the current async flow
    /// before its bind; they override <c>SASL_SECPROPS</c> of <c>LDAPCONF</c> for that handle. Null in production.
    /// </summary>
    internal static readonly AsyncLocal<(int Minimum, int Maximum)?> SaslSecurityLimitsForTests = new();

    private readonly ConnectionSettings _settings;
    private readonly ILogger _logger;
    private readonly bool _isHostName;
    private int _certificateWarningLogged;

    /// <param name="settings">Validated settings incl. the credential.</param>
    /// <param name="host">DC host name or address all connections go to.</param>
    /// <param name="domain">DNS domain name (to tell a domain name from a host name), optional.</param>
    /// <param name="logger">Logger.</param>
    public LdapConnectionFactory(ConnectionSettings settings, string host, string? domain, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
        Host = host.Trim();
        _isHostName = !IPAddress.TryParse(Host, out _)
            && !string.Equals(Host.TrimEnd('.'), domain?.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Host all connections of this factory go to.</summary>
    public string Host { get; }

    public int Port => _settings.Port;

    public ConnectionSettings Settings => _settings;

    /// <summary>
    /// Creates, configures and binds a connection. The bind runs on the thread pool (S.DS.P binds are
    /// synchronous) and is abandoned - and the connection disposed when it eventually returns - on timeout or
    /// cancellation.
    /// </summary>
    /// <param name="probeReachability">Check the TCP port within <see cref="ConnectionOptions.ConnectTimeout"/> first.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="DirectoryException">The DC is unreachable or the bind failed.</exception>
    public async Task<LdapConnection> OpenAsync(bool probeReachability, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (probeReachability)
        {
            await ProbeAsync(cancellationToken).ConfigureAwait(false);
        }

        var credential = _settings.Credential;
        if (credential is null && _settings.Options.Authentication != AuthenticationMode.CurrentUser)
        {
            throw new ObjectDisposedException(nameof(LdapConnectionFactory), "The session credential was released.");
        }

        var connection = Create(credential);
        Task? bind = null;
        try
        {
            Configure(connection);
            bind = Task.Run(() => Bind(connection), CancellationToken.None);
            await bind.WaitAsync(_settings.Options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            EnsureSealed(connection);
            return connection;
        }
        catch (Exception ex)
        {
            if (bind is { IsCompleted: false })
            {
                // The native bind cannot be interrupted; release the connection once it returns.
                _ = bind.ContinueWith(
                    static (_, state) => ((LdapConnection)state!).Dispose(),
                    connection,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                connection.Dispose();
            }

            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw LdapErrors.Translate(ex, OperationKind.Bind, null)
                ?? new DirectoryException(DirectoryErrorKind.Unknown, "Bind failed.", ex);
        }
    }

    /// <summary>
    /// True when the bound connection encrypts traffic (TLS, or Negotiate with sealing). Password resets are
    /// refused on unencrypted channels.
    /// </summary>
    public bool IsEncrypted(LdapConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_settings.UsesTransportEncryption)
        {
            return true;
        }

        if (_settings.Options.Security != TransportSecurity.SignAndSeal || _settings.AuthType != AuthType.Negotiate)
        {
            return false;
        }

        // Windows reports the negotiated sealing state; libldap reports the SASL security strength factor.
        return OperatingSystem.IsWindows()
            ? connection.SessionOptions.Sealing
            : SecurityStrengthFactor(connection) >= OpenLdapSasl.MinimumSealedSsf;
    }

    /// <summary>
    /// The SASL security strength factor of a bound Kerberos (Negotiate) connection on Linux/macOS (0 = no security
    /// layer); null on Windows (SSPI reports sealing instead), for simple binds and when libldap cannot be reached.
    /// </summary>
    public int? SecurityStrengthFactor(LdapConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return OperatingSystem.IsWindows() || _settings.AuthType != AuthType.Negotiate
            ? null
            : OpenLdapSasl.TryGetSecurityStrengthFactor(connection);
    }

    private LdapConnection Create(NetworkCredential? credential)
    {
        var identifier = new LdapDirectoryIdentifier(Host, Port, _isHostName, connectionless: false);
        LdapConnection CreateCore() => new(identifier, credential, _settings.AuthType);

        var relaxCertificate = _settings.Options.AllowUntrustedCertificate && _settings.UsesTransportEncryption;
        if (relaxCertificate && Interlocked.Exchange(ref _certificateWarningLogged, 1) == 0)
        {
            Log.CertificateValidationDisabled(_logger, Host);
        }

        if (relaxCertificate && !OperatingSystem.IsWindows())
        {
            return OpenLdapTls.CreateWithoutCertificateValidation(CreateCore);
        }

        return CreateCore();
    }

    private void Configure(LdapConnection connection)
    {
        var options = _settings.Options;
        var session = connection.SessionOptions;
        session.ProtocolVersion = 3;
        session.ReferralChasing = ReferralChasingOptions.None;
        connection.AutoBind = false;
        connection.Timeout = options.RequestTimeout;
        if (options.Security == TransportSecurity.Tls)
        {
            session.SecureSocketLayer = true;
        }

        if (!OperatingSystem.IsWindows() && _settings.AuthType == AuthType.Negotiate
            && SaslSecurityLimitsForTests.Value is { } limits
            && !OpenLdapSasl.TrySetSecurityStrengthLimits(connection, limits.Minimum, limits.Maximum))
        {
            throw new InvalidOperationException("The SASL security limits of the test could not be applied.");
        }

        if (OperatingSystem.IsWindows())
        {
            if (options.Security == TransportSecurity.SignAndSeal && _settings.AuthType == AuthType.Negotiate)
            {
                session.Signing = true;
                session.Sealing = true;
            }

            if (options.AllowUntrustedCertificate && _settings.UsesTransportEncryption)
            {
                session.VerifyServerCertificate = static (_, _) => true;
            }
        }
    }

    /// <summary>
    /// Linux/macOS, Kerberos with sign and seal: refuses a bound connection whose SASL layer is weaker than
    /// <see cref="OpenLdapSasl.MinimumSealedSsf"/> (or unknown). Windows signs and seals through SSPI (Configure).
    /// </summary>
    /// <exception cref="DirectoryException">The negotiated SSF is below the minimum.</exception>
    private void EnsureSealed(LdapConnection connection)
    {
        if (OperatingSystem.IsWindows()
            || _settings.AuthType != AuthType.Negotiate
            || _settings.Options.Security != TransportSecurity.SignAndSeal)
        {
            return;
        }

        var ssf = SecurityStrengthFactor(connection);
        if (ssf is >= OpenLdapSasl.MinimumSealedSsf)
        {
            return;
        }

        Log.SessionNotSealed(_logger, Host, ssf ?? -1, OpenLdapSasl.MinimumSealedSsf);
        var negotiated = ssf is { } value ? value.ToString(CultureInfo.InvariantCulture) : "unknown";
        throw new DirectoryException(
            DirectoryErrorKind.StrongAuthRequired,
            $"The LDAP session negotiated a SASL security strength factor of {negotiated}; signing and sealing needs at least "
            + $"{OpenLdapSasl.MinimumSealedSsf.ToString(CultureInfo.InvariantCulture)} (SASL_SECPROPS minssf=56 in LDAPCONF).");
    }

    private void Bind(LdapConnection connection)
    {
        if (_settings.Options.Security == TransportSecurity.StartTls)
        {
            connection.SessionOptions.StartTransportLayerSecurity(null);
        }

        connection.Bind();
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.Options.ConnectTimeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(Host, Port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DirectoryException(
                DirectoryErrorKind.ServerUnavailable,
                $"The server did not accept a connection on port {Port} within {_settings.Options.ConnectTimeout.TotalSeconds:0} s.");
        }
        catch (SocketException ex)
        {
            throw LdapErrors.Translate(ex, OperationKind.Bind, null)!;
        }
    }
}
