using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Stores;

// IConfigStore + ISecretStore: config, config_audit, secrets (one transaction per save).
public sealed partial class InMemoryWatchdogStore
{
    private readonly Dictionary<string, StoredSecret> _secrets = new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, ConfigAuditEntry> _audit = [];
    private ConfigEnvelope? _config;
    private long _lastAuditId;

    // ---------------------------------------------------------------- IConfigStore

    public Task<ConfigEnvelope?> GetAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_config is null ? null : Copy(_config));
        }
    }

    public Task<ConfigEnvelope> SaveAsync(
        WatchdogConfiguration configuration,
        long expectedVersion,
        PrincipalRef by,
        string summary,
        IReadOnlyList<SecretChange> secretChanges,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(by);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(secretChanges);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var actual = _config?.Version ?? 0;
            if (actual != expectedVersion)
            {
                throw new ConfigVersionConflictException(expectedVersion, actual);
            }

            var now = UtcNow;
            var account = by.Account ?? by.Sid ?? string.Empty;
            var envelope = new ConfigEnvelope
            {
                Version = actual + 1,
                Configuration = Copy(configuration),
                UpdatedAt = now,
                UpdatedBy = account,
            };

            foreach (var change in secretChanges)
            {
                if (change.Value is null)
                {
                    _secrets.Remove(change.Name);
                }
                else
                {
                    _secrets[change.Name] = new StoredSecret(new SecretValue(change.Value.Reveal()), change.Binding ?? string.Empty);
                }
            }

            _config = envelope;
            var auditId = ++_lastAuditId;
            _audit[auditId] = new ConfigAuditEntry
            {
                Id = auditId,
                At = now,
                BySid = by.Sid,
                ByAccount = account,
                Summary = summary,
                Version = envelope.Version,
            };
            return Task.FromResult(Copy(envelope));
        }
    }

    public Task<ConfigAuditPage> GetAuditAsync(long? before, int limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit, 1, MaxPageSize);
        lock (_gate)
        {
            var entries = _audit.Values.Reverse().Where(a => before is not { } b || a.Id < b).Take(take + 1).ToList();
            return Task.FromResult(new ConfigAuditPage { Items = [.. entries.Take(take)], HasMore = entries.Count > take });
        }
    }

    // ---------------------------------------------------------------- ISecretStore

    public Task<StoredSecret?> GetAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            return Task.FromResult(_secrets.TryGetValue(name, out var secret)
                ? new StoredSecret(new SecretValue(secret.Value.Reveal()), secret.Binding)
                : null);
        }
    }

    public Task SetAsync(string name, SecretValue value, string binding, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
        {
            _secrets[name] = new StoredSecret(new SecretValue(value.Reveal()), binding);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            _secrets.Remove(name);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<string, string> result = _secrets.ToDictionary(s => s.Key, s => s.Value.Binding, StringComparer.Ordinal);
            return Task.FromResult(result);
        }
    }
}
