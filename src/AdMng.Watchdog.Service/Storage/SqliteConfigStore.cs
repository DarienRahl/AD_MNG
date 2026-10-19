using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Secrets;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// <see cref="IConfigStore"/> over <c>config</c> (one versioned row, JSON via <see cref="WatchdogJson.Options"/>),
/// <c>config_audit</c> and <c>secrets</c>: a save checks the expected version, applies the secret changes, writes the
/// document and the audit entry in ONE transaction – a conflict or any failure leaves secrets untouched (spec 17.2).
/// A document that still carries <see cref="SecretField.NewValue"/> is refused (secret values never reach the JSON), and
/// channel URL hints are always stored in the safe form of <see cref="SecretHints"/>.
/// </summary>
public sealed class SqliteConfigStore : IConfigStore
{
    private const int MaxPageSize = 500;

    private readonly SqliteDatabase _database;
    private readonly SecretProtector _protector;
    private readonly TimeProvider _timeProvider;

    internal SqliteConfigStore(SqliteDatabase database, SecretProtector protector, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _database = database;
        _protector = protector;
        _timeProvider = timeProvider;
    }

    /// <summary>The stored document; URL hints written before the safe-hint rule (<see cref="SecretHints"/>) are repaired
    /// in place on this read (same version, no audit entry).</summary>
    public async Task<ConfigEnvelope?> GetAsync(CancellationToken cancellationToken)
    {
        var row = _database.Read(c => SqliteValues.Query(
            c,
            null,
            "SELECT version, json, updated_at, updated_by FROM config WHERE id = 1;",
            r => (Version: r.GetInt64(0), Json: r.GetString(1), UpdatedAt: r.GetInt64(2), UpdatedBy: r.GetString(3))).FirstOrDefault());
        if (row.Json is null)
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<WatchdogConfiguration>(row.Json, WatchdogJson.Options)
            ?? throw new InvalidDataException("The stored configuration is empty.");
        var configuration = SecretHints.Sanitize(stored);
        if (!ReferenceEquals(configuration, stored))
        {
            var json = JsonSerializer.Serialize(configuration, WatchdogJson.Options);
            await _database.WriteAsync(
                (connection, transaction) => SqliteValues.Execute(
                    connection,
                    transaction,
                    "UPDATE config SET json = $json WHERE id = 1 AND version = $version;",
                    ("$json", json),
                    ("$version", row.Version)),
                cancellationToken).ConfigureAwait(false);
        }

        return new ConfigEnvelope
        {
            Version = row.Version,
            Configuration = configuration,
            UpdatedAt = SqliteValues.FromUnixMs(row.UpdatedAt),
            UpdatedBy = row.UpdatedBy,
        };
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
        if (SecretBindings.Enumerate(configuration).Any(s => s.Field.NewValue is not null))
        {
            throw new ArgumentException("The configuration still carries secret values (SecretField.NewValue); strip them before saving.", nameof(configuration));
        }

        var json = JsonSerializer.Serialize(SecretHints.Sanitize(configuration), WatchdogJson.Options);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                var actual = SqliteValues.Scalar<long?>(connection, transaction, "SELECT version FROM config WHERE id = 1;") ?? 0;
                if (actual != expectedVersion)
                {
                    throw new ConfigVersionConflictException(expectedVersion, actual);
                }

                var now = _timeProvider.GetUtcNow();
                foreach (var change in secretChanges)
                {
                    SqliteSecretStore.Write(connection, transaction, _protector, change, now);
                }

                var account = by.Account ?? by.Sid ?? string.Empty;
                var version = actual + 1;
                SqliteValues.Execute(
                    connection,
                    transaction,
                    "INSERT OR REPLACE INTO config (id, version, json, updated_at, updated_by) VALUES (1, $version, $json, $at, $by);",
                    ("$version", version),
                    ("$json", json),
                    ("$at", SqliteValues.ToUnixMs(now)),
                    ("$by", account));
                SqliteValues.Execute(
                    connection,
                    transaction,
                    "INSERT INTO config_audit (at, by_sid, by_account, version, summary) VALUES ($at, $sid, $account, $version, $summary);",
                    ("$at", SqliteValues.ToUnixMs(now)),
                    ("$sid", SqliteValues.OrNull(by.Sid)),
                    ("$account", account),
                    ("$version", version),
                    ("$summary", summary));
                return new ConfigEnvelope
                {
                    Version = version,
                    Configuration = JsonSerializer.Deserialize<WatchdogConfiguration>(json, WatchdogJson.Options)!,
                    UpdatedAt = now,
                    UpdatedBy = account,
                };
            },
            cancellationToken);
    }

    public Task<ConfigAuditPage> GetAuditAsync(long? before, int limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit, 1, MaxPageSize);
        var filter = before is null ? string.Empty : " WHERE id < $before";
        var rows = _database.Read(c => SqliteValues.Query(
            c,
            null,
            $"SELECT id, at, by_sid, by_account, summary, version FROM config_audit{filter} ORDER BY id DESC LIMIT {take + 1};",
            r => new ConfigAuditEntry
            {
                Id = r.GetInt64(0),
                At = SqliteValues.FromUnixMs(r.GetInt64(1)),
                BySid = SqliteValues.StringOrNull(r, 2),
                ByAccount = r.GetString(3),
                Summary = r.GetString(4),
                Version = r.GetInt64(5),
            },
            ("$before", before)));
        return Task.FromResult(new ConfigAuditPage { Items = [.. rows.Take(take)], HasMore = rows.Count > take });
    }
}
