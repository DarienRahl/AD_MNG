using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.Watchdog.Core.Tests.Stores;

/// <summary>Behaviour of the in-memory store beyond the shared contract: foreign keys, isolation of the stored copies and
/// the simulated storage figures.</summary>
public sealed class InMemoryWatchdogStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemoryWatchdogStore _store = new(new FakeTimeProvider(Now));

    [Fact]
    public async Task PendingRowsAndOutboxLinks_MustReferenceStoredEvents()
    {
        var pending = new PendingNotification
        {
            EventId = 42,
            RuleId = "lockouts",
            Target = new NotificationTarget { ChannelType = DeliveryChannelType.Webhook, ChannelId = "siem" },
            Severity = Severity.Low,
            BucketKey = "lockouts|siem",
            DueAt = Now,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.AddAsync([pending], Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.EnqueueAsync(Message([42]), Ct));
        Assert.Empty(await _store.GetAllAsync(Ct));
    }

    [Fact]
    public async Task StoredEvents_AreCopies_NotTheCallersInstances()
    {
        var changes = new List<AttributeChange> { new() { Attribute = "department", NewValues = ["Księgowość"] } };
        var id = await _store.AppendAsync(Event() with { Changes = changes }, Ct);

        changes.Add(new AttributeChange { Attribute = "title" });

        Assert.Single((await _store.GetAsync(id, Ct))!.Changes);
    }

    [Fact]
    public async Task DuplicateEventIds_AreRejected()
    {
        var watchEvent = Event();
        await _store.AppendAsync(watchEvent, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.AppendAsync(watchEvent, Ct));
    }

    [Fact]
    public async Task Status_ReportsTheSimulatedFigures_AndBackupsWriteNothing()
    {
        _store.SimulatedDatabaseBytes = 9L * 1024 * 1024 * 1024;
        _store.SimulatedFreeBytes = 512L * 1024 * 1024;

        var name = await _store.BackupAsync(Ct);
        var status = await _store.GetStatusAsync(Ct);

        Assert.Equal("watchdog-20261014-0800.db", name);
        Assert.Equal(1, _store.BackupCount);
        Assert.Equal(9L * 1024 * 1024 * 1024, status.DatabaseBytes);
        Assert.Equal(512L * 1024 * 1024, status.FreeBytes);
        Assert.Equal(Now, status.LastBackupAt);
    }

    [Fact]
    public async Task Purge_KeepsDigestRunsForAYear()
    {
        await _store.RecordAsync(Now.AddDays(-1), Now, 3, [], Ct);

        await _store.PurgeAsync(new RetentionSettings(), Now.AddDays(364), Ct);
        Assert.Equal(Now, await _store.GetLastPeriodEndAsync(Ct));

        await _store.PurgeAsync(new RetentionSettings(), Now.AddDays(366), Ct);
        Assert.Null(await _store.GetLastPeriodEndAsync(Ct));
    }

    private static WatchEvent Event() => new()
    {
        EventId = Guid.NewGuid(),
        Kind = WatchEventKind.AttributesChanged,
        Object = new ObjectRef { Dn = "CN=Zofia Żak,OU=Księgowość,DC=contoso,DC=test", Kind = WatchObjectKind.User },
        DetectedAt = Now,
        Source = DetectionSource.IncrementalPoll,
    };

    private static OutboxMessage Message(IReadOnlyList<long> eventIds) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        ChannelType = DeliveryChannelType.Webhook,
        ChannelId = "siem",
        Kind = DeliveryKind.Alert,
        EventIds = eventIds,
        CreatedAt = Now,
        NextAttemptAt = Now,
    };
}
