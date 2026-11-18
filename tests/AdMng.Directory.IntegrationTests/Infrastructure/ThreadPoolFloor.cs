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
public sealed class ThreadPoolFloor
{
    private const int Floor = 64;

    public ThreadPoolFloor()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, Floor), Math.Max(completionPorts, Floor));
    }
}
