using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Testing.Scenarios;

/// <summary>Mutable <see cref="IWatchdogConfigProvider"/>: <see cref="Update"/> stores a new version and raises
/// <see cref="Changed"/>.</summary>
public sealed class ScenarioConfigProvider : IWatchdogConfigProvider
{
    private readonly Lock _gate = new();
    private ConfigEnvelope _current;

    public ScenarioConfigProvider(WatchdogConfiguration? configuration = null)
    {
        _current = new ConfigEnvelope { Version = 1, Configuration = configuration ?? new WatchdogConfiguration() };
    }

    public ConfigEnvelope Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<ConfigEnvelope>? Changed;

    /// <summary>The current configuration.</summary>
    public WatchdogConfiguration Configuration => Current.Configuration;

    public Task<ConfigEnvelope> ReloadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

    /// <summary>Replaces the configuration (version + 1) and raises <see cref="Changed"/>.</summary>
    public void Update(Func<WatchdogConfiguration, WatchdogConfiguration> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ConfigEnvelope updated;
        lock (_gate)
        {
            updated = _current with { Version = _current.Version + 1, Configuration = change(_current.Configuration) };
            _current = updated;
        }

        Changed?.Invoke(this, updated);
    }
}
