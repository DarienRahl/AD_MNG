using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>The Watchdog tab in the main window and "Historia zmian (Watchdog)" from objects.</summary>
public sealed class WatchdogShellTests
{
    [Fact]
    public async Task ToolsWatchdog_OpensOneTab_AndSelectsItAgain()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            using var shell = h.CreateShell();
            await shell.InitializeAsync();
            var list = shell.SelectedTab;
            var command = Assert.Single(shell.CommandDescriptors, c => c.Id == "tools.watchdog");
            Assert.Same(shell.ShowWatchdogCommand, command.Command);
            Assert.Equal("Ctrl+Shift+A", command.Shortcut);
            Assert.True(command.Command.CanExecute(null));

            command.Command.Execute(null);
            var watchdog = Assert.IsType<WatchdogDocumentViewModel>(shell.SelectedTab);
            await watchdog.Connecting;
            Assert.True(watchdog.IsConnected);
            Assert.True(watchdog.IsVisible);
            Assert.Null(shell.SelectedDocument);

            shell.SelectedTab = list;
            Assert.False(watchdog.IsVisible);

            shell.ShowWatchdogCommand.Execute(null);
            Assert.Same(watchdog, shell.SelectedTab);
            Assert.Single(shell.Documents.OfType<WatchdogDocumentViewModel>());
            Assert.True(watchdog.IsVisible);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task WatchdogTab_WorksWithoutAnAdSession_AndSurvivesReconnecting()
    {
        using var h = await WatchdogHarness.CreateAsync(adSession: false);
        await h.Ui.RunAsync(async () =>
        {
            using var shell = h.CreateShell();
            Assert.True(shell.ShowWatchdogCommand.CanExecute(null));

            var watchdog = shell.OpenWatchdog()!;
            await watchdog.Connecting;
            await watchdog.Events.Loading;
            Assert.True(watchdog.IsConnected);
            Assert.NotEmpty(watchdog.Events.Items);
            Assert.False(watchdog.Events.Filter.CanPick);
            Assert.Single(shell.Documents);

            await h.Ad.ReconnectAsync(h.Ad.Session);
            await shell.InitializeAsync();

            Assert.Contains(watchdog, shell.Documents);
            Assert.Single(shell.ListDocuments);
            Assert.True(watchdog.Events.Filter.CanPick);
            Assert.True(watchdog.IsConnected);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task F5_RefreshesTheWatchdogTab_CtrlW_ClosesItAndStopsPolling()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            using var shell = h.CreateShell();
            await shell.InitializeAsync();
            var watchdog = shell.OpenWatchdog()!;
            await watchdog.Connecting;
            await watchdog.Events.Loading;
            var loads = FeedLoads(h);

            await shell.RefreshCommand.ExecuteAsync(null);

            Assert.Equal(loads + 1, FeedLoads(h));

            Assert.Equal("Ctrl+W", Assert.Single(shell.CommandDescriptors, c => c.Id == "nav.closeTab").Shortcut);
            Assert.Equal("F5", Assert.Single(shell.CommandDescriptors, c => c.Id == "object.refresh").Shortcut);
            shell.CloseTabCommand.Execute(null);

            Assert.DoesNotContain(watchdog, shell.Documents);
            Assert.IsType<ObjectListViewModel>(shell.SelectedTab);
            var calls = h.Client.Calls.All.Count;
            h.Time.Advance(TimeSpan.FromMinutes(5));
            await h.Ui.IdleAsync();
            Assert.Equal(calls, h.Client.Calls.All.Count);

            // Opening it again creates a fresh tab.
            var reopened = shell.OpenWatchdog()!;
            Assert.NotSame(watchdog, reopened);
            await reopened.Connecting;
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task CtrlW_OnUnsavedWatchdogConfiguration_AsksBeforeDiscardingIt()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            using var shell = h.CreateShell();
            await shell.InitializeAsync();
            var watchdog = shell.OpenWatchdog()!;
            await watchdog.Connecting;
            await watchdog.Config.Loading;
            var question = "confirm:" + h.Ad.Localizer[L.Watchdog.ConfigDiscard];
            Assert.False(shell.HasUnsavedDocuments);

            // Unsaved edits (a typed SMTP password among them).
            watchdog.Channels.Smtp!.Password.SetNewValue("Hasło-Łódź-2026");
            watchdog.Config.Update(c => c with { Smtp = c.Smtp with { From = "Strażnik AD <straznik@contoso.com>" } });
            Assert.True(watchdog.HasUnsavedChanges);
            Assert.True(shell.HasUnsavedDocuments);

            h.Ad.Dialogs.ConfirmAnswers.Enqueue(false);
            await shell.CloseTabCommand.ExecuteAsync(null);

            Assert.Contains(watchdog, shell.Documents);
            Assert.Same(watchdog, shell.SelectedTab);
            Assert.True(watchdog.Config.IsDirty);
            Assert.Equal(1, h.Ad.Dialogs.Shown.Count(s => s == question));

            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await shell.CloseTabCommand.ExecuteAsync(watchdog);

            Assert.DoesNotContain(watchdog, shell.Documents);
            Assert.IsType<ObjectListViewModel>(shell.SelectedTab);
            Assert.Equal(2, h.Ad.Dialogs.Shown.Count(s => s == question));
            Assert.False(shell.HasUnsavedDocuments);

            // A tab without unsaved changes closes without a question.
            var reopened = shell.OpenWatchdog()!;
            await reopened.Connecting;
            await reopened.Config.Loading;
            await shell.CloseTabCommand.ExecuteAsync(reopened);
            Assert.DoesNotContain(reopened, shell.Documents);
            Assert.Equal(2, h.Ad.Dialogs.Shown.Count(s => s == question));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ClosingTheWindow_AsksForEveryTabWithUnsavedChanges()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            using var shell = h.CreateShell();
            await shell.InitializeAsync();
            var list = shell.SelectedTab;
            Assert.True(await shell.CanCloseDocumentsAsync());

            var watchdog = shell.OpenWatchdog()!;
            await watchdog.Connecting;
            await watchdog.Config.Loading;
            watchdog.Config.Update(c => c with { Smtp = c.Smtp with { From = "Strażnik AD <straznik@contoso.com>" } });
            shell.SelectedTab = list;

            // Declined: the window stays open and shows the tab the question was about.
            h.Ad.Dialogs.ConfirmAnswers.Enqueue(false);
            Assert.False(await shell.CanCloseDocumentsAsync());
            Assert.Same(watchdog, shell.SelectedTab);
            Assert.Contains(watchdog, shell.Documents);

            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            Assert.True(await shell.CanCloseDocumentsAsync());
            Assert.Equal(2, h.Ad.Dialogs.Shown.Count(s => s == "confirm:" + h.Ad.Localizer[L.Watchdog.ConfigDiscard]));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task HiddenTab_KeepsItsBadgeUpToDate_WithoutLongPolling()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            using var shell = h.CreateShell();
            await shell.InitializeAsync();
            var watchdog = shell.OpenWatchdog()!;
            await watchdog.Connecting;
            await watchdog.Events.Loading;
            Assert.Equal("5", watchdog.BadgeText);

            // In the background the tab only reads the status, every 60 s (the poller runs on its own; the clock decides).
            shell.SelectedTab = shell.ListDocuments.First();
            Assert.False(watchdog.IsVisible);
            await h.Ui.IdleAsync();
            h.Client.Status = h.Client.Status with
            {
                Last24Hours = h.Client.Status.Last24Hours with { UnacknowledgedHighOrAbove = 7 },
            };
            var statuses = h.Client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync));
            h.Time.Advance(WatchdogPoller.HiddenStatusInterval + TimeSpan.FromSeconds(1));
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), statuses + 1);
            await WatchdogTestWait.UntilAsync(h, () => watchdog.BadgeText == "7", "the badge of the hidden tab");

            // No long-poll while hidden.
            var longPolls = h.Client.EventQueries.Count(q => q.After is not null);
            statuses = h.Client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync));
            h.Time.Advance(WatchdogPoller.HiddenStatusInterval + TimeSpan.FromSeconds(1));
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), statuses + 1);
            await h.Ui.IdleAsync();
            Assert.Equal(longPolls, h.Client.EventQueries.Count(q => q.After is not null));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task History_FromTheObjectList_ShowsTheEventsOfThatObject()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            using var shell = h.CreateShell();
            await shell.InitializeAsync();
            Assert.True(shell.IsWatchdogHistoryAvailable);
            Assert.False(shell.ShowWatchdogHistoryCommand.CanExecute(null));
            await shell.NavigateAsync(ListLocation.ForContainer(h.Ad.Company.SalesOu));
            var document = shell.SelectedDocument!;
            document.SelectedItem = document.Items.Single(r => r.Name == "Żaneta Szymańska");
            Assert.True(shell.ShowWatchdogHistoryCommand.CanExecute(null));
            Assert.Same(shell.ShowWatchdogHistoryCommand, Assert.Single(shell.CommandDescriptors, c => c.Id == "object.watchdogHistory").Command);

            await shell.ShowWatchdogHistoryCommand.ExecuteAsync(null);

            var watchdog = Assert.IsType<WatchdogDocumentViewModel>(shell.SelectedTab);
            await watchdog.Connecting;
            await watchdog.Events.Loading;
            var guid = (await h.Ad.Session.GetAsync(h.Ad.Company.ZanetaSzymanska, [Attr.ObjectGuid], TestContext.Current.CancellationToken))!.ObjectGuid;
            Assert.Equal(WatchdogSubTab.Events, watchdog.SelectedSubTab);
            Assert.Equal(guid, watchdog.Events.Filter.ObjectGuid);
            Assert.Contains("Żaneta Szymańska", watchdog.Events.Filter.ObjectText, StringComparison.Ordinal);
            Assert.Contains(h.Client.EventQueries, q => q.ObjectGuid == guid && q.After is null);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task History_IsOfferedOnlyWithAServiceAddress()
    {
        using var h = await WatchdogHarness.CreateAsync(configured: false);
        await h.Ui.RunAsync(async () =>
        {
            using var shell = h.CreateShell();
            await shell.InitializeAsync();
            var changed = new List<string?>();
            shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            await shell.NavigateAsync(ListLocation.ForContainer(h.Ad.Company.SalesOu));
            shell.SelectedDocument!.SelectedItem = shell.SelectedDocument.Items.Single(r => r.Name == "Żaneta Szymańska");
            Assert.False(shell.IsWatchdogHistoryAvailable);
            Assert.False(shell.ShowWatchdogHistoryCommand.CanExecute(null));
            Assert.False(h.Launcher.Show(Guid.NewGuid(), "Żaneta Szymańska"));

            await h.Connection.SetAddressAsync(WatchdogHarness.ServiceUrl);

            Assert.True(shell.IsWatchdogHistoryAvailable);
            Assert.Contains(nameof(ShellViewModel.IsWatchdogHistoryAvailable), changed);
            Assert.True(shell.ShowWatchdogHistoryCommand.CanExecute(null));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task History_FromThePropertySheet()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            using var shell = h.CreateShell();
            await shell.InitializeAsync();
            var sheet = new PropertySheetViewModel(h.Ad.Services, h.Ad.Engines.Planner, h.Ad.Company.JanKowalski) { HistoryLauncher = h.Launcher };
            Assert.True(sheet.IsWatchdogHistoryAvailable);
            Assert.False(sheet.ShowWatchdogHistoryCommand.CanExecute(null));
            await sheet.InitializeAsync();
            Assert.True(sheet.ShowWatchdogHistoryCommand.CanExecute(null));

            sheet.ShowWatchdogHistoryCommand.Execute(null);

            var watchdog = Assert.IsType<WatchdogDocumentViewModel>(shell.SelectedTab);
            await watchdog.Connecting;
            Assert.Equal(sheet.ObjectGuid, watchdog.Events.Filter.ObjectGuid);
            Assert.Contains("Jan Kowalski", watchdog.Events.Filter.ObjectText, StringComparison.Ordinal);

            // Without a launcher (no watchdog client in the application) nothing is offered.
            var plain = new PropertySheetViewModel(h.Ad.Services, h.Ad.Engines.Planner, h.Ad.Company.JanKowalski);
            await plain.InitializeAsync();
            Assert.False(plain.IsWatchdogHistoryAvailable);
            Assert.False(plain.ShowWatchdogHistoryCommand.CanExecute(null));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task History_InAnOpenPropertySheet_FollowsTheServiceAddress()
    {
        using var h = await WatchdogHarness.CreateAsync(configured: false);
        await h.Ui.RunAsync(async () =>
        {
            using var sheet = new PropertySheetViewModel(h.Ad.Services, h.Ad.Engines.Planner, h.Ad.Company.JanKowalski) { HistoryLauncher = h.Launcher };
            await sheet.InitializeAsync();
            var changed = new List<string?>();
            sheet.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.False(sheet.IsWatchdogHistoryAvailable);
            Assert.False(sheet.ShowWatchdogHistoryCommand.CanExecute(null));

            await h.Connection.SetAddressAsync(WatchdogHarness.ServiceUrl);

            Assert.Contains(nameof(PropertySheetViewModel.IsWatchdogHistoryAvailable), changed);
            Assert.True(sheet.IsWatchdogHistoryAvailable);
            Assert.True(sheet.ShowWatchdogHistoryCommand.CanExecute(null));

            sheet.Dispose();
            changed.Clear();
            await h.Connection.ClearAddressAsync();
            Assert.Empty(changed);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task OpenInAdMng_FromTheWatchdogTab_LocatesTheObjectInAListTab()
    {
        using var h = await WatchdogHarness.CreateAsync();
        var zaneta = await h.Ad.Session.GetAsync(h.Ad.Company.ZanetaSzymanska, [Attr.ObjectGuid], TestContext.Current.CancellationToken);
        var stored = h.Client.AddEvent(SampleEvents.NewUser("Żaneta Szymańska", SampleEvents.Now) with
        {
            Object = new ObjectRef
            {
                ObjectGuid = zaneta!.ObjectGuid,
                Dn = "CN=Żaneta Szymańska,OU=Wrocław,OU=Firma,DC=contoso,DC=test",
                Name = "Żaneta Szymańska",
                Kind = WatchObjectKind.User,
            },
        });
        await h.Ui.RunAsync(async () =>
        {
            using var shell = h.CreateShell();
            await shell.InitializeAsync();
            var watchdog = shell.OpenWatchdog()!;
            await watchdog.Connecting;
            await watchdog.Events.Loading;
            watchdog.Events.SelectedItem = watchdog.Events.Items.Single(r => r.Id == stored.Id);
            await watchdog.Events.Details.Loading;

            await watchdog.Events.Details.OpenInAdMngCommand.ExecuteAsync(null);
            await WatchdogTestWait.UntilAsync(h, () => shell.SelectedDocument?.SelectedItem?.Dn == h.Ad.Company.ZanetaSzymanska, "the located object");

            Assert.Contains(watchdog, shell.Documents);
            Assert.Equal(h.Ad.Company.SalesOu, shell.SelectedDocument!.Location!.BaseDn, ignoreCase: true);
        });
        h.AssertNoUiErrors();
    }

    private static int FeedLoads(WatchdogHarness h) => h.Client.EventQueries.Count(q => q.After is null && q.Before is null);
}
