using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>The stored configuration version differs from the expected one (HTTP 412).</summary>
public sealed class ConfigVersionConflictException(long expected, long actual)
    : Exception($"Configuration version conflict (expected {expected}, actual {actual}).")
{
    public long Expected { get; } = expected;
    public long Actual { get; } = actual;
}

/// <summary>The versioned runtime configuration, its secrets and audit trail (section 8).</summary>
public interface IConfigStore
{
    Task<ConfigEnvelope?> GetAsync(CancellationToken cancellationToken);
    /// <summary>Saves (config already stripped of SecretField.NewValue) when the stored version equals expectedVersion
    /// (0 = first save, no stored row); applies the secret changes and writes the audit entry **in the same transaction**
    /// – a failed save leaves secrets untouched.</summary>
    /// <exception cref="ConfigVersionConflictException"/>
    Task<ConfigEnvelope> SaveAsync(WatchdogConfiguration configuration, long expectedVersion, PrincipalRef by, string summary,
        IReadOnlyList<SecretChange> secretChanges, CancellationToken cancellationToken);
    Task<ConfigAuditPage> GetAuditAsync(long? before, int limit, CancellationToken cancellationToken);
}
