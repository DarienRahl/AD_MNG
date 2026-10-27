using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AdMng.App.Computers;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests.Computers;

/// <summary>
/// The computer windows (K1, docs/dev/k-computers-and-gpo.md 10): the review on the inventory sample, the remote actions
/// with the fake client (restart with the typed name), the restart dialog, the prestage fields of "Nowy komputer", the
/// "Komputer" submenu, the "Stan" page of the property sheet and the "Komputery" tab of the settings.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ComputerWindowsTests
{
    private const string FileServerHost = "srv-pliki01.contoso.test";

    [Fact]
    public Task ToolsMenu_OpensOneReview_TheEndOfSupportTileListsOldSystems_AndTheDetailsFollowTheSelection() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        context.Directory.AddComputerInventorySample(context.Company);
        await context.ConnectAsync();
        var windows = context.Services.GetRequiredService<IWindowService>();
        Assert.True(windows.IsRegistered(typeof(ComputerReviewViewModel)));
        Assert.True(windows.IsRegistered(typeof(RemoteComputerViewModel)));
        Assert.True(windows.IsRegistered(typeof(RestartComputerViewModel)));
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        Assert.Contains(main.Shell.CommandDescriptors, d => d.Id == "tools.computerReview");

        // Tools → Computer review, twice: one window per connection window.
        Assert.True(main.Shell.ShowComputerReviewCommand.CanExecute(null));
        await main.Shell.ShowComputerReviewCommand.ExecuteAsync(null);
        await main.Shell.ShowComputerReviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        var window = Assert.Single(Application.Current.Windows.OfType<ComputerReviewWindow>());
        Assert.Same(window, windows.FindOpen(ToolWindows.ComputerReviewKey));
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        var review = window.ViewModel;
        Assert.Equal(Visibility.Visible, window.NotRunHint.Visibility);
        Assert.Equal(Visibility.Collapsed, window.TilesList.Visibility);
        Assert.Equal(Enum.GetValues<ComputerReport>().Length, window.ReportOptionsList.Items.Count);
        Assert.Equal(review.CatalogText, window.CatalogText.Text);
        Assert.True(window.RunButton.IsEnabled);

        await review.RunCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !review.IsBusy, "the review and its owner and creator reads");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(review.Error);
        Assert.Equal(Visibility.Visible, window.TilesList.Visibility);
        Assert.Equal(Visibility.Collapsed, window.NotRunHint.Visibility);
        Assert.Equal(review.Tiles.Count, window.TilesList.Items.Count);
        Assert.Equal(review.StatusText, window.StatusText.Text);

        // End of support: Windows 7 and Server 2012 R2 ended long ago (independent of the day the test runs).
        window.TilesList.SelectedItem = review.Tiles.Single(t => t.Report == ComputerReport.EndOfSupport);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(review.Columns.Count, window.ItemsGrid.Columns.Count);
        var windows7 = review.Items.Single(r => r.Name == "LOD-PC107");
        Assert.Contains(review.Items, r => r.Name == "SRV-STARY04");
        Assert.Equal(review.Items.Count, window.ItemsList.Items.Count);

        // The details pane of the selected computer; the actions of one computer become available.
        window.ItemsList.SelectedItem = windows7;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.NotEmpty(window.DetailsList.Items);
        Assert.Contains(UiWait.Descendants<TextBox>(window.DetailsList), t => t.Text.Contains("Windows 7", StringComparison.Ordinal));
        Assert.Equal(Visibility.Visible, window.RemoteActionsButton.Visibility);
        Assert.True(window.RemoteActionsButton.IsEnabled);
        Assert.True(window.PropertiesButton.IsEnabled);
        Assert.True(window.ExportButton.IsEnabled);

        // Another tile rebuilds the columns.
        window.TilesList.SelectedItem = review.Tiles.First(t => t.Report == ComputerReport.ServersWithoutOwner);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(review.Columns.Count, window.ItemsGrid.Columns.Count);
        Assert.Contains(review.Items, r => r.Name == "SRV-APP02");
        context.AssertHealthy();
    });

    [Fact]
    public Task RemoteWindow_WithTheFakeClient_ReadsStatusUsersAndServices_RefreshesPolicy_AndRestartsAfterTheTypedName() => WpfTestHost.RunAsync(async () =>
    {
        var remote = new FakeRemoteComputerClient();
        var server = new FakeRemoteComputer
        {
            OperatingSystem = new("Microsoft Windows Server 2022 Standard", "10.0.20348", 20348, 2762, new DateTimeOffset(2026, 10, 20, 5, 30, 0, TimeSpan.Zero)),
        };
        server.Users.Add(new LoggedOnUser(@"CONTOSO\łukasz.wiśniewski", 2, new DateTimeOffset(2026, 10, 27, 7, 45, 0, TimeSpan.Zero), IsConsole: false));
        server.Services.Add(new RemoteService("Spooler", "Bufor wydruku", "Stopped", "Auto", false, "LocalSystem"));
        server.Services.Add(new RemoteService("LanmanServer", "Serwer", "Running", "Auto", false, "LocalSystem"));
        remote.Computers[FileServerHost] = server;
        var ping = new FakePingService();
        ping.Answering[FileServerHost] = TimeSpan.FromMilliseconds(1);
        await using var context = await UiTestContext.CreateAsync("pl", services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IRemoteComputerClient>(remote));
            services.Replace(ServiceDescriptor.Singleton<IPingService>(ping));
        });
        var sample = context.Directory.AddComputerInventorySample(context.Company);
        await context.ConnectAsync();
        UiTestSupport.ShowOffScreen(context.Services.GetRequiredService<MainWindow>());

        // One window per computer, opened through the window's dialog service (as the "Komputer" submenu does).
        var dialogs = Assert.IsAssignableFrom<IComputerDialogs>(context.Services.GetRequiredService<IDialogService>());
        await dialogs.ShowRemoteActionsAsync(sample.FileServer);
        await dialogs.ShowRemoteActionsAsync(sample.FileServer);
        await UiTestSupport.WaitForIdleAsync();
        var window = Assert.Single(Application.Current.Windows.OfType<RemoteComputerWindow>());
        Assert.Same(window, context.Services.GetRequiredService<IWindowService>().FindOpen(RemoteComputerViewModel.WindowKey(sample.FileServer)));
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        var vm = window.ViewModel;
        Assert.Null(vm.Error);
        Assert.Equal(FileServerHost, window.HostText.Text);
        Assert.Contains("Kerberos", window.ConnectionText.Text, StringComparison.Ordinal);

        await vm.RefreshStatusCommand.ExecuteAsync(null);
        await vm.LoadUsersCommand.ExecuteAsync(null);
        await vm.LoadServicesCommand.ExecuteAsync(null);
        await vm.RefreshPolicyCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(vm.Error);
        Assert.Contains("Windows Server 2022", window.SystemText.Text, StringComparison.Ordinal);
        Assert.Contains("20348.2762", window.SystemText.Text, StringComparison.Ordinal);
        Assert.NotEmpty(window.PingText.Text);
        Assert.Single(window.UsersList.Items);
        Assert.Equal(2, window.ServicesList.Items.Count);
        window.OnlyStoppedAutomaticBox.IsChecked = true;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("Spooler", Assert.IsType<RemoteServiceRow>(Assert.Single(window.ServicesList.Items)).Name);
        Assert.Equal(4, window.LogList.Items.Count);
        Assert.Equal([FileServerHost], ping.Pinged);

        // "Uruchom ponownie…" on a server: the name must be typed; the message reaches the computer verbatim.
        const string message = "Serwer plików uruchomi się ponownie o 18:00 – zapisz pracę. Dział IT, Łódź";
        var drive = UiWait.DriveModalAsync<RestartComputerWindow>(async dialog =>
        {
            await UiTestSupport.WaitForIdleAsync();
            Assert.True(dialog.ViewModel.NeedsTypedName);
            Assert.True(dialog.TypedNameBox.IsVisible);
            Assert.False(dialog.ConfirmButton.IsEnabled);
            dialog.RestartMessageBox.Text = message;
            dialog.TypedNameBox.Text = "srv-pliki01";
            await UiTestSupport.WaitForIdleAsync();
            Assert.True(dialog.ConfirmButton.IsEnabled);
            dialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        });
        await vm.RestartCommand.ExecuteAsync(null);
        await drive;
        await UiTestSupport.WaitForIdleAsync();

        var restart = Assert.Single(remote.Calls, c => c.Operation == "ScheduleRestart");
        Assert.Equal(message, restart.Message);
        Assert.Equal(TimeSpan.FromMinutes(10), restart.Delay);
        Assert.False(restart.Force);
        Assert.Null(vm.Error);
        Assert.Equal(5, window.LogList.Items.Count);

        // Closing the window: nothing of the computer is kept.
        window.Close();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.True(vm.IsDisposed);
        context.AssertHealthy();
    });

    [Fact]
    public Task RestartDialog_OfAWorkstation_NeedsNoName_AndRefusesADelayOutOfRange() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        await context.ConnectAsync();
        var factory = context.Services.GetRequiredService<ComputerViewModelFactory>();
        var target = new RemoteTarget("CN=KRK-PC101,OU=Komputery,OU=Firma,DC=contoso,DC=test", "KRK-PC101", "krk-pc101.contoso.test", ComputerType.Workstation);
        var window = (RestartComputerWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(factory.CreateRestart(target));
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync();

        Assert.False(window.TypedNameBox.IsVisible);
        Assert.True(window.ConfirmButton.IsEnabled);
        Assert.Contains("10", window.SummaryText.Text, StringComparison.Ordinal);
        Assert.Contains("10", window.RestartMessageBox.Text, StringComparison.Ordinal);

        window.DelayBox.Text = "1441";
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.ConfirmButton.IsEnabled);
        Assert.NotEmpty(window.ErrorText.Text);

        window.DelayBox.Text = "0";
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.ConfirmButton.IsEnabled);
        window.ConfirmButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Equal(TimeSpan.Zero, window.ViewModel.Request?.Delay);
        Assert.True(window.Result);
        context.AssertHealthy();
    });

    [Fact]
    public Task NewComputer_PrestageFields_GrantTheJoinRightThroughThePlan() => WpfTestHost.RunAsync(async () =>
    {
        var (created, dialogs) = await TestDialogService.CreateContextAsync("pl");
        await using var context = created;
        context.Directory.AddComputerInventorySample(context.Company);
        var helpdesk = context.Directory.AddGroup("GG_Helpdesk_Dołączanie", context.Company.GroupsOu);
        await context.ConnectAsync();
        var dialog = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateNewObject(ObjectKind.Computer, context.Company.ComputersOu);
        var window = (NewObjectWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();

        Assert.True(window.ChangeJoinerButton.IsVisible);
        Assert.False(window.ClearJoinerButton.IsVisible);
        Assert.Equal(dialog.JoinerText, window.JoinerText.Text);

        dialog.Name = "GDA-PC205";
        window.LocationBox.Text = "Gdańsk, pokój 7 – recepcja";
        dialogs.PickedObjects.Enqueue([helpdesk]);
        await dialog.ChangeJoinerCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => dialog.SamAvailability == NameAvailability.Available, "pre-Windows 2000 name checked");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(@"CONTOSO\GG_Helpdesk_Dołączanie", window.JoinerText.Text);
        Assert.True(window.ClearJoinerButton.IsVisible);
        Assert.Contains("GG_Helpdesk_Dołączanie", window.JoinSummaryText.Text, StringComparison.Ordinal);

        await dialog.CreateCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(dialog.Error);
        var dn = dialog.CreatedDn!;
        Assert.Equal(["Gdańsk, pokój 7 – recepcja"], context.Directory.Session.GetValues(dn, Attr.Location));
        var descriptor = (byte[])context.Directory.Session.Snapshot(dn)!.Attributes[Attr.NtSecurityDescriptor][0];
        Assert.True(DaclEditor.TryReadExplicit(descriptor, out var aces));
        var helpdeskSid = context.Directory.Session.GetValues(helpdesk, Attr.ObjectSid)[0];
        Assert.All(ComputerJoinRights.For(helpdeskSid), wanted => Assert.Contains(aces, a => a.SameAce(wanted)));
        context.AssertHealthy();
    });

    [Fact]
    public Task ComputerSubmenu_AndDetailsPane_FollowTheSelectedComputer() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        var sample = context.Directory.AddComputerInventorySample(context.Company);
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        var shell = main.Shell;
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(context.Company.ComputersOu));
        await UiWait.UntilAsync(() => shell.SelectedDocument?.IsFullyLoaded == true, "the computers of OU=Komputery");
        await UiTestSupport.WaitForIdleAsync();

        // The submenu of "Akcja" and of the list's context menu: every item is a palette command.
        var items = main.ComputerMenuItem.Items.OfType<MenuItem>().ToList();
        Assert.Equal(6, items.Count);
        Assert.All(items, i => Assert.NotNull(i.Command));
        Assert.Equal("Komputer", main.ComputerMenuItem.Header);
        var list = main.SelectedListView!;
        Assert.Equal(6, list.ComputerContextMenuItem.Items.OfType<MenuItem>().Count());
        Assert.False(shell.ComputerRemoteActionsCommand.CanExecute(null));
        Assert.Equal(Visibility.Collapsed, main.ComputerRemoteActionsButton.Visibility);

        var document = shell.SelectedDocument!;
        list.ListControl.UnselectAll();
        list.ListControl.SelectedItem = document.Items.Single(r => r.Name == "KRK-PC101");
        await Task.Delay(400);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(shell.IsComputerSelection);
        Assert.True(shell.ComputerRemoteActionsCommand.CanExecute(null));
        Assert.True(items[0].IsEnabled);
        Assert.Equal(Visibility.Visible, main.ComputerRemoteActionsButton.Visibility);

        // Remote Desktop with the fake launcher: no credential is handed to mstsc.
        await shell.ComputerRemoteDesktopCommand.ExecuteAsync(null);
        var launcher = Assert.IsType<FakeLocalToolLauncher>(context.Services.GetRequiredService<ILocalToolLauncher>());
        var launch = Assert.Single(launcher.Launches);
        Assert.Equal((LocalTool.RemoteDesktop, "krk-pc101.contoso.test", false), (launch.Tool, launch.Host, launch.WithWindowCredential));
        Assert.NotNull(sample.Pro24H2);
        context.AssertHealthy();
    });

    [Fact]
    public Task PropertySheet_OfAServer_ShowsTypeReleaseAndTheStatusPage() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        var sample = context.Directory.AddComputerInventorySample(context.Company);
        await context.ConnectAsync();
        var factory = context.Services.GetRequiredService<IDialogViewModelFactory>();
        var window = (PropertiesWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(factory.CreatePropertySheet(sample.FileServer));
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        var sheet = window.ViewModel;
        Assert.Null(sheet.LoadError);

        var status = Assert.IsType<ComputerStatusPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == L.Computers.StatusPage));
        sheet.SelectedPage = status;
        await UiWait.UntilAsync(() => status.Details.Count > 0, "the facts of the Stan page");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Same(status, window.PageTabs.SelectedItem);
        Assert.True(status.CanUseRemote);
        var texts = UiWait.Descendants<TextBox>(window).Where(t => t.IsVisible).Select(t => t.Text).ToList();
        Assert.Contains(texts, t => t.Contains("Windows Server 2022", StringComparison.Ordinal));
        Assert.Contains(UiWait.Descendants<Button>(window), b => b.IsVisible && Equals(b.Content, context.Services.GetRequiredService<ILocalizer>()[L.Computers.RemoteActionsCommand]));
        Assert.Contains(UiWait.Descendants<Button>(window), b => b.IsVisible && Equals(b.Content, context.Services.GetRequiredService<ILocalizer>()[L.Computers.CheckAllDomainControllers]));
        context.AssertHealthy();
    });

    [Fact]
    public Task SettingsWindow_ComputersTab_SavesTheRemoteOptionsAndAnOverride() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        var window = (SettingsWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(
            context.Services.GetRequiredService<IDialogViewModelFactory>().CreateSettings());
        UiTestSupport.ShowOffScreen(window);
        window.SettingsTabs.SelectedItem = window.ComputersTab;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        var tab = window.ViewModel.Computers;

        window.RemoteTimeoutBox.Text = "60";
        window.RefreshUserPolicyBox.IsChecked = true;
        window.ServerContainersBox.Text = "OU=Serwery,OU=Firma,{domain}";
        window.NewBuildBox.Text = "19045";
        window.NewEditionBox.SelectedItem = tab.Editions.Single(e => e.Value == EditionClass.EnterpriseEducation);
        window.NewEndOfSupportPicker.SelectedDate = new DateTime(2028, 10, 10);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.AddOverrideButton.IsEnabled);
        window.AddOverrideButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await UiTestSupport.WaitForIdleAsync();
        Assert.Single(window.OverridesList.Items);

        window.RemotePortBox.Text = "port";
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.SaveButton.IsEnabled);
        window.RemotePortBox.Text = string.Empty;
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.SaveButton.IsEnabled);
        await window.ViewModel.SaveCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();

        var stored = context.Services.GetRequiredService<ISettingsStore>().Current.Computers;
        Assert.Equal(60, stored.RemoteTimeoutSeconds);
        Assert.True(stored.RefreshUserPolicy);
        Assert.Equal(["OU=Serwery,OU=Firma,{domain}"], Assert.Single(stored.PlacementRules).Containers);
        var row = Assert.Single(stored.ReleaseOverrides);
        Assert.Equal((19045, EditionClass.EnterpriseEducation, new DateOnly(2028, 10, 10)), (row.Build, row.Edition, row.EndOfSupport));
        context.AssertHealthy();
    });
}
