using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Tests.Rules.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class RuleEvaluationJobTests
{
    private readonly FakeEventStore _store = new();
    private readonly FakeConfigProvider _config = new();
    private readonly FixedTokenSource _tokens = new(Tokens);
    private readonly FakeSignals _signals = new();
    private readonly RecordingPlanner _planner = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private RuleEvaluationJob CreateJob(IEventStore? store = null) =>
        new(store ?? _store, new RuleEngine(_config, _tokens, _clock), _planner, _config, _tokens, _signals, _clock, NullLogger<RuleEvaluationJob>.Instance);

    [Fact]
    public void JobDescription_FollowsSection13_1()
    {
        var job = CreateJob();

        Assert.Equal("rules", job.Name);
        Assert.Equal(TimeSpan.FromSeconds(30), job.DefaultInterval);
        Assert.Equal([WatchdogSignal.EventsStored, WatchdogSignal.AttributionUpdated, WatchdogSignal.ConfigChanged], job.WakeSignals);
    }

    [Fact]
    public async Task RunOnce_EvaluatesUnevaluatedEvents_AndCommitsEvaluationsWithNotifications()
    {
        var created = _store.Add(Create(WatchEventKind.UserCreated));
        var logon = _store.Add(Create(WatchEventKind.AttributesChanged, changedAttributes: ["profilePath"]));
        var already = _store.Add(Create(WatchEventKind.GroupCreated), evaluated: true);

        var result = await CreateJob().RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal(TimeSpan.FromSeconds(30), result.NextRunIn);
        Assert.Equal(1, _store.CommitCalls);
        Assert.Equal([created.Id, logon.Id], _store.Evaluations.Select(e => e.EventId));
        Assert.Equal([DefaultRules.LogonSettings, DefaultRules.AccountAttributes], _store.Evaluations[1].MatchedRules);
        Assert.Equal(Severity.Medium, _store.Get(logon.Id).Severity);
        Assert.Equal([DefaultRules.NewAccounts, DefaultRules.LogonSettings], _store.Notifications.Select(n => n.RuleId));
        Assert.Equal([WatchdogSignal.NotificationsQueued], _signals.Raised);
        Assert.Equal(2, _planner.Calls.Count);
        Assert.Same(_config.Current.Configuration, _planner.Calls[0].Configuration);
        Assert.Empty(_store.Get(already.Id).MatchedRules);
        Assert.Equal(1, _tokens.Calls);
    }

    [Fact]
    public async Task RunOnce_WithoutEvents_CommitsNothing()
    {
        var result = await CreateJob().RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal(0, _store.CommitCalls);
        Assert.Empty(_signals.Raised);
    }

    [Fact]
    public async Task RunOnce_DigestOnlyMatches_RaiseNoSignal()
    {
        _store.Add(Create(WatchEventKind.GroupCreated, "CN=Księgowość,OU=Grupy,DC=contoso,DC=test", WatchObjectKind.Group));

        await CreateJob().RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal([DefaultRules.GroupChanges], Assert.Single(_store.Evaluations).MatchedRules);
        Assert.Empty(_store.Notifications);
        Assert.Empty(_signals.Raised);
    }

    [Fact]
    public async Task RunOnce_AFullBatch_RunsAgainAtOnce()
    {
        for (var i = 0; i < RuleEvaluationJob.BatchSize + 3; i++)
        {
            _store.Add(Create(WatchEventKind.ObjectRenamed));
        }

        var job = CreateJob();
        var first = await job.RunOnceAsync(TestContext.Current.CancellationToken);
        var second = await job.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.Zero, first.NextRunIn);
        Assert.Equal(TimeSpan.FromSeconds(30), second.NextRunIn);
        Assert.Equal(RuleEvaluationJob.BatchSize + 3, _store.Evaluations.Count);
    }

    [Fact]
    public async Task RunOnce_StoreFailure_ReturnsFailedWithoutThrowing()
    {
        var result = await CreateJob(new FailingEventStore()).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Failed);
        Assert.Equal("InvalidOperationException", result.FailureCode);
    }

    [Fact]
    public async Task RunOnce_Cancellation_Propagates()
    {
        _store.Add(Create(WatchEventKind.UserCreated));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateJob(new CancellingEventStore()).RunOnceAsync(cts.Token));
    }

    private sealed class FailingEventStore : DelegatingEventStore
    {
        public override Task<IReadOnlyList<WatchEvent>> GetUnevaluatedAsync(int limit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("baza niedostępna: CN=Łukasz Wiśniewski");
    }

    private sealed class CancellingEventStore : DelegatingEventStore
    {
        public override Task<IReadOnlyList<WatchEvent>> GetUnevaluatedAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromCanceled<IReadOnlyList<WatchEvent>>(cancellationToken);
    }
}
