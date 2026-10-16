using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Tests.Fakes;

/// <summary>A configuration provider holding one envelope (tests set it directly).</summary>
public sealed class FakeConfigProvider : IWatchdogConfigProvider
{
    public FakeConfigProvider(WatchdogConfiguration? configuration = null)
    {
        Current = new ConfigEnvelope { Version = 1, Configuration = configuration ?? new WatchdogConfiguration() };
    }

    public ConfigEnvelope Current { get; private set; }

    public int Reloads { get; private set; }

    /// <summary>Optional source for <see cref="ReloadAsync"/> (e.g. the store under test).</summary>
    public Func<CancellationToken, Task<ConfigEnvelope?>>? Source { get; set; }

    public event EventHandler<ConfigEnvelope>? Changed;

    public void Set(WatchdogConfiguration configuration, long version = 1) =>
        Current = new ConfigEnvelope { Version = version, Configuration = configuration };

    public async Task<ConfigEnvelope> ReloadAsync(CancellationToken cancellationToken)
    {
        Reloads++;
        if (Source is not null && await Source(cancellationToken) is { } loaded)
        {
            Current = loaded;
        }

        Changed?.Invoke(this, Current);
        return Current;
    }
}
