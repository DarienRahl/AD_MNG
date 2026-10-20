using System.Text;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Testing.Gpo;
using AdMng.Watchdog.Testing.Scenarios;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>
/// <see cref="GpoContentJob"/> (stage B design 8.1–8.8) over the in-memory store and SYSVOL: silent baseline (throttled),
/// GroupPolicyChanged triggers → "was → is" events in the trigger's change set, path redirection without reading, the
/// daily reconciliation finding out-of-band edits, the poison-input guard, SYSVOL lag, deletion and restore, health.
/// </summary>
public sealed class GpoContentJobTests
{
    private const string DomainDn = "DC=contoso,DC=com";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Gpo = GpoFixtures.Gpo;
    private static readonly Guid GpcObject = Guid.Parse("0b5b0000-1111-4222-8333-444455556666");

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly InMemoryWatchdogStore _store;
    private readonly InMemorySysvolReader _sysvol = GpoFixtures.SampleGpo();
    private readonly ScenarioConfigProvider _config = new();
    private readonly HealthRegistry _health = new();
    private readonly ScenarioSignals _signals = new();
    private readonly GpoContentJob _job;

    public GpoContentJobTests()
    {
        _store = new InMemoryWatchdogStore(_clock);

        // Without the GPP cpassword items of the sample (they make every content event Critical – tag gppPassword).
        _sysvol.RemoveFile(Gpo, "Machine/Preferences/Groups/Groups.xml").RemoveFile(Gpo, "Machine/Preferences/ScheduledTasks/ScheduledTasks.xml");
        var sessions = new ScenarioSessionProvider(new FakeDirectorySession(FakeDirectorySession.CreateInfo(GpoFixtures.Domain, GpoFixtures.Dc)));
        _job = new GpoContentJob(_store, _store, _store, _sysvol, sessions, _config, _health, _signals, _clock, NullLogger<GpoContentJob>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string GpcDn(Guid gpo) => "CN={" + gpo.ToString().ToUpperInvariant() + "},CN=Policies,CN=System," + DomainDn;

    [Fact]
    public async Task Baseline_ReadsEveryGpoSilently_TwentyPerRun()
    {
        var others = Enumerable.Range(1, 24).Select(i => new Guid(i, 0, 0, new byte[8])).ToList();
        foreach (var other in others)
        {
            _sysvol.SetFile(other, "GPT.INI", Encoding.ASCII.GetBytes("[General]\r\nVersion=0\r\n"));
        }

        await SeedAsync([Gpc(Gpo, 131075), .. others.Select(o => Gpc(o, 0, Guid.NewGuid()))]);

        await RunAsync(); // first run: the cursor starts at the newest event, 20 GPOs read
        Assert.Equal(20, (await _store.ListGpoContentAsync(false, Ct)).Count);
        Assert.Null(_health.GpoContent!.BaselineCompletedAt);
        Assert.Equal(5, _health.GpoContent.QueuedGpos);

        await RunAsync();
        Assert.Equal(25, (await _store.ListGpoContentAsync(false, Ct)).Count);
        await RunAsync(); // completion is noted once everything has a snapshot
        Assert.Equal(Now, _health.GpoContent!.BaselineCompletedAt);
        Assert.Equal((25, 25, HealthState.Ok), (_health.GpoContent.TotalGpos, _health.GpoContent.ReadGpos, _health.GpoContent.State));
        Assert.Empty(await ContentEventsAsync());
        var sample = (await _store.GetGpoContentAsync(Gpo, Ct))!.Snapshot;
        Assert.Equal((131075, 131075, GpoFixtures.Dc), (sample.GpcVersion, sample.GptVersion, sample.Dc));
        Assert.Contains(sample.Settings, s => s.Key == "Computer/Security/System Access/MinimumPasswordLength");
    }

    [Fact]
    public async Task VersionChange_ReportsWasIs_InTheTriggersChangeSet()
    {
        await BaselineAsync();
        _sysvol.SetFile(Gpo, GpoFixtures.GptTmplPath, GpoFixtures.Utf16(GpoFixtures.Text("GptTmpl.inf").Replace("MinimumPasswordLength = 14", "MinimumPasswordLength = 8", StringComparison.Ordinal)));
        _sysvol.SetFile(Gpo, "GPT.INI", Encoding.ASCII.GetBytes("[General]\r\nVersion=131076\r\n"));
        var changeSet = await GpcChangedAsync(Gpo, 131076, ("versionNumber", "131075", "131076"));

        await RunAsync();

        var content = Assert.Single(await ContentEventsAsync());
        Assert.Equal((WatchEventKind.GroupPolicySettingsChanged, changeSet, Severity.High), (content.Kind, content.ChangeSetId, content.Severity));
        Assert.Equal(AttributionStatus.Pending, content.Attribution.Status);
        Assert.Equal(GpcObject, content.Object.ObjectGuid);
        var row = Assert.Single(content.Changes);
        Assert.Equal(("Computer/Security/System Access/MinimumPasswordLength", "14", "8"), (row.Attribute, row.OldValues.Single(), row.NewValues.Single()));
        Assert.Empty(await _store.GetGpoQueueAsync(Ct));
        Assert.Contains(WatchdogSignal.EventsStored, _signals.Raised);
        Assert.Equal(131076, (await _store.GetGpoContentAsync(Gpo, Ct))!.Snapshot.GpcVersion);
    }

    [Fact]
    public async Task LinkedToTheDomainHead_SensitiveChangeIsCritical()
    {
        await BaselineAsync(domainGpLink: "[LDAP://" + GpcDn(Gpo) + ";0]");
        _sysvol.SetFile(Gpo, GpoFixtures.GptTmplPath, GpoFixtures.Utf16(GpoFixtures.Text("GptTmpl.inf").Replace("MinimumPasswordLength = 14", "MinimumPasswordLength = 8", StringComparison.Ordinal)));
        _sysvol.SetFile(Gpo, "GPT.INI", Encoding.ASCII.GetBytes("[General]\r\nVersion=131076\r\n"));
        await GpcChangedAsync(Gpo, 131076, ("versionNumber", "131075", "131076"));

        await RunAsync();

        Assert.Equal(Severity.Critical, Assert.Single(await ContentEventsAsync()).Severity);
    }

    [Fact]
    public async Task RedirectedPath_IsCritical_AndNothingIsRead()
    {
        await BaselineAsync();
        var calls = _sysvol.Calls.Count;
        var hijack = @"\\evil.example\share\{" + Gpo.ToString().ToUpperInvariant() + "}";
        var changeSet = await GpcChangedAsync(Gpo, 131075, ("gPCFileSysPath", GpoPathValidator.ExpectedPath(GpoFixtures.Domain, Gpo), hijack), path: hijack);

        await RunAsync();
        await RunAsync();

        var redirected = Assert.Single(await ContentEventsAsync());
        Assert.Equal((WatchEventKind.GroupPolicyPathRedirected, Severity.Critical, changeSet), (redirected.Kind, redirected.Severity, redirected.ChangeSetId));
        Assert.Equal([hijack], Assert.Single(redirected.Changes).NewValues);
        Assert.Equal(calls, _sysvol.Calls.Count);

        // While the path stays redirected, version changes are not read and the reconciliation skips the GPO.
        await GpcChangedAsync(Gpo, 131076, ("versionNumber", "131075", "131076"), path: hijack);
        _job.ForceReconciliation();
        await RunAsync();
        await RunAsync();
        Assert.Equal(calls, _sysvol.Calls.Count);
        Assert.Single(await ContentEventsAsync());
    }

    [Fact]
    public async Task OutOfBandEdit_IsFoundByTheReconciliation_DailyOrForced()
    {
        await BaselineAsync();
        _sysvol.SetFile(Gpo, "Machine/Scripts/scripts.ini", GpoFixtures.Utf16("[Startup]\n0CmdLine=złośliwy.cmd\n0Parameters=\n"), Now.AddMinutes(5));

        await RunAsync();
        Assert.Empty(await ContentEventsAsync()); // no versionNumber change: not noticed until the reconciliation

        _job.ForceReconciliation();
        await RunAsync(); // listing differs → queued
        await RunAsync(); // read
        var outOfBand = Assert.Single(await ContentEventsAsync());
        Assert.Equal((WatchEventKind.GroupPolicyContentOutOfBand, Severity.High, DetectionSource.Reconciliation), (outOfBand.Kind, outOfBand.Severity, outOfBand.Source));
        Assert.Equal((AttributionStatus.Unknown, AttributionReasons.SysvolNotAudited), (outOfBand.Attribution.Status, outOfBand.Attribution.Reason));
        Assert.Contains(outOfBand.Changes, c => c.Attribute == "Computer/Scripts/Startup/0" && c.NewValues.SequenceEqual(["złośliwy.cmd"]));

        // The next one runs a day after the last.
        _sysvol.SetFile(Gpo, "Machine/Scripts/scripts.ini", GpoFixtures.Utf16("[Startup]\n0CmdLine=inny.cmd\n0Parameters=\n"), Now.AddMinutes(9));
        _clock.Advance(TimeSpan.FromHours(23));
        await RunAsync();
        await RunAsync();
        Assert.Single(await ContentEventsAsync());
        _clock.Advance(TimeSpan.FromHours(1));
        await RunAsync();
        await RunAsync();
        Assert.Equal(2, (await ContentEventsAsync()).Count);
        Assert.Equal(_clock.GetUtcNow(), _health.GpoContent!.LastReconciliationAt);
    }

    [Fact]
    public async Task PoisonInput_IsParkedAfterThreeAttempts_StoredBeforeEachRead_UntilTheNextChange()
    {
        await SeedAsync([Gpc(Gpo, 131075)]);
        var poisoned = new PoisonedReader(_store, Gpo);
        var sessions = new ScenarioSessionProvider(new FakeDirectorySession(FakeDirectorySession.CreateInfo(GpoFixtures.Domain, GpoFixtures.Dc)));
        var job = new GpoContentJob(_store, _store, _store, poisoned, sessions, _config, _health, _signals, _clock, NullLogger<GpoContentJob>.Instance);

        await job.RunOnceAsync(Ct);
        _clock.Advance(GpoContentJob.RetryDelays[0]);
        await job.RunOnceAsync(Ct);
        _clock.Advance(GpoContentJob.RetryDelays[1]);
        await job.RunOnceAsync(Ct);
        _clock.Advance(TimeSpan.FromHours(1));
        await job.RunOnceAsync(Ct);

        Assert.Equal([1, 2, 3], poisoned.AttemptsSeenAtRead); // stored before every read: a crash counts too
        var parked = Assert.Single(await _store.GetGpoQueueAsync(Ct));
        Assert.True(parked.IsParked);
        Assert.Equal(HealthState.Warning, Check(HealthCheckCodes.Gpo(Gpo, HealthCheckCodes.GpoParseError))!.State);
        Assert.Equal((1, HealthState.Warning), (_health.GpoContent!.FailedGpos, _health.GpoContent.State));
        Assert.NotNull(_health.GpoContent.BaselineCompletedAt); // a parked GPO does not block the baseline

        // The next change of the GPO gets new attempts.
        poisoned.Healthy = true;
        await GpcChangedAsync(Gpo, 131075, ("versionNumber", "131074", "131075"));
        await job.RunOnceAsync(Ct);
        Assert.NotNull(await _store.GetGpoContentAsync(Gpo, Ct));
        Assert.Empty(await _store.GetGpoQueueAsync(Ct));
        Assert.Equal(HealthState.Ok, Check(HealthCheckCodes.Gpo(Gpo, HealthCheckCodes.GpoParseError))!.State);
    }

    [Fact]
    public async Task SysvolLag_IsRetried_ThenReadWithAWarningAfterTheTolerance()
    {
        await BaselineAsync();
        _sysvol.SetFile(Gpo, GpoFixtures.GptTmplPath, GpoFixtures.Utf16(GpoFixtures.Text("GptTmpl.inf").Replace("MinimumPasswordLength = 14", "MinimumPasswordLength = 8", StringComparison.Ordinal)));
        await GpcChangedAsync(Gpo, 131076, ("versionNumber", "131075", "131076")); // GPT.INI still says 131075

        await RunAsync();
        Assert.Empty(await ContentEventsAsync());
        Assert.Equal(1, _health.GpoContent!.SysvolLaggingGpos);
        var queued = Assert.Single(await _store.GetGpoQueueAsync(Ct));
        Assert.Equal((0, _clock.GetUtcNow() + SysvolLagPolicy.RetryInterval), (queued.Attempts, queued.DueAt));

        _clock.Advance(TimeSpan.FromHours(2));
        await RunAsync();

        Assert.Single(await ContentEventsAsync());
        Assert.Equal(HealthState.Warning, Check(SysvolLagPolicy.HealthCode(Gpo))!.State);
        Assert.Equal(0, _health.GpoContent.SysvolLaggingGpos);
    }

    [Fact]
    public async Task AccessDenied_IsAPermissionGap_ReportedOnce()
    {
        await SeedAsync([Gpc(Gpo, 131075)]);
        _sysvol.FailFolder(Gpo, SysvolErrorKind.AccessDenied, "NT_STATUS_ACCESS_DENIED");

        await RunAsync();
        _clock.Advance(GpoContentJob.RetryDelays[0]);
        await RunAsync();

        Assert.Equal(HealthState.Error, Check(HealthCheckCodes.SysvolAccessDenied)!.State);
        var gap = Assert.Single((await _store.QueryAsync(new EventQuery { After = 0, Kinds = [WatchEventKind.HealthPermissionGap] }, Ct)).Items);
        Assert.Equal([HealthCheckCodes.SysvolAccessDenied], gap.Changes.Single(c => c.Attribute == "code").NewValues);
        Assert.Equal(HealthState.Error, _health.GpoContent!.State);
    }

    [Fact]
    public async Task KerberosFailure_StopsTheRun_WithoutCountingAnAttempt()
    {
        await SeedAsync([Gpc(Gpo, 131075)]);
        _sysvol.FailFolder(Gpo, SysvolErrorKind.Authentication, "NT_STATUS_LOGON_FAILURE");

        var result = await _job.RunOnceAsync(Ct);

        Assert.Equal((true, "sysvol"), (result.Failed, result.FailureCode));
        Assert.Equal(0, Assert.Single(await _store.GetGpoQueueAsync(Ct)).Attempts);
        Assert.Equal(HealthState.Error, Check(GpoContentJob.ConnectionHealthCode)!.State);
    }

    [Fact]
    public async Task DeletedGpo_KeepsItsSnapshotForTheRestoreDiff()
    {
        await BaselineAsync();
        await _store.CommitAsync(new DetectionCommit
        {
            Deletions = [(GpcObject, _clock.GetUtcNow())],
            Events =
            [
                Event(WatchEventKind.ObjectDeleted, Gpo) with
                {
                    Object = new ObjectRef { Dn = "CN={" + Gpo.ToString().ToUpperInvariant() + "}\\0ADEL:" + GpcObject + ",CN=Deleted Objects," + DomainDn, Kind = WatchObjectKind.GroupPolicy, ObjectGuid = GpcObject },
                    PreviousDn = GpcDn(Gpo),
                },
            ],
        }, Ct);
        await RunAsync();
        await RunAsync();
        Assert.NotNull((await _store.GetGpoContentAsync(Gpo, Ct))!.DeletedAt);
        Assert.Empty(await _store.GetGpoQueueAsync(Ct));

        // Restored with other content: the diff is against the snapshot kept at the deletion.
        _sysvol.RemoveFile(Gpo, "Machine/Scripts/scripts.ini");
        await _store.CommitAsync(new DetectionCommit { Upserts = [Gpc(Gpo, 131075)], Events = [Event(WatchEventKind.ObjectRestored, Gpo)] }, Ct);
        await RunAsync();

        var restored = Assert.Single(await ContentEventsAsync());
        Assert.Contains(restored.Changes, c => c.Attribute == "Computer/Scripts/Startup/0" && c.NewValues.Count == 0);
        Assert.Null((await _store.GetGpoContentAsync(Gpo, Ct))!.DeletedAt);
    }

    [Fact]
    public async Task Disabled_ReadsNothing()
    {
        await SeedAsync([Gpc(Gpo, 131075)]);
        _config.Update(c => c with { Directory = c.Directory with { GpoContent = new GpoContentSettings { Enabled = false } } });

        await RunAsync();

        Assert.Empty(_sysvol.Calls);
        Assert.Equal((HealthState.Disabled, false), (_health.GpoContent!.State, _health.GpoContent.Enabled));
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    private async Task RunAsync()
    {
        var result = await _job.RunOnceAsync(Ct);
        Assert.False(result.Failed, result.FailureCode);
    }

    private async Task BaselineAsync(string? domainGpLink = null)
    {
        await SeedAsync([Gpc(Gpo, 131075)], domainGpLink);
        await RunAsync();
        await RunAsync();
        Assert.NotNull(_health.GpoContent!.BaselineCompletedAt);
        Assert.Empty(await ContentEventsAsync());
    }

    private Task SeedAsync(IReadOnlyList<SnapshotObject> gpcs, string? domainGpLink = null)
    {
        var domain = new SnapshotObject
        {
            ObjectGuid = Guid.NewGuid(),
            Dn = DomainDn,
            Kind = WatchObjectKind.Domain,
            Attributes = new Dictionary<string, SnapshotAttribute>(StringComparer.OrdinalIgnoreCase)
            {
                ["gPLink"] = new() { Values = [domainGpLink ?? string.Empty] },
            },
        };
        return _store.CommitAsync(new DetectionCommit
        {
            NewState = new DcState
            {
                Domain = GpoFixtures.Domain,
                DcHostName = GpoFixtures.Dc,
                DsServiceName = "CN=NTDS Settings,CN=DC01,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration," + DomainDn,
                InvocationId = Guid.NewGuid(),
                HighestUsn = 1000,
            },
            Upserts = [domain, .. gpcs],
            IsBaseline = true,
        }, Ct);
    }

    private static SnapshotObject Gpc(Guid gpo, int version, Guid? objectGuid = null, string? path = null) => new()
    {
        ObjectGuid = objectGuid ?? GpcObject,
        Dn = GpcDn(gpo),
        Kind = WatchObjectKind.GroupPolicy,
        Name = "{" + gpo.ToString().ToUpperInvariant() + "}",
        DisplayName = "Zasady kontrolerów domeny – Łódź",
        Attributes = new Dictionary<string, SnapshotAttribute>(StringComparer.OrdinalIgnoreCase)
        {
            ["versionNumber"] = new() { Values = [version.ToString(System.Globalization.CultureInfo.InvariantCulture)] },
            ["gPCFileSysPath"] = new() { Values = [path ?? @"\\contoso.com\sysvol\contoso.com\Policies\{" + gpo.ToString().ToUpperInvariant() + "}"] },
        },
    };

    /// <summary>What detection does for a GPMC edit: the new GPC state and a GroupPolicyChanged event.</summary>
    private async Task<Guid> GpcChangedAsync(Guid gpo, int version, (string Attribute, string Old, string New) change, string? path = null)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var changeSet = Guid.NewGuid();
        await _store.CommitAsync(new DetectionCommit
        {
            Upserts = [Gpc(gpo, version, path: path)],
            Events =
            [
                Event(WatchEventKind.GroupPolicyChanged, gpo) with
                {
                    ChangeSetId = changeSet,
                    Changes = [new AttributeChange { Attribute = change.Attribute, OldValues = [change.Old], NewValues = [change.New], OldCount = 1, NewCount = 1 }],
                },
            ],
        }, Ct);
        return changeSet;
    }

    private WatchEvent Event(WatchEventKind kind, Guid gpo) => new()
    {
        EventId = Guid.NewGuid(),
        ChangeSetId = Guid.NewGuid(),
        Kind = kind,
        Severity = Severity.Medium,
        Object = new ObjectRef { Dn = GpcDn(gpo), Kind = WatchObjectKind.GroupPolicy, ObjectGuid = GpcObject },
        DetectedAt = _clock.GetUtcNow(),
        Source = DetectionSource.IncrementalPoll,
    };

    private async Task<IReadOnlyList<WatchEvent>> ContentEventsAsync() =>
        (await _store.QueryAsync(new EventQuery
        {
            After = 0,
            Kinds = [WatchEventKind.GroupPolicySettingsChanged, WatchEventKind.GroupPolicyPathRedirected, WatchEventKind.GroupPolicyContentOutOfBand],
        }, Ct)).Items;

    private HealthCheckDto? Check(string code) => _health.Checks.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>A GPO whose content throws inside the reader (stands for a crash); records the stored attempts at each read.</summary>
    private sealed class PoisonedReader(IGpoContentStore store, Guid gpo) : ISysvolReader
    {
        private readonly InMemorySysvolReader _inner = GpoFixtures.SampleGpo();

        public List<int> AttemptsSeenAtRead { get; } = [];

        public bool Healthy { get; set; }

        public async Task<SysvolListing> ListAsync(SysvolGpoFolder folder, CancellationToken cancellationToken)
        {
            if (!Healthy)
            {
                AttemptsSeenAtRead.Add((await store.GetGpoQueueAsync(cancellationToken)).Single(q => q.GpoGuid == gpo).Attempts);
                throw new InvalidOperationException("parser bug");
            }

            return await _inner.ListAsync(folder, cancellationToken);
        }

        public Task<IReadOnlyList<SysvolFile>> ReadAsync(SysvolGpoFolder folder, IReadOnlyList<string> relativePaths, long maxBytesPerFile,
            CancellationToken cancellationToken) => _inner.ReadAsync(folder, relativePaths, maxBytesPerFile, cancellationToken);
    }
}
