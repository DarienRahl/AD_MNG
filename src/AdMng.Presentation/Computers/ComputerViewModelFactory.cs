using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Views;

namespace AdMng.Presentation.Computers;

/// <summary>
/// Creates the computer windows' view models (K1 design note 7) with the window's services: the review, the remote actions
/// of one computer and the restart dialog. One per connection window (scoped); the remote client, the resolver, ping and
/// the tool launcher are application-wide and optional (a host without them – tests, Linux – hides their actions).
/// </summary>
public sealed class ComputerViewModelFactory(
    DialogServices services,
    BatchEngines engines,
    ViewLibrary views,
    WindowsReleaseCatalog catalog,
    ComputerActions actions,
    Core.Audit.IAuditLog auditLog,
    PowerShellHistory powerShell,
    IDirectoryConnector? connector = null,
    IHostAddressResolver? resolver = null,
    IRemoteComputerClient? remote = null,
    IPingService? ping = null)
{
    private readonly DomainControllerReader _domainControllers = new(connector, services.Errors);

    /// <summary>The window's computer actions (tools, ping, reset).</summary>
    public ComputerActions Actions => actions;

    /// <summary>"Przegląd komputerów".</summary>
    public ComputerReviewViewModel CreateReview() =>
        new(services, views, engines.Csv, _domainControllers, catalog, actions, resolver);

    /// <summary>"Zdalne akcje" of one computer.</summary>
    public RemoteComputerViewModel CreateRemote(string distinguishedName) =>
        new(services, actions, catalog, auditLog, powerShell, distinguishedName, remote, ping);

    /// <summary>The restart dialog of a remote target.</summary>
    public RestartComputerViewModel CreateRestart(RemoteTarget target) => new(services, target);
}
