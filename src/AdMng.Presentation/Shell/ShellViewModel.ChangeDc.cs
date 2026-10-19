using AdMng.Core.Directory;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>"Change domain controller": the command and the shell's reaction to a session moved to another DC.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>File menu, the domain root's context menu and the palette (<c>session.changeDc</c>).</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task ChangeDomainControllerAsync() => _dialogs.ShowChangeDomainControllerAsync();

    /// <summary>
    /// Called when the current session changed to <paramref name="session"/>. A DC switch of the same connection (same
    /// domain and identity, see <see cref="DomainControllerSwitch.IsSwitch"/>) keeps the open tabs: the tree is rebuilt
    /// and every list reloads its location from the new DC. Returns false for any other change (the shell starts over).
    /// </summary>
    private bool TryReloadAfterDomainControllerChange(IDirectorySession session)
    {
        if (!DomainControllerSwitch.IsSwitch(_initializedFor, session))
        {
            return false;
        }

        _initializedFor = session;
        _initialization = ReloadAfterDomainControllerChangeAsync(session);
        _initialization.Forget();
        return true;
    }

    private async Task ReloadAfterDomainControllerChangeAsync(IDirectorySession session)
    {
        SelectedObject = null;
        try
        {
            await Tree.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or OperationCanceledException)
        {
            // The domain node shows its own load error.
        }

        if (_disposed || !ReferenceEquals(Sessions.Current, session))
        {
            // Disconnected, or a newer session started its own initialization meanwhile.
            return;
        }

        CheckPrivilegedIdentityAsync(session).Forget();
        RefreshCommandStates();
        if (!ListDocuments.Any())
        {
            await AddDocument()
                .NavigateAsync(ListLocation.ForContainer(session.Info.DefaultNamingContext, session.Info.DomainDnsName, ObjectIcon.Domain))
                .ConfigureAwait(true);
            return;
        }

        foreach (var list in ListDocuments.ToList())
        {
            list.LoadAsync().Forget();
        }

        if (SelectedDocument?.Location is { } location)
        {
            SyncTreeAsync(location).Forget();
        }

        UpdateDetailsAsync().Forget();
    }
}
