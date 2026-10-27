using AdMng.Core.Computers.Remote;
using AdMng.Presentation.Computers;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.Services;

/// <summary>
/// The computer windows (K1, <see cref="IComputerDialogs"/>): "Przegląd komputerów" and "Zdalne akcje" (modeless, one per
/// connection window and one per computer, through <see cref="ToolWindows"/>), the restart dialog (modal) and the computer
/// actions of this connection window.
/// </summary>
public sealed partial class DialogService : IComputerDialogs
{
    private ComputerViewModelFactory? _computers;

    private ComputerViewModelFactory Computers => _computers ??= _services.GetRequiredService<ComputerViewModelFactory>();

    ComputerActions IComputerDialogs.Actions => Computers.Actions;

    public Task ShowComputerReviewAsync() => OnUiThreadAsync(() =>
    {
        _tools.ShowComputerReview();
        return true;
    });

    public Task ShowRemoteActionsAsync(string distinguishedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        return OnUiThreadAsync(() =>
        {
            _tools.ShowRemoteComputer(distinguishedName);
            return true;
        });
    }

    public async Task<RestartRequest?> AskRestartAsync(RemoteTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        RestartRequest? request = null;
        await OnUiThreadAsync(() =>
        {
            var dialog = Computers.CreateRestart(target);
            request = ShowWindowModal(dialog) ? dialog.Request : null;
            return request is not null;
        }).ConfigureAwait(true);
        return request;
    }

    public Task RunComputerActionAsync(ComputerAction action, IReadOnlyList<string> distinguishedNames)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        return IsClosed ? Task.CompletedTask : Computers.Actions.RunAsync(action, distinguishedNames);
    }
}
