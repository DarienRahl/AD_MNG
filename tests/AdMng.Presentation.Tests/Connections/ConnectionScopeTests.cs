using System.Runtime.CompilerServices;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Watchdog;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests.Connections;

/// <summary>
/// Two connection windows in one process - contoso.test and fabrikam.test, each its own DI scope - never touch each
/// other: disconnects, writes, change notifications, background work, logs and Watchdog requests stay in their window
/// (design note docs/dev/multi-window.md 2, 3.7 and 10.2).
/// </summary>
public sealed class ConnectionScopeTests
{
    private const string Fabrikam = "fabrikam.test";

    [Fact]
    public async Task DisconnectInA_LeavesBConnected()
    {
        await using var host = CreateHost(out _, out _);
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");
        var inB = await b.ConnectAsync(Fabrikam, "anna.zielinska");

        await a.Sessions.DisconnectAsync();

        Assert.Null(a.Sessions.Current);
        Assert.Same(inB, b.Sessions.Current);
        Assert.NotNull(await inB.GetAsync(inB.Info.DefaultNamingContext, ["name"], TestContext.Current.CancellationToken));
        Assert.Null(a.Context.Identity);
        Assert.Equal(Fabrikam, b.Context.Identity?.DomainDnsName);
        Assert.Equal([b.Context.Id], host.Registry.Connections.Select(c => c.Id));
    }

    [Fact]
    public async Task AWriteThroughA_LandsOnlyInA_AndOnlyAHearsOfIt()
    {
        await using var host = CreateHost(out var contoso, out var fabrikam);
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync();
        await b.ConnectAsync(Fabrikam);
        var heardInA = 0;
        var heardInB = 0;
        a.Get<IDirectoryChangeNotifier>().Changed += (_, _) => heardInA++;
        b.Get<IDirectoryChangeNotifier>().Changed += (_, _) => heardInB++;
        contoso.Session.ClearOperations();
        fabrikam.Session.ClearOperations();

        var outcome = await a.Get<IChangeCoordinator>().ApplyAsync(
            new ChangeRequest([contoso.Company.LukaszWisniewski], [new SetAccountEnabledOperation(false)], "Wyłącz konto Łukasza"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ChangeOutcome.Executed, outcome);
        Assert.NotEmpty(contoso.Session.Writes);
        Assert.Empty(fabrikam.Session.Writes);
        Assert.True(await IsDisabledAsync(contoso.Session, contoso.Company.LukaszWisniewski));
        Assert.False(await IsDisabledAsync(fabrikam.Session, fabrikam.Company.LukaszWisniewski));
        Assert.Equal(1, heardInA);
        Assert.Equal(0, heardInB);

        // The session log and the PowerShell history are those of the window.
        Assert.NotEmpty(a.Get<IAuditLog>().Entries);
        Assert.Empty(b.Get<IAuditLog>().Entries);
        Assert.NotEmpty(a.Get<PowerShellHistory>().Entries);
        Assert.Empty(b.Get<PowerShellHistory>().Entries);
        Assert.Empty(b.Get<ActivityCenterViewModel>().Activities);
    }

    [Fact]
    public async Task CancellingTheRunningWorkOfA_LeavesTheWorkOfBRunning()
    {
        await using var host = CreateHost(out _, out _);
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync();
        await b.ConnectAsync(Fabrikam);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runA = a.Get<ActivityCenterViewModel>().Start("Zadanie w contoso", 1, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return EmptyResult();
        });
        var runB = b.Get<ActivityCenterViewModel>().Start("Zadanie w fabrikam", 1, async (_, _) =>
        {
            await releaseB.Task;
            return EmptyResult();
        });

        await a.Get<ActivityCenterViewModel>().CancelRunningAsync();

        Assert.False(runA.Activity.IsRunning);
        Assert.True(runB.Activity.IsRunning);
        Assert.True(b.Get<ActivityCenterViewModel>().HasRunning);
        releaseB.SetResult();
        await runB.Execution;
        Assert.False(runB.Activity.IsRunning);
    }

