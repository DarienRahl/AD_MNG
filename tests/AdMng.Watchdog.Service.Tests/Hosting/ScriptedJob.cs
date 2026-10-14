using System.Threading.Channels;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>A job whose iterations follow a script (result or exception per call) and report themselves.</summary>
public sealed class ScriptedJob(string name, TimeSpan defaultInterval, IReadOnlyList<WatchdogSignal> wakeSignals) : IWatchdogJob
{
    private readonly Channel<int> _iterations = Channel.CreateUnbounded<int>();
    private int _count;

    public string Name => name;

    public TimeSpan DefaultInterval => defaultInterval;

    public IReadOnlyList<WatchdogSignal> WakeSignals => wakeSignals;

    /// <summary>Called with the 1-based iteration number; throw to simulate an unexpected failure.</summary>
    public Func<int, JobResult> Script { get; set; } = _ => new JobResult(null);

    public int Count => Volatile.Read(ref _count);

    public Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var iteration = Interlocked.Increment(ref _count);
        try
        {
            return Task.FromResult(Script(iteration));
        }
        finally
        {
            _iterations.Writer.TryWrite(iteration);
        }
    }

    /// <summary>Waits for the next iteration and returns its number.</summary>
    public async Task<int> NextIterationAsync(CancellationToken cancellationToken) =>
        await _iterations.Reader.ReadAsync(cancellationToken);
}
