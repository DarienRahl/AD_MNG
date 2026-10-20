using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Stores;

// IStoreMaintenance: retention purge with the cascades of section 16.3; backups are only recorded (nothing on disk).
public sealed partial class InMemoryWatchdogStore
{
    /// <summary>Expiry notices are purged this long after the expiry passed (16.3).</summary>
    public static readonly TimeSpan NoticeRetention = TimeSpan.FromDays(30);

    /// <summary>Digest runs are kept for a year (16.3).</summary>
    public static readonly TimeSpan DigestRunRetention = TimeSpan.FromDays(365);

    private DateTimeOffset? _lastBackupAt;

    /// <summary>Size reported by <see cref="GetStatusAsync"/> (tests simulate a growing database).</summary>
    public long SimulatedDatabaseBytes { get; set; }

    /// <summary>Free space reported by <see cref="GetStatusAsync"/> (default 1 TiB; tests simulate a full volume).</summary>
    public long SimulatedFreeBytes { get; set; } = 1L << 40;

    /// <summary>Number of <see cref="BackupAsync"/> calls.</summary>
    public int BackupCount { get; private set; }

    public Task<PurgeResult> PurgeAsync(RetentionSettings retention, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retention);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var events = PurgeEvents(now.AddDays(-retention.EventsDays));
            var securityEvents = PurgeSecurityEvents(now.AddDays(-retention.SecurityEventsDays));
            var outbox = PurgeOutbox(now.AddDays(-retention.OutboxSentDays), now.AddDays(-retention.OutboxDeadDays));
            var snapshots = PurgeDeletedSnapshots(now.AddDays(-retention.DeletedSnapshotDays));
            var notices = PurgeNotices(now - NoticeRetention);
            var audit = PurgeAudit(now.AddDays(-retention.ConfigAuditDays));
            _digestRuns.RemoveAll(r => r.CreatedAt < now - DigestRunRetention);
            return Task.FromResult(new PurgeResult(events, securityEvents, outbox, snapshots, notices, audit));
        }
    }

    public Task<string> BackupAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var now = UtcNow;
            _lastBackupAt = now;
            BackupCount++;
            return Task.FromResult(string.Create(CultureInfo.InvariantCulture, $"watchdog-{now:yyyyMMdd-HHmm}.db"));
        }
    }

    public Task<StorageStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(new StorageStatusDto
            {
                DataDirectory = "(in memory)",
                DatabaseBytes = SimulatedDatabaseBytes,
                FreeBytes = SimulatedFreeBytes,
                EventCount = _events.Count,
                OldestEventAt = _events.Count == 0 ? null : _events.Values.Min(r => r.Event.DetectedAt),
                LastBackupAt = _lastBackupAt,
            });
        }
    }

    private int PurgeEvents(DateTimeOffset cutoff)
    {
        var purged = _events.Values.Where(r => r.Event.DetectedAt < cutoff).Select(r => r.Id).ToHashSet();
        foreach (var id in purged)
        {
            _eventGuids.Remove(_events[id].Event.EventId);
            _events.Remove(id);
        }

        // ON DELETE CASCADE: pending notifications and outbox links (the outbox rows stay).
        foreach (var pending in _pending.Values.Where(p => purged.Contains(p.EventId)).ToList())
        {
            _pending.Remove(pending.Id);
        }

        foreach (var row in _outbox.Values)
        {
            row.EventIds.ExceptWith(purged);
        }

        return purged.Count;
    }

    private int PurgeSecurityEvents(DateTimeOffset cutoff)
    {
        var purged = _securityEvents.Where(e => e.TimeCreated < cutoff).ToList();
        foreach (var securityEvent in purged)
        {
            _securityEventKeys.Remove((securityEvent.Computer.ToUpperInvariant(), securityEvent.RecordId, StoredTime(securityEvent.TimeCreated)));
        }

        _securityEvents.RemoveAll(e => e.TimeCreated < cutoff);
        return purged.Count;
    }

    private int PurgeOutbox(DateTimeOffset sentCutoff, DateTimeOffset deadCutoff)
    {
        var purged = _outbox.Values
            .Where(r => (r.Status == OutboxStatus.Sent && r.SentAt < sentCutoff)
                || (r.Status is OutboxStatus.Dead or OutboxStatus.Cancelled && r.Message.CreatedAt < deadCutoff))
            .Select(r => r.Id)
            .ToList();
        foreach (var id in purged)
        {
            _outbox.Remove(id);
        }

        return purged.Count;
    }

    private int PurgeDeletedSnapshots(DateTimeOffset cutoff)
    {
        var purged = _snapshots.Values.Where(s => s.IsDeleted && s.DeletedAt is { } at && at < cutoff).Select(s => s.ObjectGuid).ToList();
        foreach (var guid in purged)
        {
            _snapshots.Remove(guid);
            _members.Remove(guid);
        }

        return purged.Count;
    }

    private int PurgeNotices(DateTimeOffset cutoff)
    {
        var purged = _notices.Keys.Where(k => AdConvert.FromFileTime(k.ExpiresRaw) is { } expires && expires < cutoff).ToList();
        foreach (var key in purged)
        {
            _notices.Remove(key);
        }

        return purged.Count;
    }

    private int PurgeAudit(DateTimeOffset cutoff)
    {
        var purged = _audit.Values.Where(a => a.At < cutoff).Select(a => a.Id).ToList();
        foreach (var id in purged)
        {
            _audit.Remove(id);
        }

        return purged.Count;
    }
}
