using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>The effective runtime configuration (cached; reloaded after a save).</summary>
public interface IWatchdogConfigProvider
{
    /// <summary>Effective configuration (defaults merged, tokens NOT expanded). Never null after startup.</summary>
    ConfigEnvelope Current { get; }
    Task<ConfigEnvelope> ReloadAsync(CancellationToken cancellationToken);
    event EventHandler<ConfigEnvelope>? Changed;
}
