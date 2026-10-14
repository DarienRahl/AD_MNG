using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// The cached effective configuration (<see cref="IWatchdogConfigProvider"/>): the stored document with explicit nulls
/// repaired (<see cref="ConfigurationNormalizer"/>), null list entries dropped and built-in rules of a newer service
/// version merged in memory (8.2 – persisted only with the next admin PUT; the provider never writes). Tokens stay
/// unexpanded. Before the first <see cref="ReloadAsync"/>, and while nothing is stored, <see cref="Current"/> is the
/// default configuration with version 0 – never null.
/// </summary>
public sealed partial class WatchdogConfigProvider : IWatchdogConfigProvider, IDisposable
{
    private readonly IConfigStore _store;
    private readonly IDefaultConfigurationFactory _defaults;
    private readonly ILogger<WatchdogConfigProvider> _logger;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private volatile ConfigEnvelope _current;

    public WatchdogConfigProvider(IConfigStore store, IDefaultConfigurationFactory defaults, ILogger<WatchdogConfigProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _defaults = defaults;
        _logger = logger;
        _current = DefaultEnvelope();
    }

    /// <inheritdoc/>
    public ConfigEnvelope Current => _current;

    /// <summary>Raised after a reload produced another version (handlers run on the reloading thread; their exceptions
    /// are logged and swallowed).</summary>
    public event EventHandler<ConfigEnvelope>? Changed;

    /// <inheritdoc/>
    public async Task<ConfigEnvelope> ReloadAsync(CancellationToken cancellationToken)
    {
        ConfigEnvelope previous;
        ConfigEnvelope next;
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
            next = stored?.Configuration is null
                ? DefaultEnvelope()
                : stored with { Configuration = Effective(stored.Configuration), IsRedacted = false };
            previous = _current;
            _current = next;
        }
        finally
        {
            _reloadGate.Release();
        }

        if (previous.Version != next.Version)
        {
            LogReloaded(next.Version);
            RaiseChanged(next);
        }

        return next;
    }

    /// <summary>The effective form of a stored document: normalized, null entries dropped, missing built-in rules
    /// appended (<see cref="DefaultRules.MergeMissing"/>).</summary>
    public static WatchdogConfiguration Effective(WatchdogConfiguration stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var normalized = ConfigurationNormalizer.Normalize(stored);
        return normalized with
        {
            Channels = [.. normalized.Channels.Where(c => c is not null)],
            RecipientGroups = [.. normalized.RecipientGroups.Where(g => g is not null)],
            Rules = DefaultRules.MergeMissing(normalized.Rules),
        };
    }

    /// <inheritdoc/>
    public void Dispose() => _reloadGate.Dispose();

    private ConfigEnvelope DefaultEnvelope() => new() { Version = 0, Configuration = Effective(_defaults.Create()) };

    private void RaiseChanged(ConfigEnvelope envelope)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<ConfigEnvelope>>())
        {
            try
            {
                handler(this, envelope);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SafeExceptionLog.LogError(_logger, ex, "configuration Changed handler");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuration version {Version} loaded.")]
    private partial void LogReloaded(long version);
}
