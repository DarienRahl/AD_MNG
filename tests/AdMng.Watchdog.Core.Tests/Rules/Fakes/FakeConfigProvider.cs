using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Tests.Rules.Fakes;

/// <summary>A configuration provider with a settable current configuration (effective form).</summary>
internal sealed class FakeConfigProvider : IWatchdogConfigProvider
{
    public FakeConfigProvider(WatchdogConfiguration? configuration = null) =>
        Current = new ConfigEnvelope { Version = 1, Configuration = WatchdogConfigProvider.Effective(configuration ?? new DefaultConfigurationFactory().Create()) };

    public ConfigEnvelope Current { get; private set; }

    public event EventHandler<ConfigEnvelope>? Changed;

    public void Set(WatchdogConfiguration configuration)
    {
        Current = new ConfigEnvelope { Version = Current.Version + 1, Configuration = WatchdogConfigProvider.Effective(configuration) };
        Changed?.Invoke(this, Current);
    }

    public Task<ConfigEnvelope> ReloadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
}
