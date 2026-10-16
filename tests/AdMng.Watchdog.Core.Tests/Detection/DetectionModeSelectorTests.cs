using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>Every row of the mode table (section 10.3), first match wins.</summary>
public class DetectionModeSelectorTests
{
    private const string Dc01 = "CN=NTDS Settings,CN=DC01,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=contoso,DC=test";
    private const string Dc02 = "CN=NTDS Settings,CN=DC02,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=contoso,DC=test";
    private static readonly Guid Invocation01 = new("845842f4-6f6d-4e7a-a5cd-4f58276592e5");
    private static readonly Guid Invocation02 = new("b0c1d2e3-0000-4000-8000-000000000002");
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly TimeOnly ReconciliationTime = new(2, 30);

    // 14 Oct 2026, 10:00 in Warsaw (UTC+2): after today's 02:30 reconciliation moment.
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private readonly DetectionModeSelector _selector = new();

    [Fact]
    public void NoState_IsBaseline()
    {
        var decision = _selector.Select(null, null, Dc(Dc01, Invocation01, 100), Now, Warsaw, ReconciliationTime);

        Assert.Equal(new ModeDecision(DetectionMode.Baseline, false, DetectionModeSelector.ReasonBaseline, 0), decision);
    }

    [Fact]
    public void BaselineNotCompleted_IsBaselineAgain()
    {
        var state = State(Dc01, Invocation01, 90) with { BaselineCompletedAt = null };

        Assert.Equal(DetectionMode.Baseline, _selector.Select(state, null, Dc(Dc01, Invocation01, 100), Now, Warsaw, ReconciliationTime).Mode);
    }

    [Fact]
    public void NotSynchronized_SkipsWithTheStoredWatermark()
    {
        var decision = _selector.Select(State(Dc01, Invocation01, 90), null, Dc(Dc01, Invocation01, 100) with { IsSynchronized = false }, Now, Warsaw, ReconciliationTime);

        Assert.True(decision.Skip);
        Assert.Equal(DetectionModeSelector.ReasonNotSynchronized, decision.Reason);
        Assert.Equal(90, decision.Watermark);
    }

    [Fact]
    public void KnownDcWithValidOwnWatermark_IsFailbackFromThatWatermark()
    {
        var known = new DcWatermark(Dc02, Invocation02, 5_000, Now.AddDays(-1));

        var decision = _selector.Select(State(Dc01, Invocation01, 90), known, Dc(Dc02, Invocation02, 5_100), Now, Warsaw, ReconciliationTime);

        Assert.Equal(DetectionMode.Failback, decision.Mode);
        Assert.Equal(5_000, decision.Watermark);
    }

    [Fact]
    public void KnownDcRolledBack_IsResync()
    {
        var known = new DcWatermark(Dc02, Invocation02, 5_000, Now.AddDays(-1));

        var decision = _selector.Select(State(Dc01, Invocation01, 90), known, Dc(Dc02, Invocation02, 4_000), Now, Warsaw, ReconciliationTime);

        Assert.Equal(DetectionMode.Resync, decision.Mode);
        Assert.Equal(DetectionModeSelector.ReasonDcChanged, decision.Reason);
    }

    [Fact]
    public void KnownDcWithAnotherInvocationId_IsResync()
    {
        var known = new DcWatermark(Dc02, Guid.NewGuid(), 5_000, Now.AddDays(-1));

        Assert.Equal(DetectionMode.Resync, _selector.Select(State(Dc01, Invocation01, 90), known, Dc(Dc02, Invocation02, 5_100), Now, Warsaw, ReconciliationTime).Mode);
    }

    [Fact]
    public void NeverSeenDc_IsResyncDcChanged()
    {
        var decision = _selector.Select(State(Dc01, Invocation01, 90), null, Dc(Dc02, Invocation02, 100), Now, Warsaw, ReconciliationTime);

        Assert.Equal(DetectionMode.Resync, decision.Mode);
        Assert.Equal(DetectionModeSelector.ReasonDcChanged, decision.Reason);
    }

    [Fact]
    public void DsServiceNameComparison_IsCaseInsensitive()
    {
        var decision = _selector.Select(State(Dc01, Invocation01, 100), null, Dc(Dc01.ToLowerInvariant(), Invocation01, 100), Now, Warsaw, ReconciliationTime);

        Assert.Equal(DetectionMode.NoChange, decision.Mode);
    }

