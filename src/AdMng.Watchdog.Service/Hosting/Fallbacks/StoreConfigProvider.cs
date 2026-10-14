using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Hosting.Fallbacks;

/// <summary>
/// <see cref="IWatchdogConfigProvider"/> used only when the Core registration provides none: the stored envelope, or
/// version 0 with <see cref="IDefaultConfigurationFactory"/> defaults before the first save. Loaded on first use (after
/// the start-up migrations) and on <see cref="ReloadAsync"/>.
/// </summary>
public sealed class StoreConfigProvider(IConfigStore store, IDefaultConfigurationFactory defaults) : IWatchdogConfigProvider
{
    private readonly Lock _gate = new();
    private ConfigEnvelope? _current;

    public event EventHandler<ConfigEnvelope>? Changed;

    public ConfigEnvelope Current
    {
        get
        {
            lock (_gate)
            {
                if (_current is not null)
                {
                    return _current;
                }
            }

            var loaded = Load(store.GetAsync(CancellationToken.None).GetAwaiter().GetResult());
            lock (_gate)
            {
                _current ??= loaded;
                return _current;
            }
        }
    }

    public async Task<ConfigEnvelope> ReloadAsync(CancellationToken cancellationToken)
    {
        var loaded = Load(await store.GetAsync(cancellationToken).ConfigureAwait(false));
        lock (_gate)
        {
            _current = loaded;
        }

        Changed?.Invoke(this, loaded);
        return loaded;
    }

    private ConfigEnvelope Load(ConfigEnvelope? stored) =>
        stored ?? new ConfigEnvelope { Version = 0, Configuration = defaults.Create() };
}
