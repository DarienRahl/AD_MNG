using AdMng.Core.Ldap;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Pki;
using AdMng.Presentation.Session;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// The per-window PKI cache (docs/dev/p-certificate-services.md 2.5, 2.10): one build per item shared by its callers, reuse, a
/// caller's cancellation ends only its wait, everything dropped on a session change (a reconnect to the same server is not
/// reused), derived items dropped with a refreshed snapshot, the DC probes (current DC first, 3269 only for GCs, at most 4 at
/// a time), the HTTP check, <c>Dispose</c>.
/// </summary>
public sealed class PkiSnapshotServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(PkiHarness H, PkiSnapshotService Service)> CreateAsync()
    {
        var h = await PkiHarness.CreateAsync();
        return (h, new PkiSnapshotService(h.B2.Services, h.TlsProbe, h.HealthProbe));
    }

    /// <summary>The overview reader's searches of <c>CN=Public Key Services</c> (one per snapshot read).</summary>
    private static int SnapshotReads(PkiHarness h) => h.Session.Operations.Count(o =>
        o.Kind == FakeOperationKind.Search && DistinguishedName.AreEqual(o.DistinguishedName, h.Sample.PublicKeyServices));

    /// <summary>Holds the snapshot read at its first search until <paramref name="gate"/> is set.</summary>
    private static void HoldTheRead(PkiHarness h, ManualResetEventSlim gate, SemaphoreSlim started) => h.Session.BeforeOperation = operation =>
    {
        if (operation.Kind == FakeOperationKind.Search && DistinguishedName.AreEqual(operation.DistinguishedName, h.Sample.PublicKeyServices))
        {
            started.Release();
            gate.Wait(TimeSpan.FromSeconds(20));
        }
    };

    [Fact]
    public async Task ConcurrentCallers_ShareOneBuild_TheCacheIsReused_AndRefreshReadsAgain()
    {
        var (h, service) = await CreateAsync();
        using var _ = service;
        using var gate = new ManualResetEventSlim();
        using var started = new SemaphoreSlim(0);
        HoldTheRead(h, gate, started);
        var changes = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref changes);

        var first = service.GetAsync(false, null, Ct);
        var second = service.GetAsync(false, null, Ct);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.True(service.IsBuilding);
        gate.Set();
        var a = await first;
        var b = await second;

        Assert.Same(a, b);
        Assert.Equal(1, SnapshotReads(h));
        Assert.Same(a, await service.GetAsync(false, null, Ct));
        Assert.Same(a, service.Current);
        Assert.Equal(a.ReadAt, service.ReadAt);
        Assert.Equal(PkiSamples.Now, a.ReadAt);
        Assert.Equal(1, SnapshotReads(h));
        Assert.False(service.IsBuilding);
        Assert.True(changes >= 2);

        var refreshed = await service.GetAsync(true, null, Ct);
        Assert.NotSame(a, refreshed);
        Assert.Equal(2, SnapshotReads(h));
    }

    [Fact]
    public async Task ACallersCancellation_EndsOnlyItsWait()
    {
        var (h, service) = await CreateAsync();
        using var _ = service;
        using var gate = new ManualResetEventSlim();
        using var started = new SemaphoreSlim(0);
        HoldTheRead(h, gate, started);
        using var cancel = new CancellationTokenSource();

        var cancelled = service.GetAsync(false, null, cancel.Token);
        var waiting = service.GetAsync(false, null, Ct);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.True(service.IsBuilding);
        gate.Set();
        var snapshot = await waiting;
        Assert.Same(snapshot, service.Current);
        Assert.Equal(1, SnapshotReads(h));
    }

    [Fact]
    public async Task TheLastWaiterCancelling_CancelsTheBuild_AndTheNextCallReadsAgain()
    {
        var (h, service) = await CreateAsync();
        using var _ = service;
        using var gate = new ManualResetEventSlim();
        using var started = new SemaphoreSlim(0);
        HoldTheRead(h, gate, started);
        using var cancel = new CancellationTokenSource();

        var only = service.GetAsync(false, null, cancel.Token);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);
        gate.Set();
        await B2Harness.WaitUntilAsync(() => !service.IsBuilding);
        Assert.Null(service.Current);

        h.Session.BeforeOperation = null;
        Assert.NotNull(await service.GetAsync(false, null, Ct));
        Assert.Equal(2, SnapshotReads(h));
    }

    [Fact]
    public async Task SessionChanged_DropsEverything_AndAReconnectToTheSameServerIsNotReused()
    {
        // On a session of a DC (closing it does not close the shared directory), as a reconnect to the same server does.
        const string Server = "dc01.contoso.test";
        var b2 = await B2Harness.CreateOnDomainControllerAsync(Server);
        b2.Builder.AddPublicKeySample(b2.Company);
        using var service = new PkiSnapshotService(b2.Services, null, null);
        var first = b2.Sessions.Current;
        var before = await service.GetAsync(false, null, Ct);
        await service.GetExpansionAsync(false, null, Ct);
        await service.CheckHttpAsync(null, Ct);
        Assert.NotNull(service.Expansion);
        Assert.NotNull(service.HttpResults);

        await b2.ReconnectAsync(new FakeDomainControllerSession(b2.Session, Server));
        await B2Harness.WaitUntilAsync(() => service.Current is null);
        Assert.Null(service.Expansion);
        Assert.Null(service.HttpResults);

        var after = await service.GetAsync(false, null, Ct);
        Assert.NotSame(before, after);
        Assert.NotSame(first, b2.Sessions.Current);
        Assert.Equal(Server, b2.Sessions.Current!.Info.Server);
    }

    [Fact]
    public async Task Invalidate_CancelsARunningBuild_AndDropsTheCache()
    {
        var (h, service) = await CreateAsync();
        using var _ = service;
        await service.GetAsync(false, null, Ct);
        using var gate = new ManualResetEventSlim();
        using var started = new SemaphoreSlim(0);
        HoldTheRead(h, gate, started);

        var refresh = service.GetAsync(true, null, Ct);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        service.Invalidate();
        gate.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task TheExpansion_IsBuiltOnce_AndARefreshedSnapshotDropsIt()
    {
        var (_, service) = await CreateAsync();
        using var __ = service;
        var progress = new List<PkiExpansionProgress>();

        var first = await service.GetExpansionAsync(false, new Progress(progress), Ct);
        Assert.Same(first, await service.GetExpansionAsync(false, null, Ct));
        Assert.Same(first, service.Expansion);
        Assert.Contains(PublicKeyTemplates.Esc1, first.Templates.Keys);
        Assert.NotEmpty(progress);

        await service.GetAsync(true, null, Ct);
        Assert.Null(service.Expansion);
        var second = await service.GetExpansionAsync(false, null, Ct);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task TheDcChecks_ProbeTheCurrentDcFirst_And3269OnlyForGlobalCatalogs()
    {
        var (h, service) = await CreateAsync();
        using var _ = service;
        var server = h.B2.Sessions.Current!.Info.Server;
        h.TlsProbe.Set(server, 636, TlsProbeOutcome.Ok, PkiSamples.DcLdapsDer(), PkiSamples.IssuingCaDer);

        var checks = await service.GetDcCertificatesAsync(false, Ct);

        Assert.NotEmpty(checks);
        Assert.True(DomainControllerCatalog.IsSameHost(server, checks[0].Host));
        Assert.Equal(636, checks[0].Port);
        var ok = Assert.Single(checks, c => c.Outcome == TlsProbeOutcome.Ok);
        Assert.NotNull(ok.Certificate?.Leaf);
        Assert.All(h.TlsProbe.Calls, c => Assert.Equal(PkiSnapshotService.ProbeTimeout, c.Timeout));
        Assert.Same(checks, service.DcCertificates);

        // Cached: no new probe without refresh.
        var calls = h.TlsProbe.Calls.Count;
        Assert.Same(checks, await service.GetDcCertificatesAsync(false, Ct));
        Assert.Equal(calls, h.TlsProbe.Calls.Count);
        await service.GetDcCertificatesAsync(true, Ct);
        Assert.Equal(2 * calls, h.TlsProbe.Calls.Count);
    }

    [Fact]
    public void TheTargets_AreEachHostOnce_CurrentFirst_ThenByName_WithTheGcPort()
    {
        DirectoryDomainController Dc(string host, bool gc) => new(host, "Warszawa", gc, false, false);
        var targets = PkiSnapshotService.Targets(
            "DC02.contoso.test",
            [Dc("dc03.contoso.test", gc: false), Dc("dc02.contoso.test", gc: true), Dc("DC03.CONTOSO.TEST", gc: true)],
            [new PkiDomainController("CN=DC01", "dc01.contoso.test", false, []), new PkiDomainController("CN=DC02", "dc02.contoso.test", false, []),
                new PkiDomainController("CN=X", null, false, [])]);

        Assert.Equal(
            [("dc02.contoso.test", 636), ("dc02.contoso.test", 3269), ("dc01.contoso.test", 636), ("dc03.contoso.test", 636)],
            targets);
    }

    [Fact]
    public async Task TheDcChecks_ProbeAtMostFourAtATime()
    {
        var (h, service) = await CreateAsync();
        using var _ = service;
        for (var i = 2; i <= 9; i++)
        {
            h.Builder.AddComputer($"DC{i:00}", h.Builder.DomainControllersDn, new Dictionary<string, object?>
            {
                [Core.ActiveDirectory.Attr.DnsHostName] = $"dc{i:00}.contoso.test",
                [Core.ActiveDirectory.Attr.UserAccountControl] = 0x82000,
                [Core.ActiveDirectory.Attr.PrimaryGroupId] = 516,
            });
        }

        h.TlsProbe.Delay = TimeSpan.FromMilliseconds(40);

        var checks = await service.GetDcCertificatesAsync(false, Ct);

        Assert.True(checks.Count >= 9, $"{checks.Count} checks");
        Assert.InRange(h.TlsProbe.MaxConcurrency, 2, PkiSnapshotService.MaxParallelProbes);
    }

    [Fact]
    public async Task WithoutAProbe_TheDcChecksAreEmpty_AndTheHttpCheckReturnsNothing()
    {
        var h = await PkiHarness.CreateAsync();
        using var service = new PkiSnapshotService(h.B2.Services, null, null);

        Assert.Empty(await service.GetDcCertificatesAsync(false, Ct));
        Assert.Empty(await service.CheckHttpAsync(null, Ct));
        Assert.NotNull(service.HttpResults);
        Assert.Empty(h.TlsProbe.Calls);
    }

    [Fact]
    public async Task TheHttpCheck_RunsOnRequest_OverTheSnapshotsUrls_AndIsKeptUntilARefresh()
    {
        var (h, service) = await CreateAsync();
        using var _ = service;
        h.HealthProbe.Set(PkiSamples.IssuingCrlHttpUrl, PkiHttpOutcome.NotFound, 404);
        await service.GetAsync(false, null, Ct);
        Assert.Null(service.HttpResults);
        Assert.Empty(h.HealthProbe.Calls);

        var results = await service.CheckHttpAsync(null, Ct);

        var urls = Assert.Single(h.HealthProbe.Calls);
        Assert.Equal(PkiHealth.HttpUrls(service.Current!).Select(u => u.Url), urls.Select(u => u.Url));
        Assert.Contains(results, r => r.Url == PkiSamples.IssuingCrlHttpUrl && r.Outcome == PkiHttpOutcome.NotFound);
        Assert.Same(results, service.HttpResults);

        await service.GetAsync(true, null, Ct);
        Assert.Null(service.HttpResults);
    }

    [Fact]
    public async Task Dispose_DropsTheCache_RefusesNewCalls_AndIgnoresLaterSessionChanges()
    {
        var (h, service) = await CreateAsync();
        await service.GetAsync(false, null, Ct);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        service.Dispose();
        Assert.Null(service.Current);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetAsync(false, null, Ct));
        var afterDispose = changes;
        await h.B2.Sessions.DisconnectAsync();
        Assert.Equal(afterDispose, changes);
        service.Dispose();
        Assert.Equal("PkiSnapshotService(snapshot False, expansion False, DCs False, LDAPS False, HTTP False, building False)", service.ToString());
    }

    [Fact]
    public async Task WithoutASession_TheCallsSayTheSessionEnded()
    {
        var (h, service) = await CreateAsync();
        using var _ = service;
        await h.B2.Sessions.DisconnectAsync();

        await Assert.ThrowsAsync<SessionEndedException>(() => service.GetAsync(false, null, Ct));
    }

    private sealed class Progress(List<PkiExpansionProgress> sink) : IProgress<PkiExpansionProgress>
    {
        public void Report(PkiExpansionProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
