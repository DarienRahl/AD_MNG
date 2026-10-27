using System.Windows;
using System.Windows.Media;
using AdMng.App.Connections;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Directory;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// Several connection windows in one process (design note docs/dev/multi-window.md 10.3): a second main window with
/// another domain, both usable and distinguishable, child windows and dialogs of the right window, closing one keeps the
/// other, the last one ends the application. Real <see cref="DialogService"/>s (no shared test double).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class MultiWindowTests
{
    private const string Fabrikam = "fabrikam.test";

    [Fact]
    public Task NewConnectionWindow_OpensASecondMainWindow_WithItsOwnIdentity() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var manager = Manager(context);
        var contoso = await OpenAsync(manager, FakeDirectoryBuilder.DefaultDomain);

        // Ctrl+Shift+N in the first window: an unowned connect window of a new connection.
        var before = ConnectWindows();
        Assert.True(contoso.Shell.NewConnectionWindowCommand.CanExecute(null));
        contoso.Shell.NewConnectionWindowCommand.Execute(null);
        var connect = await WaitForNewConnectWindowAsync(before);
        Assert.Null(connect.Owner);
        Assert.StartsWith("New connection – ", connect.Title, StringComparison.Ordinal);
        var fabrikam = await ConnectAsync(connect, Fabrikam);

        Assert.StartsWith(@"contoso.test – CONTOSO\Administrator – ", contoso.Title, StringComparison.Ordinal);
        Assert.StartsWith(@"fabrikam.test – FABRIKAM\Administrator – ", fabrikam.Title, StringComparison.Ordinal);
        Assert.NotEqual(contoso.Shell.ConnectionColor, fabrikam.Shell.ConnectionColor);
        Assert.NotEqual(StripColor(contoso), StripColor(fabrikam));
        Assert.Equal(@"fabrikam.test · FABRIKAM\Administrator", fabrikam.Shell.ConnectionLabel);
        Assert.NotEqual(ConnectionChrome.GetConnectionId(contoso), ConnectionChrome.GetConnectionId(fabrikam));
        Assert.Equal(2, manager.OpenWindows.Count);
        await UiWait.UntilAsync(() => contoso.Shell.OpenWindows.Count == 2, "the window switcher of the first window");

        // Both windows work: each lists its own domain.
        await UiWait.UntilAsync(() => contoso.Shell.SelectedDocument?.IsFullyLoaded == true && fabrikam.Shell.SelectedDocument?.IsFullyLoaded == true, "the domain tabs");
        Assert.Equal("contoso.test", contoso.Shell.Tree.DomainNode?.Name);
        Assert.Equal(Fabrikam, fabrikam.Shell.Tree.DomainNode?.Name);
        context.AssertHealthy();
    });

    [Fact]
    public Task ChildWindowsAndToasts_BelongToTheirOwnConnection() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var manager = Manager(context);
        var contoso = await OpenAsync(manager, FakeDirectoryBuilder.DefaultDomain);
        var fabrikam = await OpenAsync(manager, Fabrikam);

        fabrikam.Shell.ShowSessionLogCommand.Execute(null);
        contoso.Shell.ShowSessionLogCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        var logs = Application.Current.Windows.OfType<SessionLogWindow>().ToList();
        Assert.Equal(2, logs.Count);
        var fabrikamLog = Assert.Single(logs, w => ConnectionChrome.BelongsTo(w, ConnectionChrome.GetConnectionId(fabrikam)!.Value));
        Assert.Same(fabrikam, fabrikamLog.Owner);
        Assert.EndsWith(" – fabrikam.test", fabrikamLog.Title, StringComparison.Ordinal);
        Assert.Equal(Fabrikam, ConnectionChrome.GetIdentity(fabrikamLog)?.DomainDnsName);

        // "One per connection": the session log of A is another window, and asking again activates B's own.
        fabrikam.Shell.ShowSessionLogCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(2, Application.Current.Windows.OfType<SessionLogWindow>().Count());

        // Toasts appear only in their own window.
        Assert.NotSame(contoso.ToastHost.ItemsSource, fabrikam.ToastHost.ItemsSource);
        Services(manager, fabrikam).GetRequiredService<INotificationService>().Show(NotificationSeverity.Info, "Komunikat okna fabrikam");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Contains(((IEnumerable<ToastViewModel>)fabrikam.ToastHost.ItemsSource).Select(t => t.Message), m => m == "Komunikat okna fabrikam");
        Assert.DoesNotContain(((IEnumerable<ToastViewModel>)contoso.ToastHost.ItemsSource).Select(t => t.Message), m => m == "Komunikat okna fabrikam");
        context.AssertHealthy();
    });

    [Fact]
    public Task AConfirmationOfB_IsOwnedByB_AndNamesItsDomain() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var manager = Manager(context);
        _ = await OpenAsync(manager, FakeDirectoryBuilder.DefaultDomain);
        var fabrikam = await OpenAsync(manager, Fabrikam);
        var dialogs = Services(manager, fabrikam).GetRequiredService<IDialogService>();

        MessageDialogWindow? shown = null;
        var drive = UiWait.DriveModalAsync<MessageDialogWindow>(window =>
        {
            shown = window;
            window.ViewModel.CancelCommand.Execute(null);
            return Task.CompletedTask;
        });
        var answer = await dialogs.ConfirmAsync("Usuń", "Czy na pewno?", destructive: true);
        await drive;

        Assert.False(answer);
        Assert.NotNull(shown);
        Assert.Same(fabrikam, shown.Owner);
        Assert.Equal(@"Domain: fabrikam.test (FABRIKAM\Administrator)", shown.ViewModel.ConnectionHeader);
        Assert.Equal(fabrikam.Shell.ConnectionColor, shown.ViewModel.ConnectionColor);
        context.AssertHealthy();
    });

    [Fact]
    public Task ClosingOneWindow_KeepsTheOther_AndTheLastOneEndsTheApplication() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var manager = Manager(context);
        var contoso = await OpenAsync(manager, FakeDirectoryBuilder.DefaultDomain);
        var fabrikam = await OpenAsync(manager, Fabrikam);
        var contosoServices = Services(manager, contoso);
        var contosoSession = contosoServices.GetRequiredService<ISessionManager>().Current!;
        var contosoDialogs = contosoServices.GetRequiredService<IDialogService>();

        contoso.Close();
        await UiWait.UntilAsync(() => manager.Count == 1, "the first connection window to end");

        Assert.Empty(context.Shutdown.ExitCodes);
        Assert.True(fabrikam.IsVisible);
        Assert.True(fabrikam.Shell.IsConnected);
        Assert.Equal(Fabrikam, fabrikam.Shell.Connection?.Identity?.DomainDnsName);
        Assert.True(((FakeDomainControllerSession)contosoSession).IsDisposed);
        Assert.Single(manager.OpenWindows);

        // Leftover work of the closed window shows nothing: a confirmation answers "no" without a window.
        Assert.False(await contosoDialogs.ConfirmAsync("Usuń", "Czy na pewno?", destructive: true));
        Assert.DoesNotContain(Application.Current.Windows.OfType<MessageDialogWindow>(), w => w.IsVisible);

        fabrikam.Close();
        await UiWait.UntilAsync(() => manager.Count == 0, "the last connection window to end");
        Assert.Equal([0], context.Shutdown.ExitCodes);
        context.AssertHealthy();
    });

    [Fact]
    public Task ACancelledNewConnection_WithAnotherWindowOpen_DoesNotEndTheApplication() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var manager = Manager(context);
        var contoso = await OpenAsync(manager, FakeDirectoryBuilder.DefaultDomain);

        var before = ConnectWindows();
        manager.OpenNewConnection();
        var connect = await WaitForNewConnectWindowAsync(before);
        connect.Close();
        await UiWait.UntilAsync(() => manager.Count == 1, "the cancelled connection window to end");

        Assert.Empty(context.Shutdown.ExitCodes);
        Assert.True(contoso.IsVisible);
        context.AssertHealthy();
    });

    [Fact]
    public Task ABindCompletingAfterItsConnectWindowClosed_IsClosed_AndOpensNoWindow() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var manager = Manager(context);
        var contoso = await OpenAsync(manager, FakeDirectoryBuilder.DefaultDomain);
        var mainWindows = Application.Current.Windows.OfType<MainWindow>().Count();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Connector.Gate = gate;

        var before = ConnectWindows();
        manager.OpenNewConnection();
        var connect = await WaitForNewConnectWindowAsync(before);
        connect.ViewModel.Domain = Fabrikam;
        var connecting = connect.ViewModel.ConnectCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => connect.ViewModel.IsBusy, "the slow bind");
        connect.Close();
        gate.SetResult();
        await connecting;
        await UiWait.UntilAsync(() => manager.Count == 1, "the abandoned connection window to end");

        Assert.Equal(mainWindows, Application.Current.Windows.OfType<MainWindow>().Count());
        Assert.Empty(context.Shutdown.ExitCodes);
        Assert.True(contoso.IsVisible);
        context.Connector.Gate = null;
        context.AssertHealthy();
    });

    [Fact]
    public Task ExitAll_ClosesEveryWindow_OneAfterTheOther() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var manager = Manager(context);
        var contoso = await OpenAsync(manager, FakeDirectoryBuilder.DefaultDomain);
        var fabrikam = await OpenAsync(manager, Fabrikam);

        await manager.ExitAllAsync(contoso);
        await UiWait.UntilAsync(() => manager.Count == 0, "every connection window to end");

        Assert.False(contoso.IsVisible);
        Assert.False(fabrikam.IsVisible);
        Assert.Equal([0], context.Shutdown.ExitCodes);
        context.AssertHealthy();
    });

    [Fact]
    public Task AChildWindow_KeepsTheDomainItWasOpenedIn_AfterTheWindowReconnectedElsewhere() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var manager = Manager(context);
        var window = await OpenAsync(manager, FakeDirectoryBuilder.DefaultDomain);
        window.Shell.ShowSessionLogCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        var log = Application.Current.Windows.OfType<SessionLogWindow>().Single();

        var sessions = Services(manager, window).GetRequiredService<ISessionManager>();
        await sessions.DisconnectAsync();
        await sessions.ConnectAsync(new ConnectionOptions { Domain = Fabrikam }, null, CancellationToken.None);
        await UiWait.UntilAsync(() => window.Title.StartsWith(Fabrikam, StringComparison.Ordinal), "the main window to follow the new domain");

        Assert.EndsWith(" – contoso.test", log.Title, StringComparison.Ordinal);
        Assert.Equal("contoso.test", ConnectionChrome.GetIdentity(log)?.DomainDnsName);
        context.AssertHealthy();
    });

    [Fact]
    public Task AQuestionOverAChildWindowOfTheOldDomain_NamesThatDomain_AfterTheWindowReconnected() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var manager = Manager(context);
        var window = await OpenAsync(manager, FakeDirectoryBuilder.DefaultDomain);
        window.Shell.ShowSessionLogCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        var log = Application.Current.Windows.OfType<SessionLogWindow>().Single();
        var services = Services(manager, window);
        var sessions = services.GetRequiredService<ISessionManager>();
        await sessions.DisconnectAsync();
        await sessions.ConnectAsync(new ConnectionOptions { Domain = Fabrikam }, null, CancellationToken.None);
        await UiWait.UntilAsync(() => window.Title.StartsWith(Fabrikam, StringComparison.Ordinal), "the main window to follow the new domain");
        var dialogs = services.GetRequiredService<IDialogService>();

        async Task<MessageDialogWindow> AskAsync()
        {
            MessageDialogWindow? shown = null;
            var drive = UiWait.DriveModalAsync<MessageDialogWindow>(dialog =>
            {
                shown = dialog;
                dialog.ViewModel.CancelCommand.Execute(null);
                return Task.CompletedTask;
            });
            _ = await dialogs.ConfirmAsync("Eksport wyników", "Czy zapisać hasła do pliku?", destructive: false);
            await drive;
            return shown!;
        }

        // Asked over the contoso child window: its domain, not the main window's new one.
        log.Activate();
        await UiWait.UntilAsync(() => log.IsActive, "the contoso session log to be active");
        var overLog = await AskAsync();
        Assert.Same(log, overLog.Owner);
        Assert.StartsWith("Domain: contoso.test", overLog.ViewModel.ConnectionHeader, StringComparison.Ordinal);

        // Asked over the main window: the connection's current domain.
        window.Activate();
        await UiWait.UntilAsync(() => window.IsActive, "the main window to be active");
        var overMain = await AskAsync();
        Assert.Same(window, overMain.Owner);
        Assert.StartsWith("Domain: fabrikam.test", overMain.ViewModel.ConnectionHeader, StringComparison.Ordinal);
        context.AssertHealthy();
    });

    [Fact]
    public Task AnUnhandledError_IsShownOnTheActiveConnection_WithoutAConnectionHeader() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var manager = Manager(context);
        var contoso = await OpenAsync(manager, FakeDirectoryBuilder.DefaultDomain);
        _ = await OpenAsync(manager, Fabrikam);
        contoso.Activate();
        await UiTestSupport.WaitForIdleAsync();

        MessageDialogWindow? shown = null;
        var drive = UiWait.DriveModalAsync<MessageDialogWindow>(window =>
        {
            shown = window;
            window.ViewModel.ConfirmCommand.Execute(null);
            return Task.CompletedTask;
        });
        await manager.ShowErrorAsync("Application error", "Coś poszło nie tak.");
        await drive;

        Assert.NotNull(shown);
        Assert.Null(shown.ViewModel.ConnectionHeader);
        Assert.False(shown.ViewModel.HasConnectionHeader);
        Assert.IsType<MainWindow>(shown.Owner);
        context.AssertHealthy();
    });

    [Fact]
    public Task ADialogOfAnInactiveWindow_DoesNotTakeTheKeyboard() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await CreateContextAsync();
        var manager = Manager(context);
        var contoso = await OpenAsync(manager, FakeDirectoryBuilder.DefaultDomain);
        var fabrikam = await OpenAsync(manager, Fabrikam);
        contoso.Activate();
        await UiTestSupport.WaitForIdleAsync();
        Assert.SkipUnless(contoso.IsActive, "The test process cannot activate windows on this desktop.");

        MessageDialogWindow? shown = null;
        var drive = UiWait.DriveModalAsync<MessageDialogWindow>(window =>
        {
            shown = window;
            window.ViewModel.CancelCommand.Execute(null);
            return Task.CompletedTask;
        });
        _ = await Services(manager, fabrikam).GetRequiredService<IDialogService>().ConfirmAsync("Kontynuować?", "Czy kontynuować?", destructive: false);
        await drive;

        Assert.NotNull(shown);
        Assert.False(shown.ShowActivated);
        Assert.Contains(((IEnumerable<ToastViewModel>)contoso.ToastHost.ItemsSource).Select(t => t.Message), m => m.Contains(Fabrikam, StringComparison.Ordinal));
        context.AssertHealthy();
    });

    /// <summary>A context whose connector also reaches fabrikam.test; every connect is a non-owning session.</summary>
    private static async Task<UiTestContext> CreateContextAsync()
    {
        var context = await UiTestContext.CreateAsync();
        var fabrikam = new FakeDirectoryBuilder(Fabrikam);
        fabrikam.AddSampleCompany();
        context.Connector.AddDomain(fabrikam.Build());
        context.Connector.NonOwning = true;
        return context;
    }

    private static ConnectionWindowManager Manager(UiTestContext context) => context.Host.Services.GetRequiredService<ConnectionWindowManager>();

    private static IServiceProvider Services(ConnectionWindowManager manager, MainWindow window) =>
        manager.ServicesOf(ConnectionChrome.GetConnectionId(window)!.Value) ?? throw new InvalidOperationException("The window has no open connection.");

    private static Color StripColor(MainWindow window) => ((SolidColorBrush)window.ConnectionStrip.Background).Color;

    private static List<ConnectWindow> ConnectWindows() => [.. Application.Current.Windows.OfType<ConnectWindow>()];

    /// <summary>"Nowe połączenie w nowym oknie…" and a connect to <paramref name="domain"/> (current Windows user).</summary>
    private static async Task<MainWindow> OpenAsync(ConnectionWindowManager manager, string domain)
    {
        var before = ConnectWindows();
        manager.OpenNewConnection();
        return await ConnectAsync(await WaitForNewConnectWindowAsync(before), domain);
    }

    private static async Task<ConnectWindow> WaitForNewConnectWindowAsync(List<ConnectWindow> before)
    {
        await UiWait.UntilAsync(() => ConnectWindows().Any(w => w.IsVisible && !before.Contains(w)), "the connect window of a new connection");
        var connect = ConnectWindows().Single(w => w.IsVisible && !before.Contains(w));
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        return connect;
    }

    private static async Task<MainWindow> ConnectAsync(ConnectWindow connect, string domain)
    {
        var id = ConnectionChrome.GetConnectionId(connect);
        Assert.NotNull(id);
        connect.ViewModel.Domain = domain;
        connect.ViewModel.Authentication = AuthenticationMode.CurrentUser;
        await connect.ViewModel.ConnectCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(
            () => Application.Current.Windows.OfType<MainWindow>().Any(w => w.IsVisible && ConnectionChrome.BelongsTo(w, id.Value)),
            $"the main window of {domain}");
        var main = Application.Current.Windows.OfType<MainWindow>().Single(w => ConnectionChrome.BelongsTo(w, id.Value));
        await UiWait.UntilAsync(() => main.Shell.Connection?.Identity?.DomainDnsName == domain, "the identity of the new window");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        return main;
    }
}
