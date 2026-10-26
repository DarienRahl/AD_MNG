using AdMng.Presentation.Connections;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>An open connection window in the badge menu (the window switcher).</summary>
/// <param name="Id">The connection id.</param>
/// <param name="Label">"contoso.test · CONTOSO\jan.kowalski", or "Nie połączono".</param>
/// <param name="Server">The DC of the connection (empty while not connected).</param>
/// <param name="Color">The identity colour, or null while not connected (neutral grey).</param>
/// <param name="IsCurrent">The window the menu belongs to.</param>
public sealed record ConnectionWindowItem(Guid Id, string Label, string Server, ConnectionColor? Color, bool IsCurrent);

/// <summary>
/// The connection identity of the window (title, colour strip, badge), the window switcher and "Nowe połączenie w nowym
/// oknie…" (design note docs/dev/multi-window.md 4 and 7).
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>Id prefix of the generated palette entries that bring another connection window to the front.</summary>
    public const string ActivateWindowPrefix = "window.activate:";

    /// <summary>Recent containers the address bar shows (of the window's naming context).</summary>
    public const int RecentContainersShown = 10;

    private bool _attachedToWindow;

    /// <summary>The shell was disposed (its window closed).</summary>
    public bool IsDisposed => _disposed;

    /// <summary>The connection window this shell belongs to, or null in hosts without connection windows.</summary>
    public ConnectionContext? Connection => _connection;

    /// <summary>"contoso.test · CONTOSO\jan.kowalski", or "Nie połączono".</summary>
    public string ConnectionLabel => _connection?.Identity?.Label
        ?? (Sessions.Current is { } session ? $"{session.Info.DomainDnsName} · {session.Info.BoundIdentity}" : Localizer[L.Connections.NotConnected]);

    /// <summary>The identity colour of the window, or null while not connected (the strip and badge are neutral grey).</summary>
    public ConnectionColor? ConnectionColor => _connection?.Identity?.Color;

    /// <summary>Badge tool tip: DC, channel security and profile.</summary>
    public string ConnectionToolTip => _connection?.Identity is { } identity
        ? Localizer.Format(
            L.Connections.BadgeToolTip,
            identity.Server,
            Localizer[DisplayNames.SecurityKey(identity.Security)],
            identity.ProfileName ?? Localizer[L.Connections.NoProfile])
        : Localizer[L.Connections.NotConnected];

    /// <summary>The open connection windows for the badge menu (this window checked).</summary>
    public IReadOnlyList<ConnectionWindowItem> OpenWindows => _windows is null
        ? []
        : [.. _windows.OpenWindows.Select(w => new ConnectionWindowItem(
            w.Id,
            w.Identity?.Label ?? Localizer[L.Connections.NotConnected],
            w.Identity?.Server ?? string.Empty,
            w.Identity?.Color,
            w.Id == _connection?.Id))];

    /// <summary>The application supports several connection windows ("Nowe połączenie w nowym oknie…" is offered).</summary>
    public bool CanOpenConnectionWindows => _windows is not null;

    /// <summary>
    /// Called by the main window that shows this shell. A shell belongs to exactly one window: a second window of the same
    /// connection scope, or a window over a shell whose window already closed, fails loudly instead of showing a dead shell.
    /// </summary>
    /// <exception cref="InvalidOperationException">The shell is disposed or already shown by another window.</exception>
    public void AttachToWindow()
    {
        if (_disposed)
        {
            throw new InvalidOperationException("The shell of this connection window was disposed (its window closed); open a new connection window.");
        }

        if (_attachedToWindow)
        {
            throw new InvalidOperationException("The shell of this connection window is already shown by a main window.");
        }

        _attachedToWindow = true;
    }

    /// <summary>"Nowe połączenie w nowym oknie…" (Ctrl+Shift+N): an unowned connect window in a new connection scope.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenConnectionWindows))]
    private void NewConnectionWindow() => _windows?.OpenNewConnection();

    /// <summary>Brings another connection window to the front (badge menu, palette "Przejdź do okna: …").</summary>
    [RelayCommand]
    private void ActivateWindow(Guid connectionId)
    {
        if (_windows is not null && connectionId != Guid.Empty)
        {
            _windows.Activate(connectionId);
        }
    }

    /// <summary>
    /// The pins and recent containers of the window: only those under its own naming context (both lists are shared by all
    /// connection windows and are not deleted). Everything while not connected.
    /// </summary>
    private IEnumerable<T> FilterByNamingContext<T>(IEnumerable<T> items, Func<T, string> dnOf)
    {
        var namingContext = Sessions.Current?.Info.DefaultNamingContext;
        return items.Where(item => NamingContextFilter.Contains(namingContext, dnOf(item)));
    }

    /// <summary>The palette entries "Przejdź do okna: …" of the other open windows (rebuilt when the window list changes).</summary>
    private void RebuildWindowCommands()
    {
        if (_windows is null)
        {
            return;
        }

        var others = _windows.OpenWindows.Where(w => w.Id != _connection?.Id).ToList();
        CommandPalette.ReplaceGroup(ActivateWindowPrefix, others.Select(w => new CommandDescriptor(
            ActivateWindowPrefix + w.Id.ToString("N"),
            L.Connections.SwitchTo,
            L.CommandCategories.Session,
            string.Empty,
            ActivateWindowCommand)
        {
            Parameter = w.Id,
            TitleArguments = [w.Identity?.Label ?? Localizer[L.Connections.NotConnected]],
        }));
    }

    private void RaiseConnectionChanged()
    {
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(ConnectionColor));
        OnPropertyChanged(nameof(ConnectionToolTip));
        OnPropertyChanged(nameof(OpenWindows));
    }

    /// <summary>The window's identity changed (connected, another DC, another domain): title, strip and badge follow.</summary>
    private void OnConnectionChanged(object? sender, EventArgs e) => _ = _lists.Dispatcher.RunAsync(() =>
    {
        if (!_disposed)
        {
            OnPropertyChanged(nameof(WindowTitle));
            RaiseConnectionChanged();
        }
    });

    /// <summary>A connection window opened or closed, or changed its identity: the switcher and the palette follow.</summary>
    private void OnWindowsChanged(object? sender, EventArgs e) => _ = _lists.Dispatcher.RunAsync(() =>
    {
        if (!_disposed)
        {
            OnPropertyChanged(nameof(OpenWindows));
            RebuildWindowCommands();
        }
    });
}
