using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>The <c>meta</c> key/value table (installation id, last backup time …). Never holds directory values.</summary>
internal static class MetaRows
{
    public const string InstallationId = "installation_id";

    public const string LastBackupAt = "last_backup_at";

    public static string? Get(SqliteConnection connection, SqliteTransaction? transaction, string key) =>
        SqliteValues.Query(connection, transaction, "SELECT value FROM meta WHERE key = $key;", r => r.GetString(0), ("$key", key))
            .FirstOrDefault();

    public static void Set(SqliteConnection connection, SqliteTransaction? transaction, string key, string value) =>
        SqliteValues.Execute(connection, transaction, "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);", ("$key", key), ("$value", value));
}
