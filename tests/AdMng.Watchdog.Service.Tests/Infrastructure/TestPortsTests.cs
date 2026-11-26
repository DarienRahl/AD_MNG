using System.Net;
using System.Net.Sockets;
using AdMng.Watchdog.Testing.Net;
using AdMng.Watchdog.Testing.Smtp;

namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>
/// The ports of the test servers. The test projects run side by side, and the tests of one project in parallel: a port
/// that "was free a moment ago" may be somebody's by the time it is used. A test server that has to be given a port
/// number must get over a taken one, and "nothing listens here" must be a port that is held, not one that was let go.
/// </summary>
public sealed class TestPortsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASinkWhosePortWasTakenMeanwhile_ListensOnAnotherOne()
    {
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var takenPort = ((IPEndPoint)taken.LocalEndpoint).Port;
        var handedOut = 0;
        await using var sink = new SmtpSink(new SmtpSinkOptions
        {
            NextPort = () => Interlocked.Increment(ref handedOut) == 1 ? takenPort : FreeTcpPort.Next(),
        });

        await sink.StartAsync(Ct);

        Assert.Equal(2, handedOut);
        Assert.NotEqual(takenPort, sink.Port);

        // It really listens there: the greeting of an SMTP server.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, sink.Port, Ct);
        using var reader = new StreamReader(client.GetStream());
        var greeting = await reader.ReadLineAsync(Ct);
        Assert.StartsWith("220", greeting, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClosedPort_RefusesConnections_AndIsNotGivenToATestServerWhileItIsHeld()
    {
        using var closed = FreeTcpPort.Closed();
        var endpoint = new IPEndPoint(IPAddress.Loopback, closed.Port);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var refused = await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(endpoint, Ct).AsTask());
        Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);

        // The test servers of this process never get it.
        Assert.DoesNotContain(closed.Port, Enumerable.Range(0, 200).Select(_ => FreeTcpPort.Next()));

        if (OperatingSystem.IsWindows())
        {
            // And no other socket - of whatever process - can be bound to it.
            using var other = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var inUse = Assert.Throws<SocketException>(() => other.Bind(endpoint));
            Assert.Contains(inUse.SocketErrorCode, new[] { SocketError.AddressAlreadyInUse, SocketError.AccessDenied });
        }
    }

    [Fact]
    public void AFreePort_IsNeverHandedOutTwiceInOneProcess()
    {
        var ports = Enumerable.Range(0, 200).Select(_ => FreeTcpPort.Next()).ToList();

        Assert.Equal(ports.Count, ports.Distinct().Count());
    }
}
