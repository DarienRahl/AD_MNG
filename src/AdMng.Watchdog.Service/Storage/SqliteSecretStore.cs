using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Secrets;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// <see cref="ISecretStore"/> over the <c>secrets</c> table (spec 18): values encrypted with Data Protection
/// (<see cref="SecretProtector"/>, re-protected on every write), each with its destination binding. Production writes go
/// through <see cref="SqliteConfigStore.SaveAsync"/> (one transaction with the configuration and its audit entry);
/// <see cref="SetAsync"/>/<see cref="RemoveAsync"/> serve tests and DR. A secret the key ring cannot decrypt throws
/// <see cref="SecretUnreadableException"/>.
/// </summary>
public sealed class SqliteSecretStore : ISecretStore
{
    private readonly SqliteDatabase _database;
    private readonly SecretProtector _protector;
    private readonly TimeProvider _timeProvider;

    internal SqliteSecretStore(SqliteDatabase database, SecretProtector protector, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _database = database;
        _protector = protector;
        _timeProvider = timeProvider;
    }

    public Task<StoredSecret?> GetAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var row = _database.Read(c => SqliteValues.Query(
            c,
            null,
            "SELECT protected, binding FROM secrets WHERE name = $name;",
            r => ((byte[])r.GetValue(0), r.GetString(1)),
            ("$name", name)).FirstOrDefault());
        return Task.FromResult(row.Item1 is null ? null : new StoredSecret(_protector.Unprotect(name, row.Item1), row.Item2));
    }

    public Task SetAsync(string name, SecretValue value, string binding, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(binding);
        var now = _timeProvider.GetUtcNow();
        return _database.WriteAsync((c, t) => Write(c, t, _protector, new SecretChange(name, value, binding), now), cancellationToken);
    }

    public Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _database.WriteAsync((c, t) => SqliteValues.Execute(c, t, "DELETE FROM secrets WHERE name = $name;", ("$name", name)), cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, string>> ListAsync(CancellationToken cancellationToken)
    {
        var bindings = _database.Read(c => SqliteValues.Query(c, null, "SELECT name, binding FROM secrets ORDER BY name;", r => (r.GetString(0), r.GetString(1))));
        return Task.FromResult<IReadOnlyDictionary<string, string>>(bindings.ToDictionary(b => b.Item1, b => b.Item2, StringComparer.Ordinal));
    }

    /// <summary>Applies one secret change inside a transaction (null value = remove).</summary>
    internal static void Write(SqliteConnection connection, SqliteTransaction transaction, SecretProtector protector, SecretChange change, DateTimeOffset now)
    {
        if (change.Value is null)
        {
            SqliteValues.Execute(connection, transaction, "DELETE FROM secrets WHERE name = $name;", ("$name", change.Name));
            return;
        }

        SqliteValues.Execute(
            connection,
            transaction,
            "INSERT OR REPLACE INTO secrets (name, protected, binding, updated_at) VALUES ($name, $protected, $binding, $now);",
            ("$name", change.Name),
            ("$protected", protector.Protect(change.Value)),
            ("$binding", change.Binding ?? string.Empty),
            ("$now", SqliteValues.ToUnixMs(now)));
    }
}
