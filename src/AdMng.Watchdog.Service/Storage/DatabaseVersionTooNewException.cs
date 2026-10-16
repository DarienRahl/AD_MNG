namespace AdMng.Watchdog.Service.Storage;

/// <summary>The database was written by a newer service version (<c>user_version</c> above the newest migration this
/// build knows) – the service refuses to start (exit code 3, spec 16.1).</summary>
public sealed class DatabaseVersionTooNewException(int databaseVersion, int supportedVersion)
    : Exception($"The database schema version {databaseVersion} is newer than the version {supportedVersion} this service supports.")
{
    public int DatabaseVersion { get; } = databaseVersion;

    public int SupportedVersion { get; } = supportedVersion;
}
