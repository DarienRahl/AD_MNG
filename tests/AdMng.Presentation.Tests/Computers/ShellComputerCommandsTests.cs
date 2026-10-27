using AdMng.Core.Csv;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Shell;

namespace AdMng.Presentation.Tests.Computers;

/// <summary>The "Komputer" submenu and "Narzędzia → Przegląd komputerów…" of the main window (K1).</summary>
public class ShellComputerCommandsTests
{
    private static async Task<(ComputerScenario Scenario, ShellViewModel Shell)> OpenAsync(bool computerDialogs = true)
    {
        var scenario = await ComputerScenario.CreateAsync();
        var harness = scenario.Harness;
        var shell = new ShellViewModel(
            harness.Lists,
            new NavigationTreeViewModel(harness.Sessions, harness.Settings, harness.Localizer, harness.Dispatcher, harness.Errors, harness.Views, harness.Changes),
            harness.Activities,
            harness.Coordinator,
            harness.Views,
            computerDialogs ? scenario.Dialogs : harness.Dialogs,
            harness.Files,
            harness.Clipboard,
            harness.Notifications,
            new CsvCodec(),
            harness.Regional);
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(scenario.Company.ComputersOu));
        return (scenario, shell);
    }

    private static void Select(ShellViewModel shell, params string[] names)
    {
        var document = shell.SelectedDocument!;
        var rows = names.Select(n => document.Items.Single(r => r.Name == n)).ToList();
        document.SelectedItem = rows[0];
        document.SelectedItems = rows;
    }

    [Fact]
    public async Task ComputerCommands_FollowTheSelection()
    {
        var (scenario, shell) = await OpenAsync();
        using var _shell = shell;

        Assert.True(shell.ShowComputerReviewCommand.CanExecute(null));
        Assert.False(shell.ComputerRemoteActionsCommand.CanExecute(null));

        Select(shell, "KRK-PC101");
        Assert.True(shell.IsComputerSelection);
        Assert.True(shell.ComputerRemoteActionsCommand.CanExecute(null));
        Assert.True(shell.ComputerPingCommand.CanExecute(null));
        Assert.True(shell.ComputerResetAccountCommand.CanExecute(null));

        Select(shell, "KRK-PC101", "KRK-PC102");
        Assert.False(shell.ComputerRemoteActionsCommand.CanExecute(null));
        Assert.True(shell.ComputerResetAccountCommand.CanExecute(null));

        await shell.ShowComputerReviewCommand.ExecuteAsync(null);
        Assert.Equal(1, scenario.Dialogs.ReviewsShown);
        Assert.Contains(shell.CommandDescriptors, d => d.Id == "tools.computerReview");
        Assert.Contains(shell.CommandDescriptors, d => d.Id == "computer.reset");
    }

    [Fact]
    public async Task ComputerCommands_RunThroughTheComputerDialogs()
    {
        var (scenario, shell) = await OpenAsync();
        using var _shell = shell;
        Select(shell, "KRK-PC101");

        await shell.ComputerRemoteActionsCommand.ExecuteAsync(null);
        await shell.ComputerRemoteDesktopCommand.ExecuteAsync(null);
        await shell.ComputerManagementCommand.ExecuteAsync(null);

        Assert.Equal([scenario.Sample.Pro24H2], scenario.Dialogs.RemoteWindows);
        Assert.Equal([ComputerAction.RemoteDesktop, ComputerAction.ComputerManagement], scenario.Dialogs.Actions.Select(a => a.Action));
        Assert.Equal(2, scenario.Launcher.Launches.Count);
    }

    [Fact]
    public async Task ComputerCommands_AreOff_WithoutTheWindowsHost()
    {
        var (_, shell) = await OpenAsync(computerDialogs: false);
        using var _shell = shell;
        Select(shell, "KRK-PC101");

        Assert.False(shell.ShowComputerReviewCommand.CanExecute(null));
        Assert.False(shell.ComputerRemoteActionsCommand.CanExecute(null));
        Assert.False(shell.ComputerResetAccountCommand.CanExecute(null));
    }
}
