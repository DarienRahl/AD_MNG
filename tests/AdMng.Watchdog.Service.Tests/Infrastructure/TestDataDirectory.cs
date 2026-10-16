using Microsoft.Data.Sqlite;

namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>A temporary data folder for one test (database, keys, backups); deleted with every file on dispose after the
/// SQLite connection pools are cleared.</summary>
public sealed class TestDataDirectory : IDisposable
{
    public TestDataDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "admng-watchdog-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a background thread; the OS temp cleanup removes it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
