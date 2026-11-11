using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Directory.Pki;
using AdMng.Testing.Pki;

namespace AdMng.Directory.IntegrationTests.Pki;

/// <summary>
/// The DC LDAPS handshake probe (docs/dev/p-certificate-services.md 2.6, 2.10) against an in-process TLS server on
/// <c>localhost</c> (<see cref="TcpListener"/> on the loopback address + <see cref="SslStream.AuthenticateAsServerAsync(SslServerAuthenticationOptions, CancellationToken)"/>
/// with <see cref="PkiSamples.ServerCertificate"/>): the probe still admits only 636 / 3269 and connects the listener's port
/// through its test seam. Runs on Linux and Windows; the lab test probes the Samba DC.
/// </summary>
public sealed class SslStreamTlsProbeTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AHandshake_CapturesTheLeafAndThePresentedIntermediate_AndOffersNoClientCertificate()
    {
        using var leaf = PkiSamples.ServerCertificate();
        await using var server = TlsServer.Start(leaf, intermediates: [PkiSamples.IssuingCa], requestClientCertificate: true);

        var result = await Probe(server.Port).ProbeAsync("localhost", 636, Generous, TestContext.Current.CancellationToken);

        Assert.Equal(TlsProbeOutcome.Ok, result.Outcome);
        Assert.Equal("localhost", result.Host);
        Assert.Equal(636, result.Port);
        Assert.NotNull(result.Protocol);
        Assert.Equal(leaf.RawData, result.PresentedChain[0]);
        Assert.Contains(result.PresentedChain, der => der.AsSpan().SequenceEqual(PkiSamples.IssuingCaDer));
        Assert.DoesNotContain(result.PresentedChain, der => der.AsSpan().SequenceEqual(PkiSamples.RootCaDer));
        Assert.Equal(result.PresentedChain.Count, result.PresentedChain.Select(Convert.ToHexString).Distinct().Count());

        // The server asked for a client certificate (as AD LDAPS does) and got none; no application data was sent.
        var seen = await server.ClientAsync();
        Assert.True(seen.Authenticated);
        Assert.False(seen.ClientCertificatePresented);
        Assert.Equal(0, seen.ApplicationBytes);

        // The evaluation of the captured chain (C1) chains it to the AD root without downloading anything.
        var info = DcCertificateEvaluation.Evaluate(result.PresentedChain, "localhost", "contoso.test", Trust(), PkiSamples.Now);
        Assert.True(info.NameMatchesDc);
        Assert.True(info.HasServerAuth);
        Assert.True(info.IsTimeValid);
        Assert.True(info.ChainsToAdRoot);
        Assert.Equal("636: Ok", result.ToString());
    }

    [Fact]
    public async Task AnExpiredLeaf_IsStillCaptured_AndJudgedExpiredByTheEvaluation()
    {
        using var leaf = PkiSamples.ServerCertificate(notBefore: new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), notAfter: new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var server = TlsServer.Start(leaf, intermediates: [PkiSamples.IssuingCa]);

        var result = await Probe(server.Port).ProbeAsync("localhost", 636, Generous, TestContext.Current.CancellationToken);

        Assert.Equal(TlsProbeOutcome.Ok, result.Outcome);
        Assert.Equal(leaf.RawData, result.PresentedChain[0]);
        Assert.NotNull(result.PolicyErrors);
        Assert.NotEqual(SslPolicyErrors.None, result.PolicyErrors);
        var info = DcCertificateEvaluation.Evaluate(result.PresentedChain, "localhost", "contoso.test", Trust(), PkiSamples.Now);
        Assert.False(info.IsTimeValid);
        Assert.True(info.DaysLeft < 0);
    }

    [Fact]
    public async Task AWrongSan_IsCaptured_WithTheNameMismatchReported()
    {
        using var leaf = PkiSamples.ServerCertificate(dnsName: "dc99.contoso.test");
        await using var server = TlsServer.Start(leaf, intermediates: [PkiSamples.IssuingCa]);

        var result = await Probe(server.Port).ProbeAsync("localhost", 636, Generous, TestContext.Current.CancellationToken);

        Assert.Equal(TlsProbeOutcome.Ok, result.Outcome);
        Assert.True(result.PolicyErrors?.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
        var info = DcCertificateEvaluation.Evaluate(result.PresentedChain, "localhost", "contoso.test", Trust(), PkiSamples.Now);
        Assert.False(info.NameMatchesDc);
        Assert.Equal("dc99.contoso.test", Assert.Single(info.Leaf!.SanDns));
    }

    [Fact]
    public async Task ASelfSignedLeaf_AloneInThePresentedChain_DoesNotChainToTheAdRoots()
    {
        using var leaf = PkiSamples.ServerCertificate(selfSigned: true);
        await using var server = TlsServer.Start(leaf, intermediates: []);

        var result = await Probe(server.Port).ProbeAsync("localhost", 3269, Generous, TestContext.Current.CancellationToken);

        Assert.Equal(TlsProbeOutcome.Ok, result.Outcome);
        Assert.Equal(3269, result.Port);
        Assert.Equal(leaf.RawData, Assert.Single(result.PresentedChain));
        Assert.False(DcCertificateEvaluation.Evaluate(result.PresentedChain, "localhost", "contoso.test", Trust(), PkiSamples.Now).ChainsToAdRoot);
    }

    [Fact]
    public async Task AClosedPort_IsPortClosed()
    {
        // The port stays taken while the probe runs. A port that was only free a moment ago may be the listener of
        // another test by then (the test projects run side by side), and a listener that never answers is a timeout.
        using var closed = ClosedPort.Reserve();

        var result = await Probe(closed.Port).ProbeAsync("localhost", 636, Generous, TestContext.Current.CancellationToken);

        Assert.Equal(TlsProbeOutcome.PortClosed, result.Outcome);
        Assert.Empty(result.PresentedChain);
        Assert.Null(result.Protocol);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AServerThatDropsTheHandshake_IsHandshakeFailed_TheDcWithoutAnLdapsCertificate(bool reset)
    {
        // AD DS listens on 636 without a usable certificate and resets the connection after the ClientHello.
        await using var server = RawServer.Start(async (socket, ct) =>
        {
            var buffer = new byte[512];
            _ = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
            if (reset)
            {
                socket.LingerState = new LingerOption(true, 0);
            }

            socket.Close();
        });

        var result = await Probe(server.Port).ProbeAsync("localhost", 636, Generous, TestContext.Current.CancellationToken);

        Assert.Equal(TlsProbeOutcome.HandshakeFailed, result.Outcome);
        Assert.Empty(result.PresentedChain);
    }

    [Fact]
    public async Task AServerThatNeverAnswers_IsTheProbesOwnTimeout()
    {
        await using var server = RawServer.Start((_, ct) => Task.Delay(Timeout.Infinite, ct));
        var stopwatch = Stopwatch.StartNew();

        var result = await Probe(server.Port).ProbeAsync("localhost", 636, TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

        Assert.Equal(TlsProbeOutcome.Timeout, result.Outcome);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task TheCallersCancellation_Propagates_InsteadOfATimeout()
    {
        await using var server = RawServer.Start((_, ct) => Task.Delay(Timeout.Infinite, ct));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe(server.Port).ProbeAsync("localhost", 636, Generous, cancellation.Token));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SslStreamTlsProbe().ProbeAsync("127.0.0.1", 636, Generous, cancelled.Token));
    }

    [Theory]
    [InlineData("127.0.0.1", 636)]
    [InlineData("::1", 636)]
    [InlineData("", 636)]
    [InlineData("   ", 636)]
    [InlineData("dc01 contoso.test", 636)]
    [InlineData("-dc01.contoso.test", 636)]
    [InlineData("localhost", 389)]
    [InlineData("localhost", 443)]
    [InlineData("localhost", 3268)]
    public async Task AnInvalidHostOrPort_ContactsNothing(string host, int port)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var result = await Probe(listenerPort).ProbeAsync(host, port, Generous, TestContext.Current.CancellationToken);

        Assert.Equal(TlsProbeOutcome.InvalidHost, result.Outcome);
        Assert.Empty(result.PresentedChain);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task AnUnknownName_IsNameNotResolved()
    {
        var result = await new SslStreamTlsProbe().ProbeAsync("dc-nieistniejacy.contoso.invalid", 636, Generous, TestContext.Current.CancellationToken);

        // A resolver that cannot be reached at all answers late or not at all: then the probe's own timeout says so.
        Assert.Contains(result.Outcome, new[] { TlsProbeOutcome.NameNotResolved, TlsProbeOutcome.Timeout });
        Assert.Equal("dc-nieistniejacy.contoso.invalid", result.Host);
    }

    [Fact]
    public async Task ANonPositiveTimeout_IsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new SslStreamTlsProbe().ProbeAsync("localhost", 636, TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheHandshakeOptions_OfferNoCertificate_AndTheirChainPolicyCanReachNothing()
    {
        var options = SslStreamTlsProbe.Options("dc01.contoso.test", new SslStreamTlsProbe.Capture());

        Assert.Equal("dc01.contoso.test", options.TargetHost);
        Assert.Empty(options.ClientCertificates!);
        Assert.Null(options.LocalCertificateSelectionCallback!(this, "dc01.contoso.test", [], null, []));

        // Decision 1: no AIA / CRL / OCSP fetch, and an empty custom root store – neither the system store nor Windows'
        // automatic root update (which downloads roots and the CTL) takes part in the handshake's own chain build.
        var policy = options.CertificateChainPolicy!;
        Assert.True(policy.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);
        Assert.Equal(X509ChainTrustMode.CustomRootTrust, policy.TrustMode);
        Assert.Empty(policy.CustomTrustStore);
        Assert.Empty(policy.ExtraStore);

        // A fresh policy per probe: the handshake adds the presented certificates to its ExtraStore.
        Assert.NotSame(policy, SslStreamTlsProbe.Options("dc01.contoso.test", new SslStreamTlsProbe.Capture()).CertificateChainPolicy);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, TlsProbeOutcome.NameNotResolved)]
    [InlineData(SocketError.NoData, TlsProbeOutcome.NameNotResolved)]
    [InlineData(SocketError.TryAgain, TlsProbeOutcome.NameNotResolved)]
    [InlineData(SocketError.ConnectionRefused, TlsProbeOutcome.PortClosed)]
    [InlineData(SocketError.ConnectionReset, TlsProbeOutcome.PortClosed)]
    [InlineData(SocketError.TimedOut, TlsProbeOutcome.Timeout)]
    [InlineData(SocketError.HostUnreachable, TlsProbeOutcome.Timeout)]
    [InlineData(SocketError.NetworkUnreachable, TlsProbeOutcome.Timeout)]
    public void ConnectFailures_MapToTheirOutcome(SocketError error, TlsProbeOutcome expected) =>
        Assert.Equal(expected, SslStreamTlsProbe.ConnectOutcome(error));

    [Fact]
    public async Task TheLabDc_PresentsItsSelfSignedLdapsCertificate()
    {
        LabSettings.RequireLab();

        // The lab DC listens on the loopback address; the probe needs a name (an IP is refused), the certificate is Samba's
        // self-signed one, so it is presented but chains to no AD root.
        var result = await new SslStreamTlsProbe().ProbeAsync("localhost", 636, SslStreamTlsProbe.DefaultTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(TlsProbeOutcome.Ok, result.Outcome);
        Assert.NotEmpty(result.PresentedChain);
        var info = DcCertificateEvaluation.Evaluate(result.PresentedChain, "localhost", LabSettings.DomainDns, PkiTrustMaterial.Empty, DateTimeOffset.UtcNow);
        Assert.NotNull(info.Leaf);
        Assert.False(info.ChainsToAdRoot);
    }

    private static SslStreamTlsProbe Probe(int listenerPort) => new() { ConnectPort = _ => listenerPort };

    private static PkiTrustMaterial Trust() => new([PkiSamples.RootCaDer], [PkiSamples.IssuingCaDer], [PkiSamples.IssuingCaDer]);

    /// <summary>
    /// A port of the loopback addresses that refuses every connection for as long as this object lives: sockets that
    /// are bound to it and never listen. Nothing else can take the port meanwhile, on either address "localhost"
    /// resolves to.
    /// </summary>
    private sealed class ClosedPort : IDisposable
    {
        private readonly Socket[] _sockets;

        private ClosedPort(int port, Socket[] sockets)
        {
            Port = port;
            _sockets = sockets;
        }

        public int Port { get; }

        public static ClosedPort Reserve()
        {
            for (var attempt = 0; ; attempt++)
            {
                var v4 = Bind(IPAddress.Loopback, 0);
                var port = ((IPEndPoint)v4.LocalEndPoint!).Port;
                if (!Socket.OSSupportsIPv6)
                {
                    return new ClosedPort(port, [v4]);
                }

                try
                {
                    return new ClosedPort(port, [v4, Bind(IPAddress.IPv6Loopback, port)]);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 20)
                {
                    // The same number is somebody's on the IPv6 loopback address: another port.
                    v4.Dispose();
                }
                catch (SocketException)
                {
                    // No IPv6 loopback address on this machine: nothing can listen there either.
                    return new ClosedPort(port, [v4]);
                }
            }
        }

        public void Dispose()
        {
            foreach (var socket in _sockets)
            {
                socket.Dispose();
            }
        }

        private static Socket Bind(IPAddress address, int port)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Nobody binds the same port next to this socket (Windows allows that to a socket that asks for it).
                    socket.ExclusiveAddressUse = true;
                }

                socket.Bind(new IPEndPoint(address, port));
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    /// <summary>What the TLS server saw of the probe.</summary>
    private sealed record ClientSeen(bool Authenticated, bool ClientCertificatePresented, int ApplicationBytes);

    /// <summary>One TLS server connection on the loopback address (the presented chain set by an <see cref="SslStreamCertificateContext"/>).</summary>
    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<ClientSeen> _client;

        private TlsServer(X509Certificate2 leaf, X509Certificate2[] intermediates, bool requestClientCertificate)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            var context = SslStreamCertificateContext.Create(leaf, new X509Certificate2Collection(intermediates), offline: true);
            _client = Task.Run(() => ServeAsync(context, requestClientCertificate, _stop.Token));
        }

        public int Port { get; }

        public static TlsServer Start(X509Certificate2 leaf, X509Certificate2[] intermediates, bool requestClientCertificate = false) =>
            new(leaf, intermediates, requestClientCertificate);

        public Task<ClientSeen> ClientAsync() => _client.WaitAsync(Generous);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _client.WaitAsync(Generous);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException
                or System.Security.Authentication.AuthenticationException)
            {
            }

            _stop.Dispose();
        }

        private async Task<ClientSeen> ServeAsync(SslStreamCertificateContext context, bool requestClientCertificate, CancellationToken cancellationToken)
        {
            using var socket = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false);
            await stream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = context,
                    ClientCertificateRequired = requestClientCertificate,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
                cancellationToken);
            var presented = stream.RemoteCertificate is not null;

            // Everything the probe sends after the handshake (it must be nothing: it closes the stream).
            var buffer = new byte[256];
            var total = 0;
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                }
            }
            catch (IOException)
            {
                // The probe closed the connection without a close_notify.
            }

            return new ClientSeen(stream.IsAuthenticated, presented, total);
        }
    }

    /// <summary>A plain TCP server for one connection, handled by <c>handle</c> (no TLS: it drops or hangs the handshake).</summary>
    private sealed class RawServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;

        private RawServer(Func<Socket, CancellationToken, Task> handle)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = Task.Run(async () =>
            {
                using var socket = await _listener.AcceptSocketAsync(_stop.Token);
                await handle(socket, _stop.Token);
            });
        }

        public int Port { get; }

        public static RawServer Start(Func<Socket, CancellationToken, Task> handle) => new(handle);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve.WaitAsync(Generous);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }

            _stop.Dispose();
        }
    }
}
