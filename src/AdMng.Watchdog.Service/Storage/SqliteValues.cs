using AdMng.Core.Ldap;
using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>Column conventions of the watchdog database (spec 16.1): Unix milliseconds, GUID blobs, normalized DNs,
/// upper-case SIDs and parameter helpers.</summary>
internal static class SqliteValues
{
    /// <summary>Unix milliseconds UTC.</summary>
    public static long ToUnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static object ToUnixMsOrNull(DateTimeOffset? value) => value is { } v ? v.ToUnixTimeMilliseconds() : DBNull.Value;

    public static DateTimeOffset FromUnixMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    public static DateTimeOffset? FromUnixMsOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));

    /// <summary><see cref="Guid.ToByteArray()"/> (16 bytes).</summary>
    public static byte[] ToBlob(Guid value) => value.ToByteArray();

    public static object ToBlobOrNull(Guid? value) => value is { } v && v != Guid.Empty ? v.ToByteArray() : DBNull.Value;

    public static Guid GuidFrom(SqliteDataReader reader, int ordinal) => new((byte[])reader.GetValue(ordinal));

    public static Guid? GuidOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new Guid((byte[])reader.GetValue(ordinal));

    public static string? StringOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static byte[]? BlobOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : (byte[])reader.GetValue(ordinal);

    /// <summary>The <c>*_dn_norm</c> form: RFC 4514 re-serialized and upper-cased (invariant); unparsable values are
    /// trimmed and upper-cased; null/empty → "".</summary>
    public static string NormalizeDn(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
        {
            return string.Empty;
        }

        return DistinguishedName.TryParse(dn, out var parsed)
            ? parsed.ToString().ToUpperInvariant()
            : dn.Trim().ToUpperInvariant();
    }

    public static object NormalizeDnOrNull(string? dn) => string.IsNullOrWhiteSpace(dn) ? DBNull.Value : NormalizeDn(dn);

    /// <summary>SID lookup form (upper-case, trimmed) or DBNull.</summary>
    public static object SidOrNull(string? sid) =>
        string.IsNullOrWhiteSpace(sid) ? DBNull.Value : sid.Trim().ToUpperInvariant();

    /// <summary>Lower-case lookup form or DBNull.</summary>
    public static object LowerOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim().ToLowerInvariant();

    public static object OrNull(string? value) => value is null ? DBNull.Value : value;

    public static object OrNull(long? value) => value is { } v ? v : DBNull.Value;

    /// <summary>True when <paramref name="dn"/> equals or is below <paramref name="ancestor"/> (RFC 4514, case-insensitive).</summary>
    public static bool IsAtOrBelow(string? dn, string? ancestor)
    {
        if (!DistinguishedName.TryParse(dn, out var parsed) || parsed.IsRoot
            || !DistinguishedName.TryParse(ancestor, out var parent) || parent.IsRoot)
        {
            return false;
        }

        return parsed.Equals(parent) || parsed.IsDescendantOf(parent);
    }

    /// <summary>Creates a command bound to <paramref name="transaction"/> (may be null) with named parameters.</summary>
    public static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>Executes a non-query and returns the number of changed rows.</summary>
    public static int Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>Executes a scalar query; null/DBNull → default.</summary>
    public static T? Scalar<T>(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Reads every row with <paramref name="map"/>.</summary>
    public static List<T> Query<T>(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        Func<SqliteDataReader, T> map,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            result.Add(map(reader));
        }

        return result;
    }

    /// <summary>"|a|b|" list column (lower-case, distinct); "" when empty.</summary>
    public static string PipeList(IEnumerable<string> values)
    {
        var items = values.Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return items.Count == 0 ? string.Empty : "|" + string.Join('|', items) + "|";
    }

    /// <summary>"|value|" needle for <see cref="PipeList"/> columns.</summary>
    public static string PipeNeedle(string value) => "|" + value.Trim().ToLowerInvariant() + "|";

    /// <summary>"$p0, $p1, …" placeholders and their parameters for an IN list.</summary>
    public static string InList(string prefix, IReadOnlyList<object> values, List<(string Name, object? Value)> parameters)
    {
        var names = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            names[i] = $"${prefix}{i}";
            parameters.Add((names[i], values[i]));
        }

        return string.Join(", ", names);
    }
}
