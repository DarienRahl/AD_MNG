using AdMng.Core.Model;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// Computers (K1): "Narzędzia → Przegląd komputerów…" and the "Komputer" submenu of the object list (remote actions,
/// Remote Desktop, ping, Computer Management, Event Viewer, Reset Account) for selected computers. The windows and the
/// actions are reached through the window's dialog service when it implements <see cref="IComputerDialogs"/>.
/// </summary>
public sealed partial class ShellViewModel
{
    private IRelayCommand[]? _computerStateCommands;

    private IComputerDialogs? ComputerDialogs => _dialogs as IComputerDialogs;

    /// <summary>The selection of the current list consists of computers only.</summary>
    public bool IsComputerSelection => CurrentSelection is { Count: > 0 } selection && selection.All(r => r.Kind == ObjectKind.Computer);

    private IRelayCommand[] ComputerStateCommands => _computerStateCommands ??=
    [
        ShowComputerReviewCommand, ComputerRemoteActionsCommand, ComputerRemoteDesktopCommand, ComputerPingCommand,
        ComputerManagementCommand, ComputerEventViewerCommand, ComputerResetAccountCommand,
    ];

    private IEnumerable<CommandDescriptor> ComputerCommandDescriptors()
    {
        const string obj = L.CommandCategories.Object;
        const string tools = L.CommandCategories.Tools;
        return
        [
            new("tools.computerReview", L.Computers.ComputerReviewCommand, tools, string.Empty, ShowComputerReviewCommand),
            new("computer.remote", L.Computers.RemoteActionsCommand, obj, string.Empty, ComputerRemoteActionsCommand),
            new("computer.remoteDesktop", L.Computers.RemoteDesktop, obj, string.Empty, ComputerRemoteDesktopCommand),
            new("computer.ping", L.Computers.Ping, obj, string.Empty, ComputerPingCommand),
            new("computer.manage", L.Computers.ComputerManagement, obj, string.Empty, ComputerManagementCommand),
            new("computer.eventViewer", L.Computers.EventViewer, obj, string.Empty, ComputerEventViewerCommand),
            new("computer.reset", L.Computers.ResetAccountCommand, obj, string.Empty, ComputerResetAccountCommand),
        ];
    }

    [RelayCommand(CanExecute = nameof(CanShowComputerReview))]
    private Task ShowComputerReviewAsync() => ComputerDialogs?.ShowComputerReviewAsync() ?? Task.CompletedTask;

    private bool CanShowComputerReview() => ComputerDialogs is not null && Sessions.Current is not null;

    [RelayCommand(CanExecute = nameof(HasSingleComputer))]
    private Task ComputerRemoteActionsAsync() => RunComputerActionAsync(ComputerAction.RemoteActions);

    [RelayCommand(CanExecute = nameof(HasSingleComputer))]
    private Task ComputerRemoteDesktopAsync() => RunComputerActionAsync(ComputerAction.RemoteDesktop);

    [RelayCommand(CanExecute = nameof(HasSingleComputer))]
    private Task ComputerPingAsync() => RunComputerActionAsync(ComputerAction.Ping);

    [RelayCommand(CanExecute = nameof(HasSingleComputer))]
    private Task ComputerManagementAsync() => RunComputerActionAsync(ComputerAction.ComputerManagement);

    [RelayCommand(CanExecute = nameof(HasSingleComputer))]
    private Task ComputerEventViewerAsync() => RunComputerActionAsync(ComputerAction.EventViewer);

    [RelayCommand(CanExecute = nameof(HasComputers))]
    private Task ComputerResetAccountAsync() => RunComputerActionAsync(ComputerAction.ResetAccount);

    private bool HasSingleComputer() => ComputerDialogs is not null && IsComputerSelection && CurrentSelection.Count == 1;

    private bool HasComputers() => ComputerDialogs is not null && IsComputerSelection;

    private Task RunComputerActionAsync(ComputerAction action)
    {
        var selection = CurrentSelection.Where(r => r.Kind == ObjectKind.Computer).Select(r => r.Dn).ToList();
        return selection.Count == 0 || ComputerDialogs is not { } dialogs
            ? Task.CompletedTask
            : action == ComputerAction.RemoteActions
                ? dialogs.ShowRemoteActionsAsync(selection[0])
                : dialogs.RunComputerActionAsync(action, selection);
    }
}
