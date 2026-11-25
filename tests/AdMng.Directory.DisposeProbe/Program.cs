using System.Diagnostics;
using System.Globalization;
using System.Net;
using AdMng.Core.Directory;
using AdMng.Directory;
using Sdsp = System.DirectoryServices.Protocols;

// Audit finding DLR-4: the exit code 134 (SIGABRT) of the Watchdog lab tests.
//
// S.DS.P's LdapConnection.ConstructEntry frees each attribute name (ldap_memfree) and then calls ldap_next_attribute with
// the connection's SafeHandle. When the connection was disposed meanwhile, that call throws (ObjectDisposedException, or
// NullReferenceException once Dispose has nulled the handle) and the method's finally frees the same name again: glibc
// aborts the process (SIGABRT, exit code 134), or the heap is corrupted and the process dies later (SIGSEGV, 139) or hangs
// in malloc (dotnet/runtime#35189, open). AD Manager must therefore never dispose a connection while a request runs on it.
//
// The probe closes connections while the thread that runs SendRequest builds the entries of a large search response. That
// moment is found without sleeps: ldap_result (native) allocates no managed memory while it waits for the answer, the entry
// loop allocates a lot, so the closing thread spins until the process has allocated a threshold since the request started.
//
// Usage: AdMng.Directory.DisposeProbe <mode> [iterations] [threshold KB]
//   raw                    positive control, S.DS.P only: SendRequest on a worker thread, LdapConnection.Dispose during
//                          the entry loop - aborts the process (the defect the other modes must never reach)
//   dispose-during-search  LdapDirectorySession.SearchAsync, the session's DisposeAsync during the entry loop
//   cancel-then-dispose    the search's token is cancelled (its caller returns at once, the blocking request goes on) and
//                          the session is disposed right after - what a Watchdog host does when it stops
//   cancel-connect         ConnectAsync is cancelled at a varying moment (RootDSE, the first reads, the schema load), so
//                          the connector closes a session whose first searches still run
// Every iteration prints "iteration <n>: <outcome>"; the last line is "survived <n> iterations" (exit code 0). A
// NullReferenceException from S.DS.P (a request that used a disposed connection) ends the probe with exit code 2.
// Environment: ADMNG_TEST_LDAP_SERVER, ADMNG_TEST_LDAP_USER, ADMNG_TEST_LDAP_PASSWORD, ADMNG_TEST_LDAP_BASE,
// ADMNG_TEST_LDAP_DOMAIN.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: AdMng.Directory.DisposeProbe <raw|dispose-during-search|cancel-then-dispose|cancel-connect> [iterations] [threshold KB]");
    return 1;
}

var mode = args[0];
var iterations = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 20;
var thresholdKb = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 256;
var server = Env("ADMNG_TEST_LDAP_SERVER", "127.0.0.1");
var user = Env("ADMNG_TEST_LDAP_USER", "Administrator@contoso.test");
var password = Env("ADMNG_TEST_LDAP_PASSWORD", "P@ssw0rd!2026");
var baseDn = Env("ADMNG_TEST_LDAP_BASE", "DC=contoso,DC=test");
var domain = Env("ADMNG_TEST_LDAP_DOMAIN", "contoso.test");

// Every user and computer of the lab with all of its attributes: hundreds of entries, dozens of attributes each.
const string Filter = "(objectClass=user)";

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"pid {Environment.ProcessId} mode {mode} iterations {iterations} threshold {thresholdKb} KB runtime {Environment.Version} S.DS.P {typeof(Sdsp.LdapConnection).Assembly.GetName().Version}"));

var outcomes = new SortedDictionary<string, int>(StringComparer.Ordinal);
for (var i = 1; i <= iterations; i++)
{
    // A different moment in every iteration: early in the entry loop, later in it, or (cancel-connect) in another read.
    var threshold = (mode == "cancel-connect" ? 32L << (i % 9) : thresholdKb << (i % 4)) * 1024;
    string outcome;
    try
    {
        outcome = mode switch
        {
            "raw" => RawIteration(threshold),
            "dispose-during-search" => await SessionIteration(threshold, cancelFirst: false),
            "cancel-then-dispose" => await SessionIteration(threshold, cancelFirst: true),
            "cancel-connect" => await ConnectIteration(threshold),
            _ => throw new ArgumentException("Unknown mode: " + mode),
        };
    }
    catch (NullReferenceException ex)
    {
        // S.DS.P used a connection that was disposed under it: the outcome the fix rules out (and one step from the abort).
        Console.WriteLine($"iteration {i}: NullReferenceException - a request ran on a disposed connection{Environment.NewLine}{ex}");
        return 2;
    }

    outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"iteration {i}: {outcome}"));
}