    [Fact]
    public void InvocationIdChanged_IsResync()
    {
        var decision = _selector.Select(State(Dc01, Invocation01, 90), null, Dc(Dc01, Guid.NewGuid(), 100), Now, Warsaw, ReconciliationTime);

        Assert.Equal(DetectionMode.Resync, decision.Mode);
        Assert.Equal(DetectionModeSelector.ReasonInvocationIdChanged, decision.Reason);
    }

    [Fact]
    public void UsnBelowTheWatermark_IsResyncUsnRollback()
    {
        var decision = _selector.Select(State(Dc01, Invocation01, 90), null, Dc(Dc01, Invocation01, 80), Now, Warsaw, ReconciliationTime);

        Assert.Equal(DetectionMode.Resync, decision.Mode);
        Assert.Equal(DetectionModeSelector.ReasonUsnRollback, decision.Reason);
    }

    [Fact]
    public void AfterTheReconciliationTime_IsReconciliationOncePerDay()
    {
        var due = State(Dc01, Invocation01, 90) with { LastFullSyncAt = Now.AddDays(-1) };
        var done = State(Dc01, Invocation01, 90) with { LastFullSyncAt = new DateTimeOffset(2026, 10, 14, 0, 31, 0, TimeSpan.Zero) };

        Assert.Equal(DetectionMode.Reconciliation, _selector.Select(due, null, Dc(Dc01, Invocation01, 90), Now, Warsaw, ReconciliationTime).Mode);
        Assert.Equal(DetectionMode.NoChange, _selector.Select(done, null, Dc(Dc01, Invocation01, 90), Now, Warsaw, ReconciliationTime).Mode);
    }

    [Fact]
    public void BeforeTheReconciliationTime_IsNotDue()
    {
        // 01:00 local on 14 Oct: today's moment (02:30) has not come yet; yesterday's was covered.
        var early = new DateTimeOffset(2026, 10, 13, 23, 0, 0, TimeSpan.Zero);
        var state = State(Dc01, Invocation01, 90) with { LastFullSyncAt = new DateTimeOffset(2026, 10, 13, 0, 40, 0, TimeSpan.Zero) };

        Assert.False(DetectionModeSelector.IsReconciliationDue(state.LastFullSyncAt, early, Warsaw, ReconciliationTime));
    }

    [Fact]
    public void ReconciliationTimeInTheSpringGap_IsShiftedByAnHour()
    {
        // 29 Mar 2026: 02:30 does not exist in Warsaw (clocks jump 02:00 → 03:00).
        var lastSync = new DateTimeOffset(2026, 3, 28, 1, 0, 0, TimeSpan.Zero);

        Assert.False(DetectionModeSelector.IsReconciliationDue(lastSync, new DateTimeOffset(2026, 3, 29, 0, 45, 0, TimeSpan.Zero), Warsaw, ReconciliationTime));
        Assert.True(DetectionModeSelector.IsReconciliationDue(lastSync, new DateTimeOffset(2026, 3, 29, 1, 31, 0, TimeSpan.Zero), Warsaw, ReconciliationTime));
    }

    [Fact]
    public void SameUsn_IsNoChange_AndHigherUsnIncremental()
    {
        var state = State(Dc01, Invocation01, 90);

        Assert.Equal(DetectionMode.NoChange, _selector.Select(state, null, Dc(Dc01, Invocation01, 90), Now, Warsaw, ReconciliationTime).Mode);
        var incremental = _selector.Select(state, null, Dc(Dc01, Invocation01, 95), Now, Warsaw, ReconciliationTime);
        Assert.Equal(new ModeDecision(DetectionMode.Incremental, false, null, 90), incremental);
    }

    private static DcState State(string dsServiceName, Guid invocationId, long usn) => new()
    {
        Domain = "contoso.test",
        DcHostName = "dc01.contoso.test",
        DsServiceName = dsServiceName,
        InvocationId = invocationId,
        HighestUsn = usn,
        BaselineCompletedAt = Now.AddDays(-7),
        LastFullSyncAt = Now.AddHours(-1),
    };

    private static DcSnapshot Dc(string dsServiceName, Guid invocationId, long usn) => new()
    {
        HighestUsn = usn,
        DsServiceName = dsServiceName,
        DnsHostName = "dc.contoso.test",
        InvocationId = invocationId,
        Facts = new DirectoryFacts
        {
            DomainDn = "DC=contoso,DC=test",
            DomainDnsName = "contoso.test",
            ConfigurationDn = "CN=Configuration,DC=contoso,DC=test",
            SchemaDn = "CN=Schema,CN=Configuration,DC=contoso,DC=test",
        },
        Containers = WellKnownContainers.Parse([], "DC=contoso,DC=test"),
    };
}
