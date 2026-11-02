using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// Signals that reach a thread while it waits in libldap for the server's answer – in this process the SIGCHLD of every
/// child process that exits (samba-tool, smbclient, ldapsearch of other tests; smbclient of the Watchdog service). The
/// interrupted <c>poll()</c> must not end the request as "server down" (LDAP 81): the connection is fine and the server
/// still carries the request out. CI failed so with "Add failed: ServerUnavailable (LDAP 81)" during the bulk creation of
/// contacts, and the object existed afterwards.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class SignalTests(LabFixture lab)
{
    private const string LinuxOnly = "Signals interrupt libldap's poll() on Linux; WinLDAP waits for answers on its own thread.";

    [Fact]
    public async Task SignalWhileAPageIsAwaited_NeitherFailsTheSearchNorDropsTheConnection()
    {
        LabSettings.RequireLab();
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        var ct = TestContext.Current.CancellationToken;
        ThreadSignals.EnsureChildProcessHandling();
        await using var proxy = new TcpProxy(LabSettings.Server!, 389);
        var logs = new CapturingLoggerFactory();
        var options = LabSettings.Plain(LabSettings.AdminUser) with { Server = "127.0.0.1", Port = proxy.Port, MaxConnections = 1 };
        await using var session = await LabFixture.ConnectAsync(options, LabSettings.AdminPassword, logs, cancellationToken: ct);
        try
        {
            await PoolTests.WaitUntilIdleAsync(((LdapDirectorySession)session).Pool, ct);
            var search = new DirectorySearch { BaseDn = LabSettings.EmployeesOu, Attributes = [Attr.Name], PageSize = 10, SizeLimit = 30 };
            await using var entries = session.SearchAsync(search, ct).GetAsyncEnumerator(ct);
            for (var i = 0; i < 10; i++)
            {
                Assert.True(await entries.MoveNextAsync());
            }

            // The second page is requested once the first one is consumed. Its answer is held back, so the request waits in
            // libldap's poll() - and a page cannot move to another connection (the paging cookie belongs to this one).
            proxy.StallExisting();
            var next = entries.MoveNextAsync().AsTask();
            try
            {
                await Task.Delay(300, ct);
                Assert.False(next.IsCompleted);
                Assert.True(ThreadSignals.SendToEveryThread(ThreadSignals.ChildExited) > 0);
                await Task.Delay(200, ct);
            }
            finally
            {
                proxy.ResumeAll();
            }

            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(30), ct));
            var count = 11;
            while (await entries.MoveNextAsync())
            {
                count++;
            }

            Assert.Equal(30, count);
            Assert.Empty(logs.WithEventId(1101).Select(r => r.Message)); // no connection dropped
        }
        finally
        {
            proxy.ResumeAll();
        }
    }

    [Fact]
    public async Task ParallelAdds_WhileChildProcessesOfThisProcessExit_AllSucceed()
    {
        LabSettings.RequireLab();
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        const int Count = 200;
        var ct = TestContext.Current.CancellationToken;
        var logs = new CapturingLoggerFactory();
        await using var session = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword, logs, cancellationToken: ct);
        var ou = await lab.CreateOuAsync(TestNames.Unique("Sygnały"));

        // As in CI: other tests of this process run samba-tool or ldapsearch while the bulk adds of a test are in flight.
        // Every exit sends SIGCHLD to this process, by preference to the thread pool thread that started the child.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var exited = 0;
        var children = Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var child = Process.Start(new ProcessStartInfo("/bin/true") { UseShellExecute = false })!;
                    await child.WaitForExitAsync(CancellationToken.None);
                    Interlocked.Increment(ref exited);
                }
            },
            CancellationToken.None);

        var failures = new ConcurrentQueue<string>();
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, Count),
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
                async (i, token) =>
                {
                    var dn = DistinguishedName.Parse(ou).Child("CN", $"Kontakt {i:D3} Źdźbło").ToString();
                    try
                    {
                        await session.AddAsync(
                            dn,
                            new Dictionary<string, IReadOnlyList<object>> { [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.Contact] },
                            token);
                    }
                    catch (DirectoryException ex)
                    {
                        failures.Enqueue($"{i}: {ex.Message}");
                    }
                });
        }
        finally
        {
            await stop.CancelAsync();
            await children;
        }

        Assert.True(exited > 0, "No child process exited during the adds.");
        Assert.Empty(failures);
        Assert.Empty(logs.WithEventId(1101).Select(r => r.Message)); // no connection dropped
        var created = 0;
        await foreach (var _ in session.SearchAsync(new DirectorySearch { BaseDn = ou, Scope = SearchScope.OneLevel, Attributes = [Attr.Name] }, ct))
        {
            created++;
        }

        Assert.Equal(Count, created);
    }
}