foreach (var (outcome, count) in outcomes)
{
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {count} x {outcome}"));
}

Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"survived {iterations} iterations"));
return 0;

string RawIteration(long threshold)
{
    var connection = new Sdsp.LdapConnection(
        new Sdsp.LdapDirectoryIdentifier(server, 389),
        new NetworkCredential(user, password),
        Sdsp.AuthType.Basic);
    connection.SessionOptions.ProtocolVersion = 3;
    connection.Bind();
    var request = new Sdsp.SearchRequest(baseDn, Filter, Sdsp.SearchScope.Subtree, null);
    var baseline = GC.GetTotalAllocatedBytes(precise: false);
    var work = Task.Factory.StartNew(
        () => connection.SendRequest(request, TimeSpan.FromSeconds(60)),
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);
    SpinUntilAllocated(baseline, threshold, work);
    connection.Dispose();
    try
    {
        var response = (Sdsp.SearchResponse)work.GetAwaiter().GetResult();
        return $"response with {response.Entries.Count} entries";
    }
    catch (Exception ex) when (ex is not NullReferenceException)
    {
        return ex.GetType().Name;
    }
    catch (NullReferenceException)
    {
        // The handle was already nulled when the request reached its next call; not the case this probe is about.
        return "NullReferenceException (raw S.DS.P)";
    }
}

async Task<string> SessionIteration(long threshold, bool cancelFirst)
{
    var session = await new LdapDirectoryConnector().ConnectAsync(Options(), new NetworkCredential(string.Empty, password), CancellationToken.None);

    // The pool opens its further connections in the background while the session initialises; let that settle.
    await Task.Delay(300);
    var search = new DirectorySearch
    {
        BaseDn = baseDn,
        Filter = Filter,
        Attributes = ["*"],
        PageSize = 1000,
    };
    using var cts = new CancellationTokenSource();
    var count = 0;
    var baseline = GC.GetTotalAllocatedBytes(precise: false);
    var work = Task.Run(async () =>
    {
        await foreach (var _ in session.SearchAsync(search, cts.Token))
        {
            count++;
        }
    });
    SpinUntilAllocated(baseline, threshold, work);
    if (cancelFirst)
    {
        await cts.CancelAsync();
        await Outcome(work);
    }

    await session.DisposeAsync();

    // A request that still runs reaches its next call of S.DS.P meanwhile.
    await Task.Delay(100);
    return cancelFirst ? "cancelled, then closed" : await Outcome(work);
}

async Task<string> ConnectIteration(long threshold)
{
    using var cts = new CancellationTokenSource();
    var baseline = GC.GetTotalAllocatedBytes(precise: false);
    var connect = Task.Run(() => new LdapDirectoryConnector().ConnectAsync(Options(), new NetworkCredential(string.Empty, password), cts.Token));
    SpinUntilAllocated(baseline, threshold, connect);
    await cts.CancelAsync();
    try
    {
        await using var session = await connect;
        return "connected before the cancellation";
    }
    catch (Exception ex) when (ex is OperationCanceledException or DirectoryException)
    {
        // The connector closed the half-built session; its searches that still ran must end before their connections close.
        await Task.Delay(100);
        return "cancelled while connecting (" + ex.GetType().Name + ")";
    }
}

ConnectionOptions Options() => new()
{
    Server = server,
    Domain = domain,
    Authentication = AuthenticationMode.SimpleBind,
    Security = TransportSecurity.None,
    AllowInsecureSimpleBind = true,
    UserName = user,
    RequestTimeout = TimeSpan.FromSeconds(60),
    ConnectTimeout = TimeSpan.FromSeconds(5),
};

static void SpinUntilAllocated(long baseline, long threshold, Task work)
{
    var watch = Stopwatch.StartNew();
    while (GC.GetTotalAllocatedBytes(precise: false) - baseline < threshold && !work.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30))
    {
        Thread.SpinWait(10);
    }
}

static async Task<string> Outcome(Task work)
{
    try
    {
        await work;
        return "completed";
    }
    catch (NullReferenceException)
    {
        throw;
    }
    catch (Exception ex)
    {
        return ex.GetType().Name + (ex.InnerException is { } inner and not NullReferenceException ? " <- " + inner.GetType().Name : string.Empty);
    }
}

static string Env(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
