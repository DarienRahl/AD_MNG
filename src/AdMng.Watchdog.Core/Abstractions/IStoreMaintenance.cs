using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>Retention, backups and storage status (section 16.3).</summary>
public interface IStoreMaintenance
{
    Task<PurgeResult> PurgeAsync(RetentionSettings retention, DateTimeOffset now, CancellationToken cancellationToken);
    Task<string> BackupAsync(CancellationToken cancellationToken);            // VACUUM INTO <data>\backup\…; returns file name
    Task<StorageStatusDto> GetStatusAsync(CancellationToken cancellationToken);
}
