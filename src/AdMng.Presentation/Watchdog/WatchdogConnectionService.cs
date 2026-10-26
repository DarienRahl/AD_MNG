using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Watchdog.Client;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// Connects <see cref="IWatchdogClient"/> to the service address of the settings (<see cref="WatchdogClientSettings"/>):
/// builds <see cref="WatchdogClientOptions"/> (process identity – <c>runas /netonly</c> works; optional certificate pin),
/// accepts only <c>https://</c> addresses, and maps <see cref="WatchdogClientException.Kind"/> to a connection state and a
/// localized message. Stores the certificate pin the operator confirmed (stage B design 18 item 1: a setting, never
/// directory data) and removes it with the address of another service. Singleton shared by the Watchdog tabs and the object
/// history launchers of all connection windows: it also knows the domain the service watches (<see cref="ServiceDomain"/>)
/// and asks for a certificate confirmation at most once at a time for the whole application.
/// </summary>
public sealed class WatchdogConnectionService
{
    private readonly ISettingsStore _settings;
    private readonly ILocalizer _localizer;
    private readonly Lock _domainGate = new();
    private string? _configuredPin;
    private string? _serviceDomain;
    private Task? _domainRead;
    private Task<bool>? _certificateConfirmation;

    public WatchdogConnectionService(IWatchdogClient client, ISettingsStore settings, ILocalizer localizer)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <summary>Raised after the service address was set or removed (on the calling thread).</summary>
    public event EventHandler? AddressChanged;

    /// <summary>
    /// <see cref="ServiceDomain"/> became known, changed or was forgotten (on the thread of the status read or of the address
    /// change). Windows compare it with their own domain.
    /// </summary>
    public event EventHandler? ServiceDomainChanged;

    /// <summary>
    /// The AD domain the configured service watches (from its last status read in any window), or null while unknown (no
    /// address, an older service, nothing read yet). Application state in memory only: the domain name of one service.
    /// </summary>
    public string? ServiceDomain => Volatile.Read(ref _serviceDomain);

    public IWatchdogClient Client { get; }

    /// <summary>The configured address (settings), or null.</summary>
    public string? ServiceUrl => _settings.Current.Watchdog.ServiceUrl;

    /// <summary>The pinned SHA-256 of the service certificate (settings), or null.</summary>
    public string? PinnedCertificateSha256 => _settings.Current.Watchdog.PinnedCertificateSha256;

    /// <summary>A service address is configured (the object history command is offered).</summary>
    public bool HasServiceUrl => TryParseAddress(ServiceUrl, out _);

    /// <summary>"admng-wd.contoso.com:8443" – the host the tab talks to (always shown in the connection banner).</summary>
    public string HostText => TryParseAddress(ServiceUrl, out var address) ? HostOf(address) : string.Empty;

