using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Session;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// "Historia zmian (Watchdog)" from objects in the main window (object context menu, details pane, property sheet): the
/// helpdesk's most frequent question – "what happened to this account?". Available when a service address is configured,
/// the object has a GUID and the service does not watch another domain than the window's session; the main window's shell
/// handles <see cref="Requested"/> by opening (or selecting) the Watchdog tab, switching to the events and filtering by
/// the object's GUID. One per connection window (scoped): a request never opens the tab of another window.
/// </summary>
public sealed class WatchdogObjectHistoryLauncher : IDisposable
{
    private readonly WatchdogConnectionService _connection;
    private readonly ISessionManager? _sessions;
    private bool _disposed;

    /// <param name="connection">The application's Watchdog service connection.</param>
    /// <param name="sessions">The window's session (the domain compared with the service's); without it no domain check.</param>
    public WatchdogObjectHistoryLauncher(WatchdogConnectionService connection, ISessionManager? sessions = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _sessions = sessions;
        _connection.AddressChanged += OnAvailabilityInputChanged;
        _connection.ServiceDomainChanged += OnAvailabilityInputChanged;
        if (_sessions is not null)
        {
            _sessions.SessionChanged += OnSessionChanged;
            if (_sessions.Current is not null)
            {
                _connection.EnsureServiceDomainAsync().Forget();
            }
        }
    }

    /// <summary>The history of an object was requested.</summary>
    public event EventHandler<WatchdogHistoryRequest>? Requested;

    /// <summary>The service address was set or removed, or the service's or the window's domain changed (commands re-evaluate <see cref="IsAvailable"/>).</summary>
    public event EventHandler? AvailabilityChanged;

    /// <summary>
    /// A watchdog service address is configured and the service is not known to watch another domain than this window's
    /// session (its object DNs and GUIDs would not match).
    /// </summary>
    public bool IsAvailable => _connection.HasServiceUrl && !IsOtherDomain;

    /// <summary>The service watches another domain than this window is connected to.</summary>
    public bool IsOtherDomain => _connection.IsOtherDomain(_sessions?.Current?.Info.DomainDnsName);

    public bool CanShow(Guid? objectGuid) => objectGuid is not null && IsAvailable;

    /// <summary>Asks the main window to show the history of the object. Returns false when the watchdog is not available.</summary>
    public bool Show(Guid objectGuid, string? name)
    {
        if (!IsAvailable)
        {
            return false;
        }

        Requested?.Invoke(this, new WatchdogHistoryRequest(objectGuid, name));
        return true;
    }

    /// <summary>Ends the subscriptions to the application-wide connection (the window closed).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.AddressChanged -= OnAvailabilityInputChanged;
        _connection.ServiceDomainChanged -= OnAvailabilityInputChanged;
        if (_sessions is not null)
        {
            _sessions.SessionChanged -= OnSessionChanged;
        }
    }

    private void OnAvailabilityInputChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            if (ReferenceEquals(sender, _connection) && _sessions?.Current is not null)
            {
                // A new address: learn the domain of that service too.
                _connection.EnsureServiceDomainAsync().Forget();
            }

            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_sessions?.Current is not null)
        {
            _connection.EnsureServiceDomainAsync().Forget();
        }

        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
