using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Tests.Notifications.Fakes;

/// <summary>An <see cref="IWatchdogConfigProvider"/> holding one configuration that tests can replace.</summary>
internal sealed class FakeConfigProvider(WatchdogConfiguration configuration) : IWatchdogConfigProvider
{
    public ConfigEnvelope Current { get; private set; } = new() { Version = 1, Configuration = configuration };

    public event EventHandler<ConfigEnvelope>? Changed;

    public void Set(WatchdogConfiguration configuration)
    {
        Current = new ConfigEnvelope { Version = Current.Version + 1, Configuration = configuration };
        Changed?.Invoke(this, Current);
    }

    public Task<ConfigEnvelope> ReloadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
}
