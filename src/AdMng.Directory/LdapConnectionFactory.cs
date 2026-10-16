using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Sockets;
using AdMng.Core.Directory;
using Microsoft.Extensions.Logging;
using DirectoryException = AdMng.Core.Directory.DirectoryException;

namespace AdMng.Directory;

/// <summary>Opens and binds LDAP connections to one domain controller with the session's settings.</summary>
internal sealed class LdapConnectionFactory
{
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

        // Windows reports the negotiated sealing state. With OpenLDAP the GSSAPI SASL layer negotiates
        // confidentiality by default (maxssf is unlimited), which the option API cannot query.
        return !OperatingSystem.IsWindows() || connection.SessionOptions.Sealing;
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
