using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Session;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Session;

/// <summary>
/// <see cref="ISessionManager.SwitchDomainControllerAsync"/>: one DC per session - a switch opens a NEW session on the
/// chosen DC (options and credential of the current one), verifies it and only then replaces the current session.
/// </summary>
public class DomainControllerSwitchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Switch_ReplacesTheCurrentSession_AndClosesThePreviousOne()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        var previous = Assert.IsType<FakeDomainControllerSession>(harness.Sessions.Current);
        var opened = new List<(IDirectorySession Current, string Server)>();
        var inner = harness.ConnectToServer;
        harness.ConnectToServer = (current, server, ct) =>
        {
            opened.Add((current, server));
            return inner(current, server, ct);
        };
        var changes = 0;
        harness.Sessions.SessionChanged += (_, _) => changes++;

        var result = await harness.Sessions.SwitchDomainControllerAsync(" dc02.contoso.test ", null, Ct);

        // The connector got the current session (its options and credential) and the trimmed name.
        Assert.Equal([(previous, "dc02.contoso.test")], opened);
        var current = Assert.IsType<FakeDomainControllerSession>(harness.Sessions.Current);
        Assert.Same(current, result.Session);
        Assert.Equal("dc02.contoso.test", current.Info.Server);
        Assert.Equal("dc01.contoso.test", result.PreviousServer);
        Assert.Equal([current], harness.Sessions.Sessions);
        Assert.True(previous.IsDisposed);
        Assert.False(current.IsDisposed);
        Assert.Equal(1, changes);

        // The new session works (the formatter and the policy belong to it).
        Assert.NotNull(await current.GetAsync(harness.Company.JanKowalski, [Attr.Name], Ct));
        Assert.NotNull(harness.Sessions.Formatter);
        Assert.Same(current, harness.Sessions.RequireSession());
    }

    [Fact]
    public async Task Switch_ToADcOfAnotherDomain_KeepsTheCurrentSession()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        var previous = harness.Sessions.Current!;
        FakeDomainControllerSession? opened = null;
        harness.ConnectToServer = (_, server, _) =>
        {
            opened = new FakeDomainControllerSession(harness.Session, server, i => i with
            {
                DefaultNamingContext = "DC=oddział,DC=contoso,DC=test",
                DomainDnsName = "oddział.contoso.test",
            });
            return Task.FromResult<IDirectorySession>(opened);
        };

        var error = await Assert.ThrowsAsync<DomainControllerMismatchException>(() =>
            harness.Sessions.SwitchDomainControllerAsync("dc01.oddział.contoso.test", null, Ct));

        Assert.Equal("dc01.oddział.contoso.test", error.Server);
        Assert.Equal("oddział.contoso.test", error.ServerDomain);
        Assert.Equal("contoso.test", error.ExpectedDomain);
        Assert.Same(previous, harness.Sessions.Current);
        Assert.True(opened!.IsDisposed);
        Assert.False(((FakeDomainControllerSession)previous).IsDisposed);
    }

    [Fact]
    public async Task Switch_ToAnAliasOfTheCurrentDc_ChangesNothing()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        var previous = harness.Sessions.Current!;
        FakeDomainControllerSession? opened = null;
        harness.ConnectToServer = (_, _, _) => Task.FromResult<IDirectorySession>(opened = new FakeDomainControllerSession(harness.Session, "DC01.contoso.test"));
        var changes = 0;
        harness.Sessions.SessionChanged += (_, _) => changes++;

        var error = await Assert.ThrowsAsync<SameDomainControllerException>(() =>
            harness.Sessions.SwitchDomainControllerAsync("10.0.0.11", null, Ct));

        Assert.Equal("dc01.contoso.test", error.Server);
        Assert.Same(previous, harness.Sessions.Current);
        Assert.True(opened!.IsDisposed);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Switch_UnreachableDc_KeepsTheCurrentSessionOpen()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        var previous = (FakeDomainControllerSession)harness.Sessions.Current!;
        harness.ConnectToServer = (_, _, _) => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "The server is not operational.");

        var error = await Assert.ThrowsAsync<DirectoryException>(() => harness.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, Ct));

        Assert.Equal(DirectoryErrorKind.ServerUnavailable, error.Kind);
        Assert.Same(previous, harness.Sessions.Current);
        Assert.False(previous.IsDisposed);
        Assert.NotNull(await previous.GetAsync(harness.Company.JanKowalski, [Attr.Name], Ct));
    }

    [Fact]
    public async Task Switch_RunsTheHookBeforeReplacing_AndAFailingHookKeepsTheCurrentSession()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        var previous = harness.Sessions.Current!;
        IDirectorySession? currentDuringHook = null;

        await harness.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", () =>
        {
            currentDuringHook = harness.Sessions.Current;
            return Task.CompletedTask;
        }, Ct);

        Assert.Same(previous, currentDuringHook);
        var second = harness.Sessions.Current!;
        Assert.Equal("dc02.contoso.test", second.Info.Server);

        FakeDomainControllerSession? opened = null;
        harness.ConnectToServer = (_, server, _) => Task.FromResult<IDirectorySession>(opened = new FakeDomainControllerSession(harness.Session, server));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Sessions.SwitchDomainControllerAsync("dc03.contoso.test", () => throw new InvalidOperationException("Operator declined."), Ct));

        Assert.Same(second, harness.Sessions.Current);
        Assert.True(opened!.IsDisposed);
    }

    [Fact]
    public async Task Switch_CancelledAfterTheDcAnswered_ClosesTheNewSession()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        var previous = harness.Sessions.Current!;
        using var cancellation = new CancellationTokenSource();
        FakeDomainControllerSession? opened = null;
        harness.ConnectToServer = async (_, server, _) =>
        {
            await Task.Yield();
            opened = new FakeDomainControllerSession(harness.Session, server);
            await cancellation.CancelAsync();
            return opened;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, cancellation.Token));

        Assert.Same(previous, harness.Sessions.Current);
        Assert.True(opened!.IsDisposed);
    }

    [Fact]
    public async Task Switch_CancelledWhileTheHookRuns_ReplacesNothing()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        var previous = harness.Sessions.Current!;
        using var cancellation = new CancellationTokenSource();
        FakeDomainControllerSession? opened = null;
        harness.ConnectToServer = (_, server, _) => Task.FromResult<IDirectorySession>(opened = new FakeDomainControllerSession(harness.Session, server));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", () => cancellation.CancelAsync(), cancellation.Token));

        Assert.Same(previous, harness.Sessions.Current);
        Assert.True(opened!.IsDisposed);
    }

    [Fact]
    public async Task Switch_WhileTheSessionWasClosedMeanwhile_ReplacesNothing()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        FakeDomainControllerSession? opened = null;
        harness.ConnectToServer = async (_, server, _) =>
        {
            await harness.Sessions.DisconnectAsync();
            return opened = new FakeDomainControllerSession(harness.Session, server);
        };

        await Assert.ThrowsAsync<SessionEndedException>(() => harness.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, Ct));

        Assert.Null(harness.Sessions.Current);
        Assert.True(opened!.IsDisposed);
    }

    [Fact]
    public async Task Switch_WhileDisconnected_IsRefused()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        await harness.Sessions.DisconnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, Ct));
    }

    [Fact]
    public void IsSwitch_MeansTheSameDomainAndIdentity_OnAnotherDc()
    {
        var previous = new FakeDirectorySession(FakeDirectorySession.CreateInfo(server: "dc01.contoso.test"));

        Assert.True(DomainControllerSwitch.IsSwitch(previous, new FakeDirectorySession(FakeDirectorySession.CreateInfo(server: "dc02.contoso.test"))));
        Assert.False(DomainControllerSwitch.IsSwitch(previous, new FakeDirectorySession(FakeDirectorySession.CreateInfo(server: "DC01.contoso.test"))));
        Assert.False(DomainControllerSwitch.IsSwitch(
            previous,
            new FakeDirectorySession(FakeDirectorySession.CreateInfo(server: "dc02.contoso.test") with { BoundIdentity = @"CONTOSO\łukasz.wiśniewski" })));
        Assert.False(DomainControllerSwitch.IsSwitch(previous, new FakeDirectorySession(FakeDirectorySession.CreateInfo("fabrikam.test", "dc02.fabrikam.test"))));
        Assert.False(DomainControllerSwitch.IsSwitch(null, previous));
    }
}
