using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AdMng.App.Connections;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Comparison;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Services;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// "Porównaj z obiektem w innym połączeniu…" in the real application host (design note docs/dev/multi-window.md 10.3):
/// the command of window A opens the comparison window owned by A, the other connection is preselected, the object is
/// matched automatically, the tabs show the differences, CSV goes through the file dialog, and closing B ends side B.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CompareObjectsWindowTests
{
    [Fact]
    public Task TheCommandOfWindowA_ComparesWithB_AndClosingBEndsSideB() => WpfTestHost.RunAsync(async () =>
    {
        var files = new PreparedSaveDialog();
        await using var context = await UiTestContext.CreateAsync(configure: s => s.AddSingleton<IFileDialogService>(files));
        await ComparisonFixture.AddFabrikamAsync(context);
        await UiTestContext.ConnectAsync(context.Services, FakeDirectoryBuilder.DefaultDomain, "jan.kowalski");
        var other = context.CreateConnection();
        await UiTestContext.ConnectAsync(other, ComparisonFixture.Fabrikam, "anna.zielinska");
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        await main.Shell.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
        await UiWait.UntilAsync(() => main.Shell.SelectedDocument?.IsFullyLoaded == true, "the IT list");
        var document = main.Shell.SelectedDocument!;
        document.SelectedItem = document.Items.Single(r => r.Name == "Łukasz Wiśniewski");
        main.Shell.RefreshCommandStates();

        // The list's context menu and the Action menu carry the command; it opens the window of this connection.
        Assert.Contains(main.Shell.CommandDescriptors, c => c.Id == "object.compareWithConnection");
        Assert.True(main.Shell.CompareWithConnectionCommand.CanExecute(null));
        await main.Shell.CompareWithConnectionCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => Application.Current.Windows.OfType<CompareObjectsWindow>().Any(w => w.IsVisible), "the comparison window");
        var window = Application.Current.Windows.OfType<CompareObjectsWindow>().Single();
        var comparison = window.ViewModel;
        await UiWait.UntilAsync(() => comparison.HasResult && !comparison.IsRunning && !comparison.IsNestedLoading, "the comparison");

        Assert.Same(main, window.Owner);
        Assert.True(ConnectionChrome.BelongsTo(window, context.Connection.Id));
        Assert.Equal("Comparison: Łukasz Wiśniewski – contoso.test ↔ fabrikam.test – contoso.test", window.Title);
        Assert.Equal(other.GetRequiredService<ConnectionContext>().Id, comparison.SelectedTarget?.Id);
        Assert.Equal([Attr.City, Attr.Title], comparison.AttributeRows.Select(r => r.LdapName).Order(StringComparer.Ordinal));
        Assert.Equal(2, window.AttributesList.Items.Count);
        Assert.Equal("Attributes (2)", window.AttributesTab.Header);

        // A selected row lists its values; each value, and each connection of the combo, is read by its text (never by the
        // value-free ToString()), and selecting a row raises no binding error (AssertHealthy below).
        comparison.SelectedAttributeRow = comparison.AttributeRows.Single(r => r.LdapName == Attr.City);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var values = comparison.SelectedAttributeRow.Details;
        Assert.Equal(values.Count, window.ValuesList.Items.Count);
        var firstValue = Assert.IsType<ListViewItem>(window.ValuesList.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.Equal(values[0].Text, AutomationProperties.GetName(firstValue));
        Assert.Equal($"A: {values[0].MarkText}", AutomationProperties.GetHelpText(firstValue));
        Assert.Equal("Label", TextSearch.GetTextPath(window.TargetBox));
        window.TargetBox.IsDropDownOpen = true;
        await UiWait.UntilAsync(() => window.TargetBox.ItemContainerGenerator.ContainerFromIndex(0) is ComboBoxItem, "the connection items");
        var targetItem = (ComboBoxItem)window.TargetBox.ItemContainerGenerator.ContainerFromIndex(0);
        Assert.Equal(comparison.SelectedTarget!.Label, AutomationProperties.GetName(targetItem));
        Assert.Contains("FABRIKAM\\anna.zielinska", AutomationProperties.GetName(targetItem), StringComparison.Ordinal);
        window.TargetBox.IsDropDownOpen = false;
        comparison.SelectedAttributeRow = null;
        await UiTestSupport.WaitForIdleAsync();

        // Toggles: everything that is the same, then the membership tab.
        window.OnlyDifferencesBox.IsChecked = false;
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.AttributesList.Items.Count > 2);
        window.Tabs.SelectedItem = window.MembershipTab;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(2, window.MembershipList.Items.Count);
        Assert.Equal("Membership (2)", window.MembershipTab.Header);

        // CSV only on request, through the file dialog of this connection.
        var path = Path.Combine(Path.GetTempPath(), $"admng-compare-{Guid.NewGuid():N}.csv");
        files.NextSavePath = path;
        try
        {
            await comparison.ExportCsvCommand.ExecuteAsync(null);
            var csv = await File.ReadAllTextAsync(path);
            Assert.Contains("GG_Sprzedaz", csv, StringComparison.Ordinal);
            Assert.Contains("Starszy programista", csv, StringComparison.Ordinal);
            Assert.Equal("comparison-Łukasz Wiśniewski-contoso.test-fabrikam.test.csv", files.LastSuggestedName);
        }
        finally
        {
            File.Delete(path);
        }

        // B's window closes: side B ends, its results stay.
        await ((IAsyncDisposable)other).DisposeAsync();
        await UiWait.UntilAsync(() => comparison.SideB.IsEnded, "side B ended");
        Assert.Contains("was closed", comparison.SideB.EndedMessage, StringComparison.Ordinal);
        Assert.NotEmpty(comparison.AttributeRows);
        Assert.Equal(AttributeComparisonState.Different, comparison.AttributeRows.First(r => r.LdapName == Attr.Title).State);
        context.AssertHealthy();
    });

    [Fact]
    public Task WithoutAnotherConnection_TheWindowSaysHowToOpenOne() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await UiTestContext.ConnectAsync(context.Services, FakeDirectoryBuilder.DefaultDomain, "jan.kowalski");
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var launcher = context.Services.GetRequiredService<Presentation.Comparison.IObjectComparisonLauncher>();

        await launcher.CompareAsync(context.Company.JanKowalski);
        await UiWait.UntilAsync(() => Application.Current.Windows.OfType<CompareObjectsWindow>().Any(w => w.IsVisible), "the comparison window");
        var window = Application.Current.Windows.OfType<CompareObjectsWindow>().Single();
        await UiWait.InitializedAsync(window.Initialization);

        Assert.Equal(Visibility.Visible, window.NoTargetsBar.Visibility);
        Assert.False(window.CompareButton.IsEnabled);
        Assert.False(window.ExportButton.IsEnabled);
        Assert.Equal(Visibility.Collapsed, window.MembersTab.Visibility);
        context.AssertHealthy();
    });
}
