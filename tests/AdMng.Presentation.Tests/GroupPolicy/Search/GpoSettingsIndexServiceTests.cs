using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy.Search;

/// <summary>
/// The scoped settings index of a connection window (K7, docs/dev/k-computers-and-gpo.md 21.3) on the Polish GPO management
/// sample: one build, reuse, stale and dropped views, a culture switch without folder reads, the generation guard, the
/// waiters and their cancellation, the queued follow-up and the build's own SYSVOL connection.
/// </summary>
public sealed class GpoSettingsIndexServiceTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OneBuild_ThenIfMissingReusesTheView_AndTheConnectionIsDisposed()
    {
        var harness = await IndexHarness.CreateAsync();
        using var service = new GpoSettingsIndexService(harness.Services);
        var changes = 0;
        service.StateChanged += (_, _) => changes++;

        var view = await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        var lists = harness.Sysvol.Calls.Count(c => c.Operation == "List");
        var again = await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);

        Assert.Same(view, again);
        Assert.Same(view, service.Current);
        Assert.Equal(lists, harness.Sysvol.Calls.Count(c => c.Operation == "List"));
        Assert.Equal(7, view.Index.Entries.Count);
        Assert.Equal("dc01.contoso.test", view.Index.Server);
        Assert.Equal(Polish, view.Culture);
        Assert.Null(view.SysvolReason);
        Assert.Equal(0, harness.Sysvol.OpenConnections);
        Assert.Single(harness.Sysvol.OpenedOn);
        Assert.False(service.IsBuilding);
        Assert.Null(service.Progress);
        Assert.True(changes > 0);
        Assert.Equal(@"CONTOSO\GG_IT", view.SidNames[harness.B2.Builder.SidOf(harness.B2.Company.GroupIt)]);
        Assert.DoesNotContain("Księgowość", view.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changed_MarksTheViewStale_WithoutARebuild_AndIncrementalRereadsOnlyChanges()
    {
        var harness = await IndexHarness.CreateAsync();
        using var service = new GpoSettingsIndexService(harness.Services);
        var view = await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        harness.Sysvol.ClearCalls();

        harness.Services.NotifyChanged();

        Assert.True(service.IsStale);
        Assert.Same(view, await service.GetAsync(GpoIndexRefresh.IfMissing, Ct));
        Assert.Empty(harness.Sysvol.Calls);

        var refreshed = await service.GetAsync(GpoIndexRefresh.Incremental, Ct);
        Assert.NotSame(view, refreshed);
        Assert.False(service.IsStale);
        Assert.DoesNotContain(harness.Sysvol.Calls, c => c.Operation == "List"); // nothing changed: every entry reused
    }

    [Fact]
    public async Task AChangeDuringTheFirstBuild_LeavesItsViewStale()
    {
        var harness = await IndexHarness.CreateAsync();
        using var service = new GpoSettingsIndexService(harness.Services);
        var hold = harness.Sysvol.HoldNext("Read");

        var build = service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        harness.Services.NotifyChanged(); // e.g. a copy finished in another tab while the folders were read
        hold.Release();

        var view = await build;
        Assert.Same(view, service.Current);
        Assert.True(service.IsStale);
        await service.GetAsync(GpoIndexRefresh.Incremental, Ct);
        Assert.False(service.IsStale);
    }

    [Fact]
    public async Task AnotherDomainController_DropsTheView()
    {
        var harness = await IndexHarness.CreateAsync();
        using var service = new GpoSettingsIndexService(harness.Services);
        await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);

        await harness.B2.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, Ct);

        Assert.Null(service.Current);
        var view = await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        Assert.Equal("dc02.contoso.test", view.Index.Server);
        Assert.Contains("dc02.contoso.test", harness.Sysvol.OpenedOn);
    }

    [Fact]
    public async Task AReconnectToTheSameServer_NeverReusesTheOldView()
    {
        var harness = await IndexHarness.CreateAsync();
        using var service = new GpoSettingsIndexService(harness.Services);
        var first = await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        harness.B2.NextSession = new FakeDomainControllerSession(harness.B2.Session, "dc01.contoso.test");

        await harness.B2.Sessions.ConnectAsync(new ConnectionOptions { Domain = FakeDirectoryBuilder.DefaultDomain }, null, Ct);
        harness.Sysvol.ClearCalls();
        var second = await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);

        Assert.Equal(first.Index.Server, second.Index.Server);
        Assert.NotSame(first, second);
        Assert.Equal(7, harness.Sysvol.Calls.Count(c => c.Operation == "List")); // read again under the new session
    }

    [Fact]
    public async Task ACultureSwitch_ReResolvesNamesAndDefinitions_WithoutGpoFolderReads()
    {
        var harness = await IndexHarness.CreateAsync();
        harness.AddCentralStore();
        using var service = new GpoSettingsIndexService(harness.Services);
        var polish = await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        harness.Sysvol.ClearCalls();

        harness.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        var english = await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);

        Assert.Equal("en-US", english.Culture.Name);
        Assert.NotSame(polish, english);
        Assert.DoesNotContain(harness.Sysvol.Calls, c => c.Operation is "List" or "Read" && c.GpoId != Guid.Empty);
        Assert.Equal(polish.Index.Entries.Count, english.Index.Entries.Count);
        Assert.NotSame(polish.Index.Definitions, english.Index.Definitions);
        Assert.Same(english, await service.GetAsync(GpoIndexRefresh.IfMissing, Ct));
    }

    [Fact]
    public async Task IfMissingAfterACultureSwitch_NeverJoinsTheBuildOfTheOldCulture()
    {
        // The cleanup tab's first read is running (Polish); the language switches and the search tab asks again.
        var harness = await IndexHarness.CreateAsync();
        harness.AddCentralStore();
        using var service = new GpoSettingsIndexService(harness.Services);
        var hold = harness.Sysvol.HoldNext("Read");

        var polishRead = service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        harness.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        var afterSwitch = service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        hold.Release();

        Assert.Equal("pl-PL", (await polishRead).Culture.Name);
        var english = await afterSwitch;
        Assert.Equal("en-US", english.Culture.Name);
        Assert.Same(english, service.Current);
    }

    [Fact]
    public async Task ACultureSwitch_OnAStaleView_KeepsItStale()
    {
        var harness = await IndexHarness.CreateAsync();
        using var service = new GpoSettingsIndexService(harness.Services);
        await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        harness.Services.NotifyChanged();
        Assert.True(service.IsStale);

        harness.B2.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        var english = await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);

        Assert.Equal("en-US", english.Culture.Name);
        Assert.True(service.IsStale); // the texts changed, the data is still the old read
        await service.GetAsync(GpoIndexRefresh.Incremental, Ct);
        Assert.False(service.IsStale);
    }

    [Fact]
    public async Task ABuildStartedBeforeInvalidate_NeverBecomesCurrent()
    {
        var harness = await IndexHarness.CreateAsync();
        using var service = new GpoSettingsIndexService(harness.Services);
        var hold = harness.Sysvol.HoldNext("Read");

        var build = service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.True(service.IsBuilding);
        service.Invalidate();
        hold.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
        await WaitUntilAsync(() => !service.IsBuilding);
        Assert.Null(service.Current);
        Assert.Equal(0, harness.Sysvol.OpenConnections);
    }

    [Fact]
    public async Task ACallersCancel_DoesNotCancelTheBuildAnotherCallerWaitsFor()
    {
        var harness = await IndexHarness.CreateAsync();
        using var service = new GpoSettingsIndexService(harness.Services);
        var hold = harness.Sysvol.HoldNext("Read");
        using var search = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var fromSearch = service.GetAsync(GpoIndexRefresh.IfMissing, search.Token);
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var fromCleanup = service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        await search.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fromSearch);
        hold.Release();

        var view = await fromCleanup;
        Assert.Same(view, service.Current);
    }

    [Fact]
    public async Task TheLastWaitersCancel_CancelsTheBuild()
    {
        var harness = await IndexHarness.CreateAsync();
        using var service = new GpoSettingsIndexService(harness.Services);
        var hold = harness.Sysvol.HoldNext("Read");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var only = service.GetAsync(GpoIndexRefresh.IfMissing, cancel.Token);
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);
        hold.Release();

        await WaitUntilAsync(() => !service.IsBuilding);
        Assert.Null(service.Current);
        Assert.Equal(0, harness.Sysvol.OpenConnections);
    }

    [Fact]
    public async Task IfMissingRightAfterCancelBuild_StartsAFollowUp_InsteadOfJoiningTheCancelledBuild()
    {
        var harness = await IndexHarness.CreateAsync();
        using var service = new GpoSettingsIndexService(harness.Services);
        var hold = harness.Sysvol.HoldNext("Read");

        var cancelled = service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        service.CancelBuild();
        var again = service.GetAsync(GpoIndexRefresh.IfMissing, Ct); // the cancelled build is still winding down
        hold.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var view = await again;
        Assert.Same(view, service.Current);
        Assert.Equal(2, harness.Sysvol.OpenedOn.Count);
        await WaitUntilAsync(() => !service.IsBuilding);
        Assert.Equal(0, harness.Sysvol.OpenConnections);
    }

    [Fact]
    public async Task RefreshesDuringABuild_QueueExactlyOneFollowUp()
    {
        var harness = await IndexHarness.CreateAsync();
        using var service = new GpoSettingsIndexService(harness.Services);
        var hold = harness.Sysvol.HoldNext("Read");

        var first = service.GetAsync(GpoIndexRefresh.IfMissing, Ct);
        await hold.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var incremental = service.GetAsync(GpoIndexRefresh.Incremental, Ct);
        var full = service.GetAsync(GpoIndexRefresh.Full, Ct);
        var again = service.GetAsync(GpoIndexRefresh.Incremental, Ct);
        hold.Release();

        var firstView = await first;
        var followUp = await incremental;
        Assert.NotSame(firstView, followUp);
        Assert.Same(followUp, await full);
        Assert.Same(followUp, await again);
        Assert.Same(followUp, service.Current);
        Assert.Equal(2, harness.Sysvol.OpenedOn.Count); // two builds, each with its own connection
        Assert.Equal(14, harness.Sysvol.Calls.Count(c => c.Operation == "List")); // the follow-up was Full: every folder again
        Assert.Equal(0, harness.Sysvol.OpenConnections);
    }

    [Fact]
    public async Task WithoutSysvol_TheViewSaysWhy()
    {
        var harness = await IndexHarness.CreateAsync();
        harness.Sysvol.Unavailable = SysvolUnavailableReason.AccessDenied;
        using var service = new GpoSettingsIndexService(harness.Services);

        var view = await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);

        Assert.Equal(SysvolUnavailableReason.AccessDenied, view.SysvolReason);
        Assert.False(view.Index.SysvolRead);
        Assert.All(view.Index.Entries, e => Assert.Equal(GpoIndexStatus.NotRead, e.Status));
    }

    [Fact]
    public async Task Dispose_UnsubscribesAndRefusesNewBuilds()
    {
        var harness = await IndexHarness.CreateAsync();
        var service = new GpoSettingsIndexService(harness.Services);
        await service.GetAsync(GpoIndexRefresh.IfMissing, Ct);

        service.Dispose();
        harness.Services.NotifyChanged();

        Assert.Null(service.Current);
        Assert.False(service.IsStale);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetAsync(GpoIndexRefresh.IfMissing, Ct));
    }

    /// <summary>
    /// The Group Policy services of a window connected to a <see cref="FakeDomainControllerSession"/> of dc01 (so that a DC
    /// switch or a reconnect closes that session, never the shared directory), with the Polish GPO management sample.
    /// </summary>
    private sealed class IndexHarness
    {
        private IndexHarness(B2Harness b2)
        {
            B2 = b2;
            Sample = b2.Builder.AddGroupPolicyManagementSample(b2.Company, Sysvol);
            Services = new GroupPolicyServices(b2.Services, b2.Engines, new GroupPolicyPlanner(new GroupPolicyReader(), b2.Time), new ScriptedGroupPolicyDialogs(),
                Sysvol, definitions: new PolicyDefinitionsService(new TestLocalPolicyDefinitions()));
        }

        public B2Harness B2 { get; }

        public FakeSysvol Sysvol { get; } = new();

        public GroupPolicyManagementSample Sample { get; }

        public GroupPolicyServices Services { get; }

        public static async Task<IndexHarness> CreateAsync() => new(await B2Harness.CreateOnDomainControllerAsync("dc01.contoso.test", Polish));

        public void AddCentralStore()
        {
            foreach (var (path, content) in SampleAdministrativeTemplates.Files(polish: true))
            {
                Sysvol.PutPolicyDefinition(path, content);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 600 && !condition(); i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(condition());
    }
}