    /// <summary>
    /// Parses an address typed by the operator: an absolute <c>https://</c> URL with a host and nothing but an optional port
    /// and path (no user info, query or fragment). Normalized with a trailing slash.
    /// </summary>
    public static bool TryParseAddress(string? text, out Uri address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(text)
            || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return false;
        }

        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        address = builder.Uri;
        return true;
    }

    /// <summary>"host:port" of an address (port omitted for 443).</summary>
    public static string HostOf(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsDefaultPort ? address.Host : $"{address.Host}:{address.Port}";
    }

    /// <summary>The connection state for a failed call.</summary>
    public static WatchdogConnectionState StateFor(WatchdogClientErrorKind kind) => kind switch
    {
        WatchdogClientErrorKind.NotConfigured => WatchdogConnectionState.NotConfigured,
        WatchdogClientErrorKind.Unreachable or WatchdogClientErrorKind.Timeout => WatchdogConnectionState.Unreachable,
        WatchdogClientErrorKind.CertificateInvalid or WatchdogClientErrorKind.CertificateUntrusted or WatchdogClientErrorKind.CertificateChanged
            => WatchdogConnectionState.CertificateInvalid,
        WatchdogClientErrorKind.Unauthorized => WatchdogConnectionState.Unauthorized,
        WatchdogClientErrorKind.Forbidden => WatchdogConnectionState.Forbidden,
        WatchdogClientErrorKind.ApiVersionMismatch => WatchdogConnectionState.VersionMismatch,
        _ => WatchdogConnectionState.Error,
    };

    /// <summary>The connection state for any failure of a service call.</summary>
    public static WatchdogConnectionState StateFor(Exception exception) =>
        exception is WatchdogClientException client ? StateFor(client.Kind) : WatchdogConnectionState.Error;

    /// <summary>True when the failure concerns the connection itself (not one request).</summary>
    public static bool IsConnectionFailure(Exception exception) =>
        StateFor(exception) is not WatchdogConnectionState.Error and not WatchdogConnectionState.Connected;

    /// <summary>A localized error text for an address the operator typed, or null when it is acceptable.</summary>
    public string? ValidateAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return _localizer[L.Watchdog.AddressRequired];
        }

        return TryParseAddress(text, out _) ? null : _localizer[L.Watchdog.AddressHttpsOnly];
    }

    /// <summary>
    /// Configures the client with the address and the certificate pin of the settings when it is not configured with them
    /// yet. Returns false when no valid address is stored (the client is left alone). Throws when the client rejects the
    /// options.
    /// </summary>
    public bool EnsureConfigured()
    {
        if (!TryParseAddress(ServiceUrl, out var address))
        {
            return false;
        }

        var pin = PinnedCertificateSha256;
        if (Client.IsConfigured && Client.BaseAddress == address && string.Equals(_configuredPin, pin, StringComparison.Ordinal))
        {
            return true;
        }

        Client.Configure(new WatchdogClientOptions
        {
            BaseAddress = address,
            PinnedCertificateSha256 = pin,
        });
        _configuredPin = pin;
        return true;
    }

    /// <summary>
    /// Pins the certificate the operator confirmed (its SHA-256, 64 hex digits). The client uses it from the next
    /// connection attempt (<see cref="EnsureConfigured"/>) – only for a certificate whose only TLS error is its chain.
    /// </summary>
    /// <exception cref="ArgumentException">The fingerprint is not a SHA-256 hash.</exception>
    public Task PinCertificateAsync(string sha256)
    {
        var pin = WatchdogServerCertificate.NormalizeSha256(sha256) ?? throw new ArgumentException("A SHA-256 fingerprint (64 hex digits) is required.", nameof(sha256));
        return _settings.UpdateAsync(s => s.Watchdog.PinnedCertificateSha256 = pin);
    }

    /// <summary>A status read of any window: remembers the domain the service watches.</summary>
    public void ReportStatus(AdMng.Watchdog.Contracts.Api.StatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        SetServiceDomain(string.IsNullOrWhiteSpace(status.Domain) ? null : status.Domain.Trim());
    }

    /// <summary>
    /// True when the service is known to watch another domain than <paramref name="domainDnsName"/> (case-insensitive).
    /// An unknown service domain or an unknown window domain is no mismatch: the window behaves as before.
    /// </summary>
    public bool IsOtherDomain(string? domainDnsName) =>
        ServiceDomain is { } watched && !string.IsNullOrWhiteSpace(domainDnsName)
        && !string.Equals(watched.TrimEnd('.'), domainDnsName.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the service status once to learn <see cref="ServiceDomain"/> when it is unknown and an address is configured
    /// (single flight: concurrent callers share one read). Never throws; a failed read leaves the domain unknown.
    /// </summary>
    public Task EnsureServiceDomainAsync()
    {
        if (ServiceDomain is not null || !HasServiceUrl)
        {
            return Task.CompletedTask;
        }

        lock (_domainGate)
        {
            if (_domainRead is { IsCompleted: false } running)
            {
                return running;
            }

            _domainRead = ReadServiceDomainAsync();
            return _domainRead;
        }
    }

    /// <summary>
    /// One certificate confirmation for the whole application: when another Watchdog tab is already asking, the caller
    /// awaits that answer instead of showing a second, nested dialog. <paramref name="ask"/> shows the dialog and pins the
    /// certificate when the operator trusts it; the result is true when it was trusted. UI thread only.
    /// </summary>
    public Task<bool> ConfirmCertificateOnceAsync(Func<Task<bool>> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        if (_certificateConfirmation is { IsCompleted: false } pending)
        {
            return pending;
        }

        _certificateConfirmation = ask();
        return _certificateConfirmation;
    }

    /// <summary>Removes the pin (the next connection to a certificate of no trusted authority asks again).</summary>
    public Task RemovePinAsync() => _settings.UpdateAsync(s => s.Watchdog.PinnedCertificateSha256 = null);

    /// <summary>
    /// Validates and stores a new address (https only, normalized without a trailing slash). Returns a localized error
    /// or null. The client is configured with it by the next connection attempt (<see cref="EnsureConfigured"/>), which
    /// also reports a client that refuses it.
    /// </summary>
    public async Task<string?> SetAddressAsync(string text)
    {
        if (ValidateAddress(text) is { } error)
        {
            return error;
        }

        TryParseAddress(text, out var address);
        var normalized = address.GetLeftPart(UriPartial.Path).TrimEnd('/');

        // The pin belongs to the service it was confirmed for: another host or port starts with a new confirmation.
        var otherService = !TryParseAddress(ServiceUrl, out var previous) || !string.Equals(HostOf(previous), HostOf(address), StringComparison.OrdinalIgnoreCase);
        await _settings.UpdateAsync(s =>
        {
            s.Watchdog.ServiceUrl = normalized;
            if (otherService)
            {
                s.Watchdog.PinnedCertificateSha256 = null;
            }
        }).ConfigureAwait(true);
        if (otherService)
        {
            SetServiceDomain(null);
        }

        AddressChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Removes the stored address (the tab shows the address editor again).</summary>
    public async Task ClearAddressAsync()
    {
        await _settings.UpdateAsync(s => s.Watchdog.ServiceUrl = null).ConfigureAwait(true);
        SetServiceDomain(null);
        AddressChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReadServiceDomainAsync()
    {
        try
        {
            if (!EnsureConfigured())
            {
                return;
            }

            var status = await Client.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            ReportStatus(status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unknown: the windows behave as before (the Watchdog tab shows its own connection errors).
        }
    }

    private void SetServiceDomain(string? domain)
    {
        var previous = Interlocked.Exchange(ref _serviceDomain, domain);
        if (!string.Equals(previous, domain, StringComparison.OrdinalIgnoreCase))
        {
            ServiceDomainChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Localized description of a failed service call (connection banner, inline errors).</summary>
    public string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is not WatchdogClientException client)
        {
            return exception is OperationCanceledException ? _localizer[L.Errors.Cancelled] : _localizer[L.Watchdog.ErrorUnexpected];
        }

        return client.Kind switch
        {
            WatchdogClientErrorKind.NotConfigured => _localizer[L.Watchdog.ConnectionNotConfigured],
            WatchdogClientErrorKind.Unreachable => _localizer.Format(L.Watchdog.ConnectionUnreachable, HostText),
            WatchdogClientErrorKind.Timeout => _localizer.Format(L.Watchdog.ConnectionTimeout, HostText),
            WatchdogClientErrorKind.CertificateInvalid => _localizer.Format(L.Watchdog.ConnectionCertificateInvalid, HostText),
            WatchdogClientErrorKind.CertificateUntrusted => _localizer.Format(L.WatchdogLinux.ConnectionCertificateUntrusted, HostText),
            WatchdogClientErrorKind.CertificateChanged => _localizer.Format(L.WatchdogLinux.ConnectionCertificateChanged, HostText),
            WatchdogClientErrorKind.Unauthorized => _localizer[L.Watchdog.ConnectionUnauthorized],
            WatchdogClientErrorKind.Forbidden => _localizer[L.Watchdog.ConnectionForbidden],
            WatchdogClientErrorKind.ApiVersionMismatch => _localizer[L.Watchdog.ConnectionVersionMismatch],
            WatchdogClientErrorKind.NotFound => _localizer[L.Watchdog.ErrorNotFound],
            WatchdogClientErrorKind.Validation => _localizer[L.Watchdog.ErrorValidation],
            WatchdogClientErrorKind.VersionConflict => _localizer[L.Watchdog.ErrorVersionConflict],
            WatchdogClientErrorKind.Cancelled => _localizer[L.Errors.Cancelled],
            _ => client.StatusCode is { } status ? _localizer.Format(L.Watchdog.ErrorServerStatus, status) : _localizer[L.Watchdog.ErrorServer],
        };
    }
}
