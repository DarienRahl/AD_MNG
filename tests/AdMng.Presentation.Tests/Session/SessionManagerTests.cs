using System.Net;
using AdMng.Core.Directory;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Session;

/// <summary>
/// The session manager of one connection window: a bind or a domain controller switch that completes after its window
/// closed (or after it was cancelled) never becomes a session, and a disconnect never falls back to an older session.
/// </summary>
public sealed class SessionManagerTests
{
    private static readonly ConnectionOptions Contoso = new() { Domain = FakeDirectoryBuilder.DefaultDomain };

    [Fact]
    public async Task ConnectCompletingAfterDispose_ClosesTheSession()
    {
        var late = new FakeDirectoryBuilder().Build();
        var gate = NewGate();
        var connector = new GatedConnector(late) { Gate = gate };
        var manager = CreateManager(connector);
        var changes = 0;
        manager.SessionChanged += (_, _) => changes++;

        var connect = manager.ConnectAsync(Contoso, null, CancellationToken.None);
        await connector.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await manager.DisposeAsync();
        gate.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => connect);
        Assert.True(late.IsDisposed);
        Assert.Null(manager.Current);
        Assert.Empty(manager.Sessions);
        Assert.Equal(0, changes);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.ConnectAsync(Contoso, null, CancellationToken.None));
    }

    [Fact]
    public async Task ConnectCompletingAfterCancellation_ClosesTheSession()
    {
        var late = new FakeDirectoryBuilder().Build();
        var gate = NewGate();
        var connector = new GatedConnector(late) { Gate = gate };
        var manager = CreateManager(connector);
        var changes = 0;
        manager.SessionChanged += (_, _) => changes++;
        using var cancellation = new CancellationTokenSource();

        var connect = manager.ConnectAsync(Contoso, null, cancellation.Token);
        await connector.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        gate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.True(late.IsDisposed);
        Assert.Null(manager.Current);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task DomainControllerSwitchCompletingAfterDispose_ClosesTheNewSession()
    {
        var directory = new FakeDirectoryBuilder().Build();
        var first = new FakeDomainControllerSession(directory, "dc01.contoso.test");
        var switched = new FakeDomainControllerSession(directory, "dc02.contoso.test");
        var connector = new GatedConnector(first) { ServerSession = switched };
        var manager = CreateManager(connector);
        await manager.ConnectAsync(Contoso, null, TestContext.Current.CancellationToken);
        var changes = 0;
        manager.SessionChanged += (_, _) => changes++;
        var gate = NewGate();
        connector.Gate = gate;

        var change = manager.SwitchDomainControllerAsync("dc02.contoso.test", null, CancellationToken.None);
        await connector.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await manager.DisposeAsync();
        gate.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => change);
        Assert.True(switched.IsDisposed);
        Assert.True(first.IsDisposed);
        Assert.False(directory.IsDisposed);
        Assert.Null(manager.Current);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task DisconnectAfterSecondConnect_LeavesNoSession()
    {
        var directory = new FakeDirectoryBuilder().Build();
        var administrator = new FakeDomainControllerSession(directory, "dc01.contoso.test");
        var reader = new FakeDomainControllerSession(directory, "dc01.contoso.test", i => i with { BoundIdentity = @"CONTOSO\odczyt.test" });
        var connector = new GatedConnector(administrator, reader);
        var manager = CreateManager(connector);
        var ct = TestContext.Current.CancellationToken;
        await manager.ConnectAsync(Contoso, null, ct);
        await manager.ConnectAsync(Contoso, null, ct);

        // One session per window: the new connection replaced and closed the previous one.
        Assert.Same(reader, manager.Current);
        Assert.True(administrator.IsDisposed);
        Assert.Same(reader, Assert.Single(manager.Sessions));

        await manager.DisconnectAsync();

        // Disconnected means disconnected: never the previous session (another identity, possibly another domain).
        Assert.Null(manager.Current);
        Assert.False(manager.IsConnected);
        Assert.True(reader.IsDisposed);
        Assert.Empty(manager.Sessions);
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SessionManager CreateManager(IDirectoryConnector connector) =>
        new(connector, new DelegateAttributeFormatterFactory(_ => new PassThroughAttributeFormatter()), new ResxLocalizer(System.Globalization.CultureInfo.GetCultureInfo("en-US")));

    /// <summary>
    /// Connector whose binds wait for <see cref="Gate"/>, ignoring the cancellation token on purpose: a native bind cannot
    /// be interrupted and may complete after its window gave up on it.
    /// </summary>
    private sealed class GatedConnector(params IDirectorySession[] sessions) : IDirectoryConnector
    {
        private readonly Queue<IDirectorySession> _sessions = new(sessions);

        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Entered { get; private set; } = NewGate();

        public IDirectorySession? ServerSession { get; init; }

        public string? GetComputerDomain() => FakeDirectoryBuilder.DefaultDomain;

        public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DomainControllerInfo>>([]);

        public async Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken)
        {
            await WaitAsync();
            return _sessions.Dequeue();
        }

        public async Task<IDirectorySession> ConnectToServerAsync(IDirectorySession current, string server, CancellationToken cancellationToken)
        {
            await WaitAsync();
            return ServerSession ?? throw new InvalidOperationException("No session for the other domain controller.");
        }

        private async Task WaitAsync()
        {
            Entered.TrySetResult();
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            Entered = NewGate();
        }
    }
}
