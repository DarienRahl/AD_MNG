namespace AdMng.Presentation.Watchdog;

/// <summary>
/// "Historia zmian (Watchdog)" from objects in the main window (object context menu, details pane, property sheet): the
/// helpdesk's most frequent question – "what happened to this account?". Available when a service address is configured
/// and the object has a GUID; the main window's shell handles <see cref="Requested"/> by opening (or selecting) the
/// Watchdog tab, switching to the events and filtering by the object's GUID. Singleton.
/// </summary>
public sealed class WatchdogObjectHistoryLauncher
{
    private readonly WatchdogConnectionService _connection;

    public WatchdogObjectHistoryLauncher(WatchdogConnectionService connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _connection.AddressChanged += (_, _) => AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The history of an object was requested.</summary>
    public event EventHandler<WatchdogHistoryRequest>? Requested;

    /// <summary>The service address was set or removed (commands re-evaluate <see cref="IsAvailable"/>).</summary>
    public event EventHandler? AvailabilityChanged;

    /// <summary>A watchdog service address is configured.</summary>
    public bool IsAvailable => _connection.HasServiceUrl;

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
}
