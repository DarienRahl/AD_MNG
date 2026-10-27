using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Watchdog;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests.Connections;

/// <summary>
/// One process, several connection windows (design note docs/dev/multi-window.md 3–7): the shell's new-window command and
/// window switcher, the connect dialog's profile and colour, shared settings applied in every window, the domain in
/// confirmations, the Watchdog domain check and the window's lifetime.
/// </summary>
public sealed class ConnectionWindowTests
{
    private const string Fabrikam = "fabrikam.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewConnectionWindow_IsOfferedInThePalette_AndAsksTheWindowManager()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await a.ConnectAsync();
        var shell = a.Get<ShellViewModel>();

        var command = Assert.Single(shell.CommandDescriptors, c => c.Id == "session.newWindow");
        Assert.Equal("Ctrl+Shift+N", command.Shortcut);
        Assert.True(shell.CanOpenConnectionWindows);
        command.Command.Execute(null);

        Assert.Equal(1, host.Windows.NewConnectionRequests);
    }

    [Fact]
    public async Task TheWindowSwitcher_ListsTheOtherWindows_AndActivatesOne()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");
        await b.ConnectAsync(Fabrikam, "anna.zielinska");
        var shell = a.Get<ShellViewModel>();

        host.Windows.SetWindows(a.Context, b.Context);

        var entry = Assert.Single(shell.CommandDescriptors, c => c.Id.StartsWith(ShellViewModel.ActivateWindowPrefix, StringComparison.Ordinal));
        Assert.Equal(ShellViewModel.ActivateWindowPrefix + b.Context.Id.ToString("N"), entry.Id);
        shell.CommandPalette.FilterText = "fabrikam";
        var result = Assert.Single(shell.CommandPalette.Results);
        Assert.Equal(@"Go to window: fabrikam.test · FABRIKAM\anna.zielinska", result.Title);
        Assert.Equal(2, shell.OpenWindows.Count);
        Assert.True(shell.OpenWindows.Single(w => w.Id == a.Context.Id).IsCurrent);

        entry.Command.Execute(entry.Parameter);
        Assert.Equal([b.Context.Id], host.Windows.Activated);

        // B closes: its entry goes (the palette entries hold only the id, never another window's objects).
        host.Windows.SetWindows(a.Context);
        Assert.DoesNotContain(shell.CommandDescriptors, c => c.Id.StartsWith(ShellViewModel.ActivateWindowPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void ReplaceGroup_ReplacesOnlyTheGroup_AndRefusesForeignIds()
    {
        var palette = new CommandPaletteViewModel(new ResxLocalizer(CultureInfo.GetCultureInfo("en-US")));
        var noop = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        palette.Register([new CommandDescriptor("object.refresh", L.Commands.Refresh, L.CommandCategories.View, "F5", noop)]);

        palette.ReplaceGroup("window.activate:", [new CommandDescriptor("window.activate:1", L.Connections.SwitchTo, L.CommandCategories.Session, string.Empty, noop) { TitleArguments = ["contoso.test"] }]);
        palette.ReplaceGroup("window.activate:", [new CommandDescriptor("window.activate:2", L.Connections.SwitchTo, L.CommandCategories.Session, string.Empty, noop) { TitleArguments = ["fabrikam.test"] }]);

        Assert.Equal(["object.refresh", "window.activate:2"], palette.Commands.Select(c => c.Id));
        Assert.Throws<ArgumentException>(() => palette.ReplaceGroup("window.activate:", [new CommandDescriptor("object.delete", L.Commands.Delete, L.CommandCategories.Object, string.Empty, noop)]));
    }

    [Fact]
    public async Task TheConnectDialog_GivesTheWindowItsProfileAndColour_AndSavesTheColourInTheProfile()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        var connect = a.Get<ConnectViewModel>();
        connect.Initialize();
        connect.Domain = Fabrikam;
        connect.SaveProfile = true;
        connect.ProfileName = "Fabrikam – sprzedaż";
        Assert.True(connect.CanChooseColor);
        Assert.Equal(9, connect.Colors.Count);
        Assert.Null(connect.Colors[0].Value);
        connect.SelectedColor = ConnectionColor.Teal;

        await connect.ConnectCommand.ExecuteAsync(null);

        Assert.Null(connect.ErrorText);
        Assert.Equal("Fabrikam – sprzedaż", a.Context.ProfileName);
        Assert.Equal(ConnectionColor.Teal, a.Context.Identity!.Color);
        Assert.Equal(ConnectionColorSource.Profile, a.Context.Identity.ColorSource);
        var profile = Assert.Single(a.Get<ISettingsStore>().Current.ConnectionProfiles);
        Assert.Equal(ConnectionColor.Teal, profile.Color);

        // The colour survives the file (enum as text).
        var json = JsonSettingsStore.Serialize(a.Get<ISettingsStore>().Current);
        Assert.Contains("\"color\": \"Teal\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheConnectDialog_SaysWhenTheProfileIsAlreadyOpenInAnotherWindow()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.Get<ISettingsStore>().UpdateAsync(s =>
        {
            s.ConnectionProfiles.Add(new ConnectionProfile { Name = "Contoso", Domain = "contoso.test" });
            s.LastProfileName = "Contoso";
        }, Ct);
        var first = a.Get<ConnectViewModel>();
        first.Initialize();
        Assert.Null(first.ProfileOpenElsewhereText);
        await first.ConnectCommand.ExecuteAsync(null);

        var second = b.Get<ConnectViewModel>();
        second.Initialize();

        // Information only: the same domain with another account, or the same account twice, is supported.
        Assert.Equal("Contoso", second.SelectedProfile?.Name);
        Assert.Equal($"This profile is already open in the window {a.Context.Label}. Connecting again opens a second window of the same connection.", second.ProfileOpenElsewhereText);
        Assert.Equal(a.Context.Identity!.Color, second.ProfileOpenElsewhereColor);
        Assert.True(second.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChangeDomainController_RemembersTheDcInTheProfileOfItsOwnWindow()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        var settings = a.Get<ISettingsStore>();
        await settings.UpdateAsync(s =>
        {
            s.ConnectionProfiles.Add(new ConnectionProfile { Name = "Biuro Kraków", Domain = "contoso.test", Server = "dc01.contoso.test" });
            s.ConnectionProfiles.Add(new ConnectionProfile { Name = "Fabrikam", Domain = Fabrikam });
        }, Ct);
        a.Context.ProfileName = "Biuro Kraków";
        await a.ConnectAsync();
        b.Context.ProfileName = "Fabrikam";
        await b.ConnectAsync(Fabrikam);

        // The global "last profile" is that of the window connected last (B): A must still use its own.
        await settings.UpdateAsync(s => s.LastProfileName = "Fabrikam", Ct);
        using (var dialog = a.Get<IDialogViewModelFactory>().CreateChangeDomainController())
        {
            await dialog.InitializeAsync();
            Assert.True(dialog.CanUpdateProfile);
            Assert.Equal("Remember the choice in the connection profile 'Biuro Kraków'", dialog.UpdateProfileText);
            dialog.ServerText = "dc02.contoso.test";
            dialog.UpdateProfile = true;
            await dialog.ChangeCommand.ExecuteAsync(null);
        }

        Assert.Equal("dc02.contoso.test", settings.Current.ConnectionProfiles[0].Server);
        Assert.Null(settings.Current.ConnectionProfiles[1].Server);

        // A window whose profile names another domain than its session is not offered to update it.
        a.Context.ProfileName = "Fabrikam";
        using var mismatch = a.Get<IDialogViewModelFactory>().CreateChangeDomainController();
        await mismatch.InitializeAsync();
        Assert.False(mismatch.CanUpdateProfile);
    }

    [Fact]
    public async Task AdvancedFeatures_ToggledInA_ReloadsEveryWindowOnce_AndAColumnWidthReloadsNone()
    {
        var contoso = SampleDomain.Create(FakeDirectoryBuilder.DefaultDomain);
        var fabrikam = SampleDomain.Create(Fabrikam);
        await using var host = new ConnectionScopeHost(domains: [contoso.Session, fabrikam.Session]);
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync();
        await b.ConnectAsync(Fabrikam);
        var shellA = a.Get<ShellViewModel>();
        var shellB = b.Get<ShellViewModel>();
        await shellA.InitializeAsync();
        await shellB.InitializeAsync();
        await WaitQuietAsync(contoso.Session, fabrikam.Session);
        contoso.Session.ClearOperations();
        fabrikam.Session.ClearOperations();

        await shellA.ToggleAdvancedFeaturesCommand.ExecuteAsync(null);
        await WaitQuietAsync(contoso.Session, fabrikam.Session);

        // Both windows show the same structure, so one reload each means the same number of reads in both domains.
        Assert.True(shellB.ShowAdvancedFeatures);
        var readsA = Searches(contoso.Session);
        var readsB = Searches(fabrikam.Session);
        Assert.True(readsB > 0, "The other window did not reload.");
        Assert.Equal(readsB, readsA);

        // Every settings update raises Changed (a column width, a recent container): no reload without a real change.
        contoso.Session.ClearOperations();
        fabrikam.Session.ClearOperations();
        await a.Get<ISettingsStore>().UpdateAsync(s => s.ColumnLayouts["test"] = [new ColumnLayoutEntry("name", 120)], Ct);
        await WaitQuietAsync(contoso.Session, fabrikam.Session);
        Assert.Equal(0, Searches(contoso.Session));
        Assert.Equal(0, Searches(fabrikam.Session));
    }

    [Fact]
    public async Task RecentAndPinnedContainers_AreThoseOfTheWindowsDomain()
    {
        var contoso = SampleDomain.Create(FakeDirectoryBuilder.DefaultDomain);
        var fabrikam = SampleDomain.Create(Fabrikam);
        await using var host = new ConnectionScopeHost(domains: [contoso.Session, fabrikam.Session]);
        await using var a = host.CreateConnection();
        await a.ConnectAsync();
        var settings = a.Get<ISettingsStore>();
        await settings.UpdateAsync(s =>
        {
            for (var i = 0; i < 12; i++)
            {
                s.AddRecentContainer($"OU=Dział {i},{contoso.Session.Info.DefaultNamingContext}");
                s.AddRecentContainer($"OU=Dział {i},{fabrikam.Session.Info.DefaultNamingContext}");
            }

            s.PinnedContainers.Add(new PinnedContainer(contoso.Company.ItOu, "IT"));
            s.PinnedContainers.Add(new PinnedContainer(fabrikam.Company.SalesOu, "Sprzedaż"));
        }, Ct);
        var shell = a.Get<ShellViewModel>();
        await shell.InitializeAsync();

        Assert.Equal(ShellViewModel.RecentContainersShown, shell.RecentContainers.Count);
        Assert.All(shell.RecentContainers, path => Assert.StartsWith("contoso.test", path, StringComparison.Ordinal));
        var pins = shell.Tree.ViewsNode.Children.OfType<Presentation.Navigation.ContainerNodeViewModel>().ToList();
        Assert.Equal([contoso.Company.ItOu], pins.Select(p => p.Dn));

        // The other domain's entries stay in the settings (30 recent containers are kept for all windows together).
        Assert.Equal(2, settings.Current.PinnedContainers.Count);
        Assert.Equal(12, settings.Current.RecentContainers.Count(dn => dn.EndsWith(fabrikam.Session.Info.DefaultNamingContext, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task AChildWindow_NamesTheDomainOfItsOwnSession_AfterTheWindowReconnectedElsewhere()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await a.ConnectAsync();
        using var probe = new ConfirmationProbe(a.Get<DialogServices>());

        await a.Sessions.DisconnectAsync();
        await a.ConnectAsync(Fabrikam);

        Assert.Equal(Fabrikam, a.Context.Identity!.DomainDnsName);
        Assert.Equal("contoso.test", probe.Domain);
        Assert.True(probe.IsSessionEnded);
    }

    [Fact]
    public async Task TheServiceDomain_LearnedInOneWindow_HidesObjectHistoryInAWindowOfAnotherDomain()
    {
        await using var host = CreateHost();
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync();
        await b.ConnectAsync(Fabrikam);
        var service = a.Get<WatchdogConnectionService>();
        var inA = a.Get<WatchdogObjectHistoryLauncher>();
        var inB = b.Get<WatchdogObjectHistoryLauncher>();
        var changedInB = 0;
        inB.AvailabilityChanged += (_, _) => changedInB++;

        // Neither window has opened its Watchdog tab: the launchers ask the service once for its domain.
        await service.SetAddressAsync("https://admng-wd.contoso.test:8443");
        await service.EnsureServiceDomainAsync();

        Assert.Equal("contoso.test", service.ServiceDomain);
        Assert.True(inA.IsAvailable);
        Assert.False(inB.IsAvailable);
        Assert.True(inB.IsOtherDomain);
        Assert.False(inB.CanShow(Guid.NewGuid()));
        Assert.True(changedInB > 0);

        // Another service address: the domain is unknown again (windows behave as before until it is read).
        await service.ClearAddressAsync();
        Assert.Null(service.ServiceDomain);
        Assert.False(inB.IsOtherDomain);
    }

    [Fact]
    public async Task TheCertificateQuestion_IsAskedOnceForTheWholeApplication()
    {
        await using var host = CreateHost();
        var service = host.Provider.GetRequiredService<WatchdogConnectionService>();
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;

        var first = service.ConfirmCertificateOnceAsync(() =>
        {
            asked++;
            return answer.Task;
        });
        var second = service.ConfirmCertificateOnceAsync(() =>
        {
            asked++;
            return Task.FromResult(false);
        });
        answer.SetResult(true);

        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, asked);

        // Answered: the next untrusted certificate is a new question.
        Assert.False(await service.ConfirmCertificateOnceAsync(() =>
        {
            asked++;
            return Task.FromResult(false);
        }));
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task ClosingTheWindowDuringPlanning_CancelsThePlan_AndWritesNothing()
    {
        var planner = new RecordingChangePlanner();
        var planning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        planner.BeforePlan = async token =>
        {
            planning.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var contoso = SampleDomain.Create(FakeDirectoryBuilder.DefaultDomain);
        await using var host = new ConnectionScopeHost(services => services.AddSingleton<IChangePlanner>(planner), [contoso.Session]);
        await using var a = host.CreateConnection();
        await a.ConnectAsync();
        contoso.Session.ClearOperations();

        var apply = a.Get<IChangeCoordinator>().ApplyAsync(
            new ChangeRequest([contoso.Company.LukaszWisniewski], [new SetAccountEnabledOperation(false)], "Wyłącz konto"), Ct);
        await planning.Task.WaitAsync(Ct);
        a.Context.EndLifetime();

        Assert.Equal(ChangeOutcome.Cancelled, await apply);
        Assert.Empty(contoso.Session.Writes);
        Assert.Empty(a.Dialogs.Errors);
        Assert.Empty(a.Get<ActivityCenterViewModel>().Activities);
    }

    private static ConnectionScopeHost CreateHost() =>
        new(domains: [new FakeDirectoryBuilder().Build(), new FakeDirectoryBuilder(Fabrikam).Build()]);

    private static int Searches(FakeDirectorySession directory) =>
        directory.Operations.Count(o => o.Kind == FakeOperationKind.Search);

    /// <summary>Waits until no new operation reached the fakes for a moment (fire-and-forget loads of the shells).</summary>
    private static async Task WaitQuietAsync(params FakeDirectorySession[] directories)
    {
        var last = -1;
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(30, Ct);
            var now = directories.Sum(d => d.Operations.Count);
            if (now == last)
            {
                return;
            }

            last = now;
        }
    }

    /// <summary>A child window: it names the domain of the session it was opened in.</summary>
    private sealed class ConfirmationProbe(DialogServices services) : WindowViewModelBase(services)
    {
        public override string WindowTitle => "Sonda";

        public string Domain => ConfirmationDomain;
    }
}
