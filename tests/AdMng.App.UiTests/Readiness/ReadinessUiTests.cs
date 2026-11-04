using System.Windows;
using System.Windows.Controls;
using AdMng.App.Behaviors;
using AdMng.App.Infrastructure;
using AdMng.App.Readiness;
using AdMng.App.UiTests.EventLogs;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Readiness;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.Readiness;

/// <summary>
/// "Gotowość środowiska" (stage S) in the main window over the in-memory directory with two DCs: Narzędzia and Pomoc offer
/// it, the tab opens with the quick check, the view shows every area with its rows, the full check with a typed test
/// computer runs through the fakes of the host, the results reach <see cref="IReadinessResults"/> of the window, and
/// nothing raises a binding error.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ReadinessUiTests
{
    [Fact]
    public Task Readiness_FromTheMenus_RunsTheQuickAndTheFullCheck_AndShowsEveryArea() => WpfTestHost.RunAsync(async () =>
    {
        var connector = EventLogUiSupport.CreateConnector();
        await using var context = await UiTestContext.CreateAsync(language: "pl", EventLogUiSupport.Use(connector));
        await EventLogUiSupport.AddDomainControllersAsync(context);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();

        // Narzędzia and Pomoc both offer "Sprawdź gotowość środowiska…", wired to the shell command.
        var menus = LogicalTreeHelper.GetChildren(main.MainMenu).OfType<MenuItem>()
            .Where(m => m.Items.OfType<MenuItem>().Any(i => CommandMenu.GetId(i) == "tools.readiness"))
            .ToList();
        Assert.Equal(2, menus.Count);
        var item = menus[^1].Items.OfType<MenuItem>().Single(i => CommandMenu.GetId(i) == "tools.readiness");
        Assert.Same(shell.ShowReadinessCommand, item.Command);
        Assert.True(item.IsEnabled);

        shell.ShowReadinessCommand.Execute(null);
        var document = Assert.IsType<ReadinessDocumentViewModel>(shell.SelectedTab);
        await UiWait.UntilAsync(() => !document.IsLoading && document.HasResult, "the quick check");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        var view = Assert.Single(UiWait.Descendants<ReadinessView>(main));
        Assert.Equal("Gotowość środowiska", view.TitleText.Text);
        Assert.Equal(TextTrimming.CharacterEllipsis, view.TitleText.TextTrimming);
        Assert.Equal(Enum.GetValues<ReadinessArea>().Length, view.GroupsList.Items.Count);
        Assert.Equal(document.SummaryText, view.SummaryTextBlock.Text);
        Assert.True(view.QuickCheckButton.IsEnabled);
        Assert.True(view.FullCheckButton.IsEnabled);
        Assert.False(view.CancelButton.IsEnabled);
        Assert.True(view.ExportHtmlButton.IsEnabled);
        Assert.True(view.ExportCsvButton.IsEnabled);
        Assert.False(view.StaleBar.IsVisible);
        Assert.False(view.CancelledText.IsVisible);
        Assert.StartsWith("Ostatnie sprawdzenie: szybkie", view.LastRunText.Text, StringComparison.Ordinal);

        // The full check with a typed test computer: the remote checks and the event logs go through the host's fakes.
        view.TestComputerBox.Text = "WAW-PC001";
        Assert.Equal("WAW-PC001", document.TestComputerText);
        await document.FullCheckCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !document.IsLoading, "the full check");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal(ReadinessDepth.Full, document.LastDepth);
        Assert.StartsWith("Ostatnie sprawdzenie: pełne", view.LastRunText.Text, StringComparison.Ordinal);
        Assert.Contains(EventLogUiSupport.Dc01, connector.OpenedHosts, StringComparer.OrdinalIgnoreCase);
        var results = context.Services.GetRequiredService<IReadinessResults>();
        Assert.Equal(ReadinessDepth.Full, results.LastDepth);
        Assert.Equal(ReadinessCodes.All.Select(c => c.Code), results.Last.Select(p => p.Code));
        Assert.Empty(context.Sysvol.Writes);
        context.AssertHealthy();
    });
}
