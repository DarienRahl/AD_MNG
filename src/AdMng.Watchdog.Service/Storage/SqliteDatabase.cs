using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// The watchdog database (<c>&lt;data&gt;/watchdog.db</c>, spec 16.1): connection factory with the per-connection PRAGMAs
/// (<c>synchronous = NORMAL</c>, <c>foreign_keys = ON</c>, <c>busy_timeout = 30000</c>, <c>secure_delete = ON</c>,
/// <c>temp_store = MEMORY</c>), the single-writer gate and the SQL functions the stores use. Pooled connections, never
/// <c>Cache=Shared</c>; readers open their own short-lived connections and materialize results before returning.
/// </summary>
public sealed class SqliteDatabase : IAsyncDisposable
{
    /// <summary>File name of the database inside the data directory.</summary>
    public const string FileName = "watchdog.db";

    /// <summary>Name of the backup folder inside the data directory.</summary>
    public const string BackupFolderName = "backup";

    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly string _connectionString;
    private int _disposed;

    /// <summary>Uses <c>&lt;dataDirectory&gt;/watchdog.db</c>; the folder must exist (DataDirectory creates it with its ACL).</summary>
    public SqliteDatabase(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        DatabasePath = Path.Combine(DataDirectory, FileName);
        BackupDirectory = Path.Combine(DataDirectory, BackupFolderName);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <summary>The data folder (absolute).</summary>
    public string DataDirectory { get; }

    /// <summary>Full path of <c>watchdog.db</c>.</summary>
    public string DatabasePath { get; }

    /// <summary><c>&lt;data&gt;/backup</c> – VACUUM INTO targets (retention and pre-migration backups).</summary>
    public string BackupDirectory { get; }

    /// <summary>Opens a pooled connection with every per-connection PRAGMA and function applied.</summary>
    public SqliteConnection Open()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            Configure(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Runs <paramref name="read"/> on its own connection (no transaction).</summary>
    public T Read<T>(Func<SqliteConnection, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var connection = Open();
        return read(connection);
    }

    /// <summary>
    /// Runs <paramref name="read"/> on its own connection inside one deferred (read) transaction, so every statement it
    /// executes sees the same WAL snapshot – use it whenever two statements must agree (a page and its latest id). Not
    /// behind the writer gate: writers keep committing, the snapshot simply does not see them.
    /// </summary>
    public T ReadSnapshot<T>(Func<SqliteConnection, SqliteTransaction, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = read(connection, transaction);
        transaction.Commit();
        return result;
    }

    /// <summary>
    /// Runs <paramref name="write"/> inside one <c>BEGIN IMMEDIATE</c> transaction behind the single-writer gate and
    /// commits it; an exception rolls everything back.
    /// </summary>
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var result = write(connection, transaction);
            transaction.Commit();
            return result;
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary><see cref="WriteAsync{T}"/> without a result.</summary>
    public Task WriteAsync(Action<SqliteConnection, SqliteTransaction> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return WriteAsync<bool>(
            (connection, transaction) =>
            {
                write(connection, transaction);
                return true;
            },
            cancellationToken);
    }

    /// <summary>Runs <paramref name="action"/> behind the writer gate without a transaction (VACUUM, checkpoints).</summary>
    public async Task<T> ExclusiveAsync<T>(Func<SqliteConnection, T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Open();
            return action(connection);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Releases the pooled connections of this database (before files are moved or deleted).</summary>
    public void ClearPool()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ClearPool();
            _writer.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static void Configure(SqliteConnection connection)
    {
        using (var pragma = connection.CreateCommand())
        {
            // e_sqlite3 is built without SECURE_DELETE and with TEMP_STORE=1: without these PRAGMAs purged rows would
            // linger in free pages, and sorts / VACUUM INTO would spill directory data into %TEMP% (spec 16.1).
            pragma.CommandText =
                "PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000; " +
                "PRAGMA secure_delete = ON; PRAGMA temp_store = MEMORY;";
            pragma.ExecuteNonQuery();
        }

        // Exact RFC 4514 subtree test for EventQuery.Subtree (the SQL prefilter compares normalized suffixes only).
        connection.CreateFunction<string?, string?, bool>(
            "wd_at_or_below",
            static (dn, ancestor) => SqliteValues.IsAtOrBelow(dn, ancestor),
            isDeterministic: true);
    }
}
