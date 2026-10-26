namespace AdMng.Presentation.Connections;

/// <summary>
/// The read-only view of another connection window that other windows get (window switcher, and in stage 2 the
/// comparison). It never exposes that window's session manager, coordinator, dialogs, notifier or credential.
/// </summary>
public interface IConnectionHandle
{
    /// <summary>The connection window's id (stable while the window is open).</summary>
    Guid Id { get; }

    /// <summary>Domain, account, DC and colour of the connection; null while the window is not connected.</summary>
    ConnectionIdentity? Identity { get; }

    /// <summary>The identity changed: connected, disconnected, another domain controller or another domain.</summary>
    event EventHandler? Changed;

    /// <summary>The window closed (raised once; the handle then drops its subscribers).</summary>
    event EventHandler? Closed;
}

/// <summary>An open connection window, as the window switcher lists it.</summary>
/// <param name="Id">The connection id (<see cref="IConnectionHandle.Id"/>).</param>
/// <param name="Identity">The identity, or null for a window that is not connected ("Nie połączono").</param>
public sealed record ConnectionWindowInfo(Guid Id, ConnectionIdentity? Identity);

/// <summary>
/// The connection windows of the application (implemented by the WPF app's window manager): open a new connection in a
/// new window, bring another window to the front, and the list of open main windows for the window switcher.
/// </summary>
public interface IConnectionWindows
{
    /// <summary>The open main windows in opening order (connected or not).</summary>
    IReadOnlyList<ConnectionWindowInfo> OpenWindows { get; }

    /// <summary><see cref="OpenWindows"/> changed (a window opened or closed, or its identity changed). Raised on the UI thread.</summary>
    event EventHandler? WindowsChanged;

    /// <summary>"Nowe połączenie w nowym oknie…": an unowned connect window in a new connection scope.</summary>
    void OpenNewConnection();

    /// <summary>Brings the main window of the connection to the front (restored when minimized).</summary>
    void Activate(Guid connectionId);
}
