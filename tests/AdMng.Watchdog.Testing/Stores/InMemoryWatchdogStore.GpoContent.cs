using System.Text.Json;
using System.Text.Json.Serialization;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Gpo;

namespace AdMng.Watchdog.Testing.Stores;

/// <summary><see cref="AdMng.Watchdog.Core.Abstractions.IGpoContentStore"/>: snapshots copied through JSON (what the
/// deflated payload columns of migration 0005 store), the queue merged by GUID, commits atomic under the store lock.</summary>
public sealed partial class InMemoryWatchdogStore
{
    private static readonly JsonSerializerOptions GpoJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly Dictionary<Guid, StoredGpoContent> _gpoContent = [];
    private readonly Dictionary<Guid, GpoQueueEntry> _gpoQueue = [];
    private GpoContentJobState _gpoState = new();

    public Task<StoredGpoContent?> GetGpoContentAsync(Guid gpoGuid, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_gpoContent.TryGetValue(gpoGuid, out var stored) ? Copy(stored, includeSettings: true) : null);
        }
    }

    public Task<IReadOnlyList<StoredGpoContent>> ListGpoContentAsync(bool includeSettings, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<StoredGpoContent>>(
                [.. _gpoContent.Values.OrderBy(s => s.Snapshot.GpoGuid).Select(s => Copy(s, includeSettings))]);
        }
    }

    public Task<IReadOnlyList<WatchEvent>> CommitGpoContentAsync(GpoContentSnapshot snapshot, IReadOnlyList<WatchEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(events);
        List<WatchEvent> stored;
        lock (_gate)
        {
            _gpoContent[snapshot.GpoGuid] = new StoredGpoContent { Snapshot = CopySnapshot(snapshot, includeSettings: true) };
            stored = [.. events.Select(InsertEvent)];
            _gpoQueue.Remove(snapshot.GpoGuid);
        }

        SignalAppended(stored.Count);
        return Task.FromResult<IReadOnlyList<WatchEvent>>(stored);
    }

    public Task<IReadOnlyList<WatchEvent>> CompleteGpoQueueEntryAsync(Guid gpoGuid, IReadOnlyList<WatchEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        List<WatchEvent> stored;
        lock (_gate)
        {
            stored = [.. events.Select(InsertEvent)];
            _gpoQueue.Remove(gpoGuid);
        }

        SignalAppended(stored.Count);
        return Task.FromResult<IReadOnlyList<WatchEvent>>(stored);
    }

    public Task MarkGpoContentDeletedAsync(Guid gpoGuid, DateTimeOffset deletedAt, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_gpoContent.TryGetValue(gpoGuid, out var stored) && stored.DeletedAt is null)
            {
                _gpoContent[gpoGuid] = stored with { DeletedAt = deletedAt };
            }

            _gpoQueue.Remove(gpoGuid);
        }

        return Task.CompletedTask;
    }

    public Task EnqueueGpoContentAsync(GpoQueueEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _gpoQueue[entry.GpoGuid] = _gpoQueue.TryGetValue(entry.GpoGuid, out var existing) ? existing.Merge(entry) : entry;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GpoQueueEntry>> GetGpoQueueAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<GpoQueueEntry>>([.. _gpoQueue.Values.OrderBy(e => e.DueAt).ThenBy(e => e.GpoGuid).Select(Truncate)]);
        }
    }

    public Task UpdateGpoQueueEntryAsync(GpoQueueEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_gpoQueue.ContainsKey(entry.GpoGuid))
            {
                _gpoQueue[entry.GpoGuid] = entry;
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveGpoQueueEntryAsync(Guid gpoGuid, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _gpoQueue.Remove(gpoGuid);
        }

        return Task.CompletedTask;
    }

    public Task<GpoContentJobState> GetGpoContentStateAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(Truncate(_gpoState));
        }
    }

    public Task SaveGpoContentStateAsync(GpoContentJobState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _gpoState = state;
        }

        return Task.CompletedTask;
    }

    private int PurgeDeletedGpoContent(DateTimeOffset cutoff)
    {
        var purge = _gpoContent.Values.Where(s => s.DeletedAt is { } at && at < cutoff).Select(s => s.Snapshot.GpoGuid).ToList();
        foreach (var guid in purge)
        {
            _gpoContent.Remove(guid);
        }

        return purge.Count;
    }

    private static StoredGpoContent Copy(StoredGpoContent stored, bool includeSettings) =>
        new() { Snapshot = CopySnapshot(stored.Snapshot, includeSettings), DeletedAt = stored.DeletedAt };

    /// <summary>A deep copy as the JSON blob columns store it (millisecond times like the integer columns).</summary>
    private static GpoContentSnapshot CopySnapshot(GpoContentSnapshot snapshot, bool includeSettings) => snapshot with
    {
        ReadAt = Milliseconds(snapshot.ReadAt),
        Inventory = JsonSerializer.Deserialize<List<GpoInventoryEntry>>(JsonSerializer.SerializeToUtf8Bytes(snapshot.Inventory.ToList(), GpoJson), GpoJson) ?? [],
        Settings = includeSettings
            ? JsonSerializer.Deserialize<List<GpoSetting>>(JsonSerializer.SerializeToUtf8Bytes(snapshot.Settings.ToList(), GpoJson), GpoJson) ?? []
            : [],
    };

    private static GpoQueueEntry Truncate(GpoQueueEntry entry) => entry with
    {
        DueAt = Milliseconds(entry.DueAt),
        EnqueuedAt = Milliseconds(entry.EnqueuedAt),
        FirstAttemptAt = entry.FirstAttemptAt is { } first ? Milliseconds(first) : null,
    };

    private static GpoContentJobState Truncate(GpoContentJobState state) => state with
    {
        BaselineCompletedAt = state.BaselineCompletedAt is { } a ? Milliseconds(a) : null,
        LastReconciliationAt = state.LastReconciliationAt is { } b ? Milliseconds(b) : null,
        LastReadAt = state.LastReadAt is { } c ? Milliseconds(c) : null,
    };

    private static DateTimeOffset Milliseconds(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
}
