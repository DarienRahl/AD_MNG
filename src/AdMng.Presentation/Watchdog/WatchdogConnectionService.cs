using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Watchdog.Client;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// Connects <see cref="IWatchdogClient"/> to the service address of the settings (<see cref="WatchdogClientSettings"/>):
/// builds <see cref="WatchdogClientOptions"/> (process identity – <c>runas /netonly</c> works; optional certificate pin),
/// accepts only <c>https://</c> addresses, and maps <see cref="WatchdogClientException.Kind"/> to a connection state and a
/// localized message. Singleton shared by the Watchdog tab and the object history launcher.
/// </summary>
public sealed class WatchdogConnectionService
{
    private readonly ISettingsStore _settings;
    private readonly ILocalizer _localizer;

    public WatchdogConnectionService(IWatchdogClient client, ISettingsStore settings, ILocalizer localizer)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <summary>Raised after the service address was set or removed (on the calling thread).</summary>
    public event EventHandler? AddressChanged;

    public IWatchdogClient Client { get; }

    /// <summary>The configured address (settings), or null.</summary>
    public string? ServiceUrl => _settings.Current.Watchdog.ServiceUrl;

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
        WatchdogClientErrorKind.CertificateInvalid => WatchdogConnectionState.CertificateInvalid,
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
    /// Configures the client with the address of the settings when it is not configured with it yet. Returns false when
    /// no valid address is stored (the client is left alone). Throws when the client rejects the options.
    /// </summary>
    public bool EnsureConfigured()
    {
        if (!TryParseAddress(ServiceUrl, out var address))
        {
            return false;
        }

        if (Client.IsConfigured && Client.BaseAddress == address)
        {
            return true;
        }

        Client.Configure(new WatchdogClientOptions
        {
            BaseAddress = address,
            PinnedCertificateSha256 = _settings.Current.Watchdog.PinnedCertificateSha256,
        });
        return true;
    }

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
        await _settings.UpdateAsync(s => s.Watchdog.ServiceUrl = normalized).ConfigureAwait(true);
        AddressChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Removes the stored address (the tab shows the address editor again).</summary>
    public async Task ClearAddressAsync()
    {
        await _settings.UpdateAsync(s => s.Watchdog.ServiceUrl = null).ConfigureAwait(true);
        AddressChanged?.Invoke(this, EventArgs.Empty);
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
