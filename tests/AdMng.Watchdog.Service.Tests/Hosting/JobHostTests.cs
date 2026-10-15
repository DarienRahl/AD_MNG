using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>The job loop (spec 19.1): wait intervals on the clock, wake-up signals, backoff and the fatal path.</summary>
public sealed class JobHostTests : IDisposable
{
    private readonly TestClock _clock = new(TestStore.Start);
    private readonly WatchdogSignals _signals;
    private readonly FakeProcessTerminator _terminator = new();
    private readonly TestLog _log = new();
    private readonly FatalErrorHandler _fatal;
    private readonly List<JobHost> _hosts = [];

    public JobHostTests()
    {
        _signals = new WatchdogSignals(_clock);
        _fatal = new FatalErrorHandler(_terminator, _clock, _log.For<FatalErrorHandler>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunsAtOnce_ThenAfterTheDefaultInterval()
    {
        var job = new ScriptedJob("digest", TimeSpan.FromMinutes(5), []);
        var waiting = _clock.WaitForTimerAsync(TimeSpan.FromMinutes(5));
        await StartAsync(job);

        Assert.Equal(1, await job.NextIterationAsync(Ct));
        await waiting.WaitAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(2, await job.NextIterationAsync(Ct));
    }

    [Fact]
    public async Task NextRunIn_OverridesTheDefaultInterval()
    {
        var job = new ScriptedJob("outbox", TimeSpan.FromMinutes(5), []) { Script = _ => new JobResult(TimeSpan.FromSeconds(7)) };
        var waiting = _clock.WaitForTimerAsync(TimeSpan.FromSeconds(7));
        await StartAsync(job);

        await job.NextIterationAsync(Ct);
        await waiting.WaitAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(7));

        Assert.Equal(2, await job.NextIterationAsync(Ct));
    }

    [Fact]
    public async Task ASubscribedSignal_EndsTheWaitEarly()
    {
        var job = new ScriptedJob("dispatch", TimeSpan.FromHours(1), [WatchdogSignal.NotificationsQueued]);
        var waiting = _clock.WaitForTimerAsync(TimeSpan.FromHours(1));
        await StartAsync(job);
        await job.NextIterationAsync(Ct);
        await waiting.WaitAsync(Ct);

        _signals.Raise(WatchdogSignal.ConfigChanged);       // not subscribed
        _signals.Raise(WatchdogSignal.NotificationsQueued);

        Assert.Equal(2, await job.NextIterationAsync(Ct));
        Assert.Equal(TestStore.Start, _clock.GetUtcNow());
    }

    [Fact]
    public async Task UnexpectedExceptions_BackOffExponentially_WithoutLeakingTheMessage()
    {
        var job = new ScriptedJob("attribution", TimeSpan.FromMinutes(1), [])
        {
            Script = i => i <= 3 ? throw new InvalidOperationException("Konto Zofia Żak (CN=Zofia Żak,OU=Księgowość)") : new JobResult(null),
        };
        var first = _clock.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        await StartAsync(job);

        await job.NextIterationAsync(Ct);
        await first.WaitAsync(Ct);
        var second = _clock.WaitForTimerAsync(TimeSpan.FromSeconds(2));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await job.NextIterationAsync(Ct);
        await second.WaitAsync(Ct);
        var third = _clock.WaitForTimerAsync(TimeSpan.FromSeconds(4));
        _clock.Advance(TimeSpan.FromSeconds(2));
        await job.NextIterationAsync(Ct);
        await third.WaitAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(4, await job.NextIterationAsync(Ct));
        Assert.Null(_terminator.ExitCode);
        var text = _log.Text;
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Zofia", text, StringComparison.Ordinal);
    }

    /// <summary>A DC outage over a weekend: the detection job fails every cycle. Warning 2300 is written when the failure
    /// starts, repeated at most every <see cref="JobHost.FailureLogRepeat"/>, and the recovery is noted once.</summary>
    [Fact]
    public async Task RepeatedFailedIterations_AreLoggedOnTransitionsOnly()
    {
        var job = new ScriptedJob("detection", TimeSpan.FromSeconds(15), [])
        {
            Script = i => i <= 300 ? new JobResult(null, Failed: true, FailureCode: "ServerDown") : new JobResult(null),
        };
        var waiting = _clock.WaitForTimerAsync(TimeSpan.FromSeconds(15));
        await StartAsync(job);

        for (var i = 1; i <= 301; i++)
        {
            Assert.Equal(i, await job.NextIterationAsync(Ct));
            await waiting.WaitAsync(Ct);
            waiting = _clock.WaitForTimerAsync(TimeSpan.FromSeconds(15));
            _clock.Advance(TimeSpan.FromSeconds(15));
        }

        // 300 failures over 75 minutes: the first one and one reminder after an hour.
        var failures = _log.Entries.Where(e => e.EventId.Id == 2300).ToList();
        Assert.Equal(2, failures.Count);
        Assert.All(failures, e => Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, e.Level));
        Assert.Contains("ServerDown", failures[0].Values, StringComparison.Ordinal);
        Assert.Single(_log.Entries, e => e.EventId.Id == 2301);
    }

    [Fact]
    public void Backoff_IsCappedAtFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), JobHost.InitialBackoff);
        Assert.Equal(TimeSpan.FromMinutes(5), JobHost.MaxBackoff);
    }

    [Fact]
    public async Task DetectionFailingForMoreThanAnHour_ExitsWithCode1()
    {
        var job = new ScriptedJob(FatalErrorHandler.WatchedJob, TimeSpan.FromSeconds(15), [])
        {
            Script = _ => throw new TimeoutException("DC01 nie odpowiada"),
        };
        var backoff = _clock.WaitForAnyTimerAsync();
        await StartAsync(job);

        while (!_terminator.Exited.IsCompleted)
        {
            await backoff.WaitAsync(Ct);
            backoff = _clock.WaitForAnyTimerAsync();
            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Equal(1, await _terminator.Exited);
        Assert.True(_clock.GetUtcNow() - TestStore.Start > FatalErrorHandler.Threshold);
        Assert.Contains(_log.Entries, e => e.EventId.Id == EventLogIds.FatalStartupCheck);
    }

    [Fact]
    public void OtherJobs_NeverEndTheProcess_AndASuccessResetsTheClock()
    {
        _fatal.ReportFailure("outbox");
        _clock.Advance(TimeSpan.FromHours(2));
        _fatal.ReportFailure("outbox");

        _fatal.ReportFailure(FatalErrorHandler.WatchedJob);
        _clock.Advance(TimeSpan.FromMinutes(59));
        _fatal.ReportSuccess(FatalErrorHandler.WatchedJob);
        _clock.Advance(TimeSpan.FromMinutes(59));
        _fatal.ReportFailure(FatalErrorHandler.WatchedJob);
        _clock.Advance(TimeSpan.FromMinutes(59));
        _fatal.ReportFailure(FatalErrorHandler.WatchedJob);

        Assert.Null(_terminator.ExitCode);
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            host.Dispose();
        }
    }

    private async Task StartAsync(ScriptedJob job)
    {
        var host = new JobHost(job, _signals, _fatal, _clock, _log.For<JobHost>());
        _hosts.Add(host);
        await host.StartAsync(Ct);
    }
}
