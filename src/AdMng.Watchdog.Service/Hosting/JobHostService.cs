using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Starts one <see cref="JobHost"/> per registered <see cref="IWatchdogJob"/> (the Core jobs from
/// <c>AddWatchdogCore()</c> plus the service's own) and stops them with the host.</summary>
public sealed class JobHostService : IHostedService, IDisposable
{
    private readonly IReadOnlyList<JobHost> _hosts;

    public JobHostService(
        IEnumerable<IWatchdogJob> jobs,
        IWatchdogSignals signals,
        FatalErrorHandler fatal,
        TimeProvider timeProvider,
        ILogger<JobHost> logger)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        _hosts = [.. jobs.Select(job => new JobHost(job, signals, fatal, timeProvider, logger))];
    }

    /// <summary>The running hosts (tests, diagnostics).</summary>
    public IReadOnlyList<JobHost> Hosts => _hosts;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var host in _hosts)
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_hosts.Select(h => h.StopAsync(cancellationToken))).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }
}
