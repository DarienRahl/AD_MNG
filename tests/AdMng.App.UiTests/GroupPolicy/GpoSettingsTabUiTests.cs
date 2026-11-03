using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.GroupPolicy;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// The GPO document's "Ustawienia" tab (K6, docs/dev/k-computers-and-gpo.md 20.1): the settings grouped like GPMC's report
/// (configuration, then folder path), virtualizing; the search box (Ctrl+F switches to the tab and focuses it, Esc clears
/// it); the details of the selected row; "Edytuj to ustawienie", "Kopiuj" and "Raport HTML…".
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GpoSettingsTabUiTests
{
    [Fact]
    public Task SettingsTab_GroupsSearchesAndShowsTheSelectedSetting() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var sample = context.Directory.AddGroupPolicyManagementSample(context.Company, context.Sysvol);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();
        var document = Assert.IsType<GroupPolicyObjectDocumentViewModel>(shell.OpenGroupPolicyDocument(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Object) { GpoId = sample.WorkstationPasswords, Name = "Stacje robocze – Zasady haseł" }));
        await UiWait.UntilAsync(() => !document.IsLoading && document.SettingCount > 0, "the GPO's settings");
        main.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        var view = Assert.Single(UiWait.Descendants<GroupPolicyObjectView>(main));

        // Ctrl+F on another tab: the settings tab is shown and its search box takes the keyboard.
        Assert.Same(view.ScopeTab, view.GpoTabs.SelectedItem);
        await shell.FindCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync(rounds: 4);
        Assert.Same(view.SettingsTab, view.GpoTabs.SelectedItem);
        var search = Assert.Single(UiWait.Descendants<TextBox>(view), t => t.Name == "SettingsSearchBox");
        Assert.Same(search, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(search)));

        // Grouped like GPMC's report, virtualizing: configuration, then the folder path.
        var list = Assert.Single(UiWait.Descendants<ListView>(view), l => l.Name == "SettingsList");
        Assert.True(VirtualizingPanel.GetIsVirtualizingWhenGrouping(list));
        Assert.Equal(4, list.Items.Count);
        var groups = list.Items.Groups!.Cast<CollectionViewGroup>().ToList();
        Assert.Equal(["Konfiguracja komputera", "Obiekt zasad grupy"], groups.Select(g => (string)g.Name));
        Assert.Contains(groups[0].Items.Cast<CollectionViewGroup>(),
            g => (string)g.Name == "Zasady › Ustawienia systemu Windows › Ustawienia zabezpieczeń › Zasady konta › Zasady haseł" && g.ItemCount == 2);
        Assert.NotEmpty(UiWait.Descendants<GroupItem>(list));
        Assert.NotEmpty(UiWait.Descendants<PaneSplitter>(view));

        // The search filters; Esc clears it.
        document.SearchText = "długość";
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Single(list.Items);
        Assert.Equal(Visibility.Visible, Assert.Single(UiWait.Descendants<TextBlock>(view), t => t.Name == "SettingsSummaryText").Visibility);
        search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(string.Empty, document.SearchText);
        Assert.Equal(4, list.Items.Count);

        // A row: its details below the list; the commands follow the selection.
        var edit = Assert.Single(UiWait.Descendants<Button>(view), b => b.Name == "EditSettingButton");
        Assert.False(edit.IsEnabled);
        list.SelectedItem = document.AllSettings.Single(s => s.Name == "Minimalna długość hasła");
        await UiTestSupport.WaitForIdleAsync(rounds: 3);
        Assert.True(edit.IsEnabled);
        Assert.True(Assert.Single(UiWait.Descendants<Button>(view), b => b.Name == "CopySettingButton").IsEnabled);
        Assert.True(Assert.Single(UiWait.Descendants<Button>(view), b => b.Name == "SettingsReportButton").IsEnabled);
        Assert.Equal("Minimalna długość hasła", Assert.Single(UiWait.Descendants<TextBox>(view), t => t.Name == "SettingDetailsName").Text);
        Assert.Equal("12 znaków", Assert.Single(UiWait.Descendants<TextBox>(view), t => t.Name == "SettingDetailsValue").Text);
        Assert.Equal("Computer/Security/System Access/MinimumPasswordLength",
            Assert.Single(UiWait.Descendants<TextBox>(view), t => t.Name == "SettingDetailsTechnical").Text);
        Assert.Equal(Visibility.Collapsed, Assert.Single(UiWait.Descendants<TextBlock>(view), t => t.Name == "SettingDetailsHint").Visibility);

        // The GPO's comment has no editor.
        list.SelectedItem = document.AllSettings.Single(s => s.Name == "Komentarz do obiektu GPO");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.False(edit.IsEnabled);
        context.AssertHealthy();
    });
}
