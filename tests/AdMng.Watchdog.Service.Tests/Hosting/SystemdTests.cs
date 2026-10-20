using System.Net.Sockets;
using System.Text;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>The service as a systemd unit (stage B design 12.1, 15): detection, sd_notify, the watchdog notifier and the
/// job progress it follows, journald logging.</summary>
public sealed class SystemdTests
{
    private static readonly DateTimeOffset Start = TestStore.Start;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("4f1c", null, 1, true)]
    [InlineData(null, "/run/systemd/notify", 1, true)]
    [InlineData("4f1c", null, 4711, false)]   // a CI step below a runner unit inherits INVOCATION_ID
    [InlineData(null, null, 1, false)]
    [InlineData("4f1c", "/run/systemd/notify", null, false)]
    public void Detection_NeedsTheUnitEnvironment_AndPid1AsParent(string? invocationId, string? notifySocket, int? parent, bool expected)
    {
        var environment = new Dictionary<string, string?>
        {
            [SystemdSetup.InvocationIdVariable] = invocationId,
            [SystemdSetup.NotifySocketVariable] = notifySocket,
        };

        Assert.Equal(expected, SystemdSetup.IsSystemdService(name => environment.GetValueOrDefault(name), parent));
    }

    [Fact]
    public void ParentProcessId_IsReadAfterTheCommandName()
    {
        using var directory = new TestDataDirectory();
        var stat = directory.File("stat");
        File.WriteAllText(stat, "4242 (Ad Mng) (x)) S 1 4242 4242 0 -1 4194560 3460 0 0 0");

        Assert.Equal(1, SystemdSetup.ParentProcessId(stat));
        Assert.Null(SystemdSetup.ParentProcessId(directory.File("missing")));
    }

    [Theory]
    [InlineData("120000000", null, 30)]        // WatchdogSec=120s: every 30 s (a quarter, at most 30 s)
    [InlineData("600000000", null, 30)]
    [InlineData("20000000", null, 5)]
    [InlineData("2000000", null, 1)]           // never below 1 s
    [InlineData("120000000", "4242", 30)]      // WATCHDOG_PID names this process
    public void WatchdogSettings_PingAtAQuarterOfTheTimeout(string usec, string? pid, int expectedSeconds)
    {
        var environment = new Dictionary<string, string?> { ["WATCHDOG_USEC"] = usec, ["WATCHDOG_PID"] = pid };

        var settings = SystemdWatchdogSettings.FromEnvironment(name => environment.GetValueOrDefault(name), 4242);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), settings.PingInterval);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("0", null)]
    [InlineData("abc", null)]
    [InlineData("120000000", "1")]             // the watchdog belongs to another process
    public void WatchdogSettings_WithoutAWatchdogForThisProcess_AreDisabled(string? usec, string? pid)
    {
        var environment = new Dictionary<string, string?> { ["WATCHDOG_USEC"] = usec, ["WATCHDOG_PID"] = pid };

        Assert.Null(SystemdWatchdogSettings.FromEnvironment(name => environment.GetValueOrDefault(name), 4242).PingInterval);
    }

    [Fact]
    public void JobProgress_IsStalled_OnlyBeyondItsBudgetOrWaitPlusGrace()
    {
        var running = new JobProgress(JobPhase.Running, Start, TimeSpan.FromHours(1));
        var waiting = new JobProgress(JobPhase.Waiting, Start, TimeSpan.FromMinutes(5));

        Assert.False(new JobProgress(JobPhase.NotStarted, Start, TimeSpan.Zero).IsStalled(Start.AddDays(1)));
        Assert.False(running.IsStalled(Start.AddMinutes(59)));
        Assert.True(running.IsStalled(Start.AddMinutes(61)));
        Assert.False(waiting.IsStalled(Start.AddMinutes(5) + JobProgress.WaitGrace));
        Assert.True(waiting.IsStalled(Start.AddMinutes(6) + JobProgress.WaitGrace));
        Assert.True(new JobProgress(JobPhase.Stopped, Start, TimeSpan.Zero).IsStalled(Start));
        Assert.False(new JobProgress(JobPhase.Waiting, Start, TimeSpan.MaxValue).IsStalled(Start.AddYears(5)));
        Assert.Equal(TimeSpan.FromHours(6), JobProgress.RunBudget("detection"));
        Assert.Equal(JobProgress.DefaultRunBudget, JobProgress.RunBudget("outbox"));
    }

    [Fact]
    public void Notifier_Pings_WhileEveryJobProgresses_AndStops_WhenOneHangs()
    {
        var clock = new TestClock(Start);
        var notifier = new RecordingNotifier();
        var jobs = new FakeProgress { ["outbox"] = new JobProgress(JobPhase.Waiting, Start, TimeSpan.FromSeconds(30)) };
        jobs["detection"] = new JobProgress(JobPhase.Running, Start, TimeSpan.FromHours(6));
        var log = new TestLog();
        var watchdog = new SystemdWatchdogNotifier(notifier, jobs, new FakeLifetime(), new SystemdWatchdogSettings(TimeSpan.FromSeconds(30)), clock, log.For<SystemdWatchdogNotifier>());

        Assert.True(watchdog.Tick());
        clock.Advance(TimeSpan.FromMinutes(5));
        jobs["outbox"] = new JobProgress(JobPhase.Waiting, clock.GetUtcNow(), TimeSpan.FromSeconds(30));
        Assert.True(watchdog.Tick());

        clock.Advance(TimeSpan.FromHours(6));   // the detection iteration never returns
        jobs["outbox"] = new JobProgress(JobPhase.Waiting, clock.GetUtcNow(), TimeSpan.FromSeconds(30));
        Assert.False(watchdog.Tick());
        Assert.False(watchdog.Tick());

        Assert.Equal(2, notifier.States.Count);
        Assert.All(notifier.States, state => Assert.Equal("WATCHDOG=1", state));
        var stalled = Assert.Single(log.Entries, e => e.EventId.Id == EventLogIds.FatalStartupCheck);
        Assert.Equal(LogLevel.Critical, stalled.Level);
        Assert.Contains("detection", stalled.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notifier_WaitsForTheStartedHost_BeforeTheFirstPing()
    {
        var notifier = new RecordingNotifier();
        var lifetime = new FakeLifetime();
        var watchdog = new SystemdWatchdogNotifier(notifier, new FakeProgress(), lifetime, new SystemdWatchdogSettings(TimeSpan.FromSeconds(1)),
            TimeProvider.System, new TestLog().For<SystemdWatchdogNotifier>());

        await watchdog.StartAsync(Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
        Assert.Empty(notifier.States);

        lifetime.Start();
        for (var i = 0; i < 100 && notifier.States.Count == 0; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        }

        await watchdog.StopAsync(Ct);
        Assert.NotEmpty(notifier.States);
    }

    [Fact]
    public async Task JobHosts_ReportTheirPhase_ToTheNotifier()
    {
        var clock = new TestClock(Start);
        var signals = new WatchdogSignals(clock);
        var log = new TestLog();
        var job = new ScriptedJob("digest", TimeSpan.FromMinutes(5), []);
        using var service = new JobHostService(
            [job], signals, new FatalErrorHandler(new FakeProcessTerminator(), clock, log.For<FatalErrorHandler>()), clock, log.For<JobHost>());
        var waiting = clock.WaitForTimerAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(JobPhase.NotStarted, Assert.Single(service.SnapshotProgress()).Progress.Phase);
        await service.StartAsync(Ct);
        await job.NextIterationAsync(Ct);
        await waiting.WaitAsync(Ct);

        var (name, progress) = Assert.Single(service.SnapshotProgress());
        Assert.Equal("digest", name);
        Assert.Equal(JobPhase.Waiting, progress.Phase);
        Assert.Equal(TimeSpan.FromMinutes(5), progress.Expected);
        await service.StopAsync(Ct);
        Assert.Equal(JobPhase.Stopped, Assert.Single(service.SnapshotProgress()).Progress.Phase);
    }

    [Fact]
    public void NotifySocket_SendsOneDatagramPerState()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "sd_notify is Linux only.");
        using var directory = new TestDataDirectory();
        var path = directory.File("notify");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        var notifier = new SystemdNotifySocket(path);

        notifier.Notify(ServiceState.Ready);
        notifier.Notify(SystemdWatchdogNotifier.Ping);
        notifier.Notify(ServiceState.Stopping);

        var buffer = new byte[64];
        var received = Enumerable.Range(0, 3).Select(_ => Encoding.UTF8.GetString(buffer, 0, listener.Receive(buffer))).ToList();
        Assert.True(notifier.IsEnabled);
        Assert.Equal(["READY=1", "WATCHDOG=1", "STOPPING=1"], received);
        Assert.False(new SystemdNotifySocket(socketPath: null).IsEnabled);
    }

    [Fact]
    public void SystemdRegistration_UsesTheSystemdLifetime_AndTheWatchdog_OnlyAsAUnit()
    {
        var unit = new ServiceCollection();
        SystemdSetup.Configure(unit, isSystemdService: true);
        var console = new ServiceCollection();
        SystemdSetup.Configure(console, isSystemdService: false);

        Assert.Contains(unit, d => d.ServiceType == typeof(IHostLifetime) && d.ImplementationType == typeof(SystemdLifetime));
        Assert.Contains(unit, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SystemdWatchdogNotifier));
        Assert.Empty(console);
    }

    [Fact]
    public void JournaldLogging_UsesTheSystemdFormatter_BehindTheThrottle()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "journald is Linux only (Windows adds the Application log).");
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        LoggingSetup.Configure(builder, console: false, journald: true);
        using var provider = builder.Services.BuildServiceProvider();

        Assert.Equal(ConsoleFormatterNames.Systemd, provider.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName);
        var wrapped = Assert.Single(provider.GetServices<ILoggerProvider>());
        Assert.IsType<RedactingLoggerProvider>(wrapped);
    }

    private sealed class RecordingNotifier : ISystemdNotifier
    {
        private readonly Lock _gate = new();
        private readonly List<string> _states = [];

        public bool IsEnabled => true;

        public IReadOnlyList<string> States
        {
            get
            {
                lock (_gate)
                {
                    return [.. _states];
                }
            }
        }

        public void Notify(ServiceState state)
        {
            lock (_gate)
            {
                _states.Add(state.ToString());
            }
        }
    }

    private sealed class FakeProgress : Dictionary<string, JobProgress>, IJobProgressSource
    {
        public IReadOnlyList<(string Job, JobProgress Progress)> SnapshotProgress() => [.. this.Select(p => (p.Key, p.Value))];
    }

    private sealed class FakeLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Start() => _started.Cancel();

        public void StopApplication()
        {
        }

        public void Dispose() => _started.Dispose();
    }
}
