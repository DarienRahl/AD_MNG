using AdMng.Directory.IntegrationTests.Infrastructure;

[assembly: AssemblyFixture(typeof(ThreadPoolFloor))]

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// The tests of this assembly run side by side, and several of them block a pool thread on purpose for seconds (a bind to a
/// host that accepts and never answers, a held call): on a machine with four processors the pool then has four threads and
/// starts a new one only every half second. On a Windows runner that starved the in-process servers of the TLS probe tests -
/// "5 workers busy, 1 item waiting" in the failure, the connection accepted twelve seconds after the server started, a
/// timeout of 400 ms that took 12.6 s. A floor of threads the pool creates without waiting takes the starvation away; it
/// changes nothing a test asserts.
/// </summary>
/// <remarks>
/// <para>
/// It masks starvation, also the product's: no test of this assembly can show a thread pool starved by product code. The bind of
/// every new connection blocks a pool thread for as long as the DC takes to answer (<c>LdapConnectionFactory.OpenAsync</c>), and
/// on Linux and macOS so does every search in flight (the blocking path of <c>LdapAsync</c>); here up to 64 such threads go
/// unnoticed. A test of that needs a process of its own without the floor, as the DLR-4 probe has one
/// (<c>AdMng.Directory.DisposeProbe</c>).
/// </para>
/// <para>
/// It is not scoped to the TLS probe tests, whose servers it was added for: the minimum is a setting of the whole process, so a
/// fixture of their collection would raise it for every test running beside them all the same, and lowering it again when the
/// collection ends would starve the in-process servers of tests still running (the silent server of <c>WinLdapRequestTests</c>,
/// the <c>TcpProxy</c> of the lab's <c>PoolTests</c>).
/// </para>
/// </remarks>
public sealed class ThreadPoolFloor
{
    private const int Floor = 64;

    public ThreadPoolFloor()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, Floor), Math.Max(completionPorts, Floor));
    }
}