    [Fact]
    public async Task AWatchdogHistoryRequestInA_NeverReachesB()
    {
        await using var host = CreateHost(out _, out _);
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync();
        await b.ConnectAsync(Fabrikam);
        await a.Get<WatchdogConnectionService>().SetAddressAsync("https://admng-wd.contoso.test:8443");
        var inA = new List<WatchdogHistoryRequest>();
        var inB = new List<WatchdogHistoryRequest>();
        a.Get<WatchdogObjectHistoryLauncher>().Requested += (_, r) => inA.Add(r);
        b.Get<WatchdogObjectHistoryLauncher>().Requested += (_, r) => inB.Add(r);

        Assert.True(a.Get<WatchdogObjectHistoryLauncher>().Show(Guid.NewGuid(), "Łukasz Wiśniewski"));

        Assert.Single(inA);
        Assert.Empty(inB);
    }

    [Fact]
    public async Task TheRegistry_ListsBothWindows_AndFollowsConnectSwitchAndClose()
    {
        await using var host = CreateHost(out _, out _);
        var changes = 0;
        host.Registry.ConnectionsChanged += (_, _) => changes++;
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        _ = a.Context;
        _ = b.Context;
        var registered = changes;

        await a.ConnectAsync(user: "jan.kowalski");
        Assert.True(changes > registered);
        var connected = changes;
        await b.ConnectAsync(Fabrikam, "anna.zielinska");

        Assert.Equal([a.Context.Id, b.Context.Id], host.Registry.Connections.Select(c => c.Id));
        Assert.Equal(@"contoso.test · CONTOSO\jan.kowalski", a.Context.Label);
        Assert.Equal(@"fabrikam.test · FABRIKAM\anna.zielinska", b.Context.Label);
        Assert.True(changes > connected);

        var beforeSwitch = changes;
        var colour = a.Context.Identity!.Color;
        await a.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, TestContext.Current.CancellationToken);
        Assert.True(changes > beforeSwitch);
        Assert.Equal("dc02.contoso.test", a.Context.Identity!.Server);
        Assert.Equal(colour, a.Context.Identity.Color);

        var beforeClose = changes;
        await b.DisposeAsync();
        Assert.True(changes > beforeClose);
        Assert.Equal([a.Context.Id], host.Registry.All.Select(c => c.Id));
    }

    [Fact]
    public async Task ClosingAWindow_ClosesOnlyItsOwnSession()
    {
        await using var host = CreateHost(out _, out _);
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        var inA = (FakeDomainControllerSession)await a.ConnectAsync();
        var inB = (FakeDomainControllerSession)await b.ConnectAsync(Fabrikam);
        var closed = 0;
        b.Context.Closed += (_, _) => closed++;
        var bContext = b.Context;

        await b.DisposeAsync();

        Assert.True(inB.IsDisposed);
        Assert.False(inA.IsDisposed);
        Assert.False(inA.Directory.IsDisposed);
        Assert.Same(inA, a.Sessions.Current);
        Assert.NotNull(await inA.GetAsync(inA.Info.DefaultNamingContext, ["name"], TestContext.Current.CancellationToken));
        Assert.Equal(1, closed);
        Assert.True(bContext.IsClosed);
        Assert.True(bContext.Lifetime.IsCancellationRequested);
        Assert.DoesNotContain(host.Registry.All, c => c.Id == bContext.Id);
    }

    [Fact]
    public async Task ThePowerShellHistoryOfAWindowThatReconnected_NamesTheDomainOfEachEntry()
    {
        await using var host = CreateHost(out var contoso, out var fabrikam);
        await using var a = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");
        await a.Get<IChangeCoordinator>().ApplyAsync(
            new ChangeRequest([contoso.Company.LukaszWisniewski], [new SetAttributeOperation(Attr.Title, "Kierownik zespołu")], "Stanowisko w contoso"),
            TestContext.Current.CancellationToken);

        await a.ConnectAsync(Fabrikam, "anna.zielinska");
        await a.Get<IChangeCoordinator>().ApplyAsync(
            new ChangeRequest([fabrikam.Company.LukaszWisniewski], [new SetAttributeOperation(Attr.Title, "Główny księgowy")], "Stanowisko we fabrikam"),
            TestContext.Current.CancellationToken);
        using var history = a.Get<Presentation.Dialogs.IDialogViewModelFactory>().CreatePowerShellHistory();

        Assert.Equal(
            [("Stanowisko we fabrikam", Fabrikam), ("Stanowisko w contoso", "contoso.test")],
            history.Items.Select(i => (i.Title, i.Domain)));
    }

    [Fact]
    public async Task AWindowThatStartsClosing_IsGoneForOtherWindows_BeforeItsSessionCloses()
    {
        await using var host = CreateHost(out _, out _);
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");
        var inB = (FakeDomainControllerSession)await b.ConnectAsync(Fabrikam, "anna.zielinska");
        var pinned = b.Context.Pin();
        Assert.NotNull(pinned);
        var closed = 0;
        b.Context.Closed += (_, _) => closed++;

        // The first step of the close pipeline; its activities and its session close later (up to seconds).
        b.Context.EndLifetime();

        Assert.True(b.Context.IsClosing);
        Assert.Equal(1, closed);
        Assert.Null(host.Registry.Find(b.Context.Id));
        Assert.Equal([a.Context.Id], host.Registry.Connections.Select(c => c.Id));
        Assert.Null(b.Context.Pin());
        Assert.True(pinned.IsEnded);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pinned.Reader.GetAsync(inB.Info.DefaultNamingContext, ["name"], TestContext.Current.CancellationToken));
        Assert.Same(inB, b.Sessions.Current);
        Assert.False(inB.IsDisposed);

        await b.DisposeAsync();
        Assert.Equal(1, closed);
        Assert.True(inB.IsDisposed);
    }

    [Fact]
    public async Task AClosedConnectionScope_IsCollected()
    {
        await using var host = CreateHost(out _, out _);
        await using var other = host.CreateConnection();
        await other.ConnectAsync(Fabrikam);

        var references = await OpenAndCloseWindowAsync(host);

        // A pool thread may still hold the last work item it ran (a bind, a close) for a moment: collect a few times, with a
        // short pause in between, before deciding.
        for (var attempt = 0; attempt < 20 && references.Any(r => r.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        // A scoped service that stays subscribed to an application-wide event keeps a closed window alive.
        Assert.All(references, r => Assert.False(r.IsAlive, $"{r.Target?.GetType().Name} of a closed window is still referenced."));
    }

    /// <summary>Everything of the window lives only in this frame (never inlined, so the JIT keeps no locals alive).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> OpenAndCloseWindowAsync(ConnectionScopeHost host)
    {
        // Not tracked by the host (its list would keep the scope reachable).
        var window = new TestConnection(((IServiceProvider)host.Provider).CreateAsyncScope());
        await window.ConnectAsync(user: "jan.kowalski");
        var shell = window.Get<ShellViewModel>();
        await shell.InitializeAsync();

        // The U4 tool windows of the connection: the account review with its "Uprawnienia" tab and the saved filters. They
        // subscribe to the application-wide settings, saved views and saved filters; their windows dispose them on close.
        var factory = window.Get<IDialogViewModelFactory>();
        var review = factory.CreateAccountReview();
        await review.InitializeAsync();
        var savedFilters = factory.CreateSavedFilters();
        await savedFilters.InitializeAsync();
        WeakReference[] references =
        [
            new(shell),
            new(window.Get<ActivityCenterViewModel>()),
            new(window.Get<WatchdogObjectHistoryLauncher>()),
            new(window.Context),
            new(window.Sessions),
            new(factory),
            new(review),
            new(review.Privileges),
            new(savedFilters),
        ];
        review.Dispose();
        savedFilters.Dispose();
        await window.DisposeAsync();
        return references;
    }

    private static ConnectionScopeHost CreateHost(out SampleDomain contoso, out SampleDomain fabrikam)
    {
        contoso = SampleDomain.Create(FakeDirectoryBuilder.DefaultDomain);
        fabrikam = SampleDomain.Create(Fabrikam);
        return new ConnectionScopeHost(domains: [contoso.Session, fabrikam.Session]);
    }

    private static async Task<bool> IsDisabledAsync(FakeDirectorySession directory, string dn)
    {
        var entry = await directory.GetAsync(dn, [Attr.UserAccountControl], TestContext.Current.CancellationToken);
        var flags = long.Parse(entry!.GetString(Attr.UserAccountControl)!, System.Globalization.CultureInfo.InvariantCulture);
        return (flags & 0x2) != 0;
    }

    private static BatchResult EmptyResult() =>
        new(new ChangePlan { Title = "-", Objects = [] }, [], "dc01.contoso.test", @"CONTOSO\Administrator", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false);
}

/// <summary>A fake domain with the sample company (contoso.test, or fabrikam.test with the same people).</summary>
internal sealed record SampleDomain(FakeDirectorySession Session, SampleCompany Company)
{
    public static SampleDomain Create(string domain)
    {
        var builder = new FakeDirectoryBuilder(domain);
        var company = builder.AddSampleCompany();
        return new SampleDomain(builder.Build(), company);
    }
}
