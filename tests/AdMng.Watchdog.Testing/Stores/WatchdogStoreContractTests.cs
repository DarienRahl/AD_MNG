using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using Xunit;

namespace AdMng.Watchdog.Testing.Stores;

/// <summary>
/// Behaviour every store implementation must have: the in-memory store (Core tests) and the SQLite stores (Service
/// tests, through one type implementing all store interfaces). Subclass it in a test project with the implementation
/// under test – xUnit runs the inherited facts there. Scaffold starter facts; the suite is owned by W-notify.
/// </summary>
/// <typeparam name="TStore">One object implementing every store interface of section 6.2.</typeparam>
public abstract class WatchdogStoreContractTests<TStore>
    where TStore : IDetectionStore, IEventStore, INotificationQueueStore, IOutboxStore, ISecurityEventStore,
        IExpiryNoticeStore, IDigestRunStore, IConfigStore, ISecretStore, IStoreMaintenance
{
    protected static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    protected const string Domain = "contoso.test";

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A new, empty store (its own temporary database, if any).</summary>
    protected abstract Task<TStore> CreateStoreAsync();

    /// <summary>Releases a store created by <see cref="CreateStoreAsync"/> (default: IAsyncDisposable / IDisposable).</summary>
    protected virtual async ValueTask DisposeStoreAsync(TStore store)
    {
        switch (store)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    [Fact]
    public async Task EmptyStore_HasNoStateEventsOrConfiguration()
    {
        var store = await CreateStoreAsync();
        try
        {
            Assert.Null(await store.GetDcStateAsync(Domain, Ct));
            Assert.Equal(0, await store.GetLatestIdAsync(Ct));
            Assert.Equal(0, await store.CountLiveObjectsAsync(Ct));
            Assert.Null(await ((IConfigStore)store).GetAsync(Ct));
        }
        finally
        {
            await DisposeStoreAsync(store);
        }
    }

    [Fact]
    public async Task Commit_StoresStateAndSnapshots_AndAssignsAscendingEventIds()
    {
        var store = await CreateStoreAsync();
        try
        {
            var user = Snapshot();
            var stored = await store.CommitAsync(
                new DetectionCommit
                {
                    NewState = State(highestUsn: 4711),
                    Upserts = [user],
                    Events = [Event(WatchEventKind.UserCreated, user), Event(WatchEventKind.AccountEnabled, user)],
                },
                Ct);

            Assert.Equal(2, stored.Count);
            Assert.All(stored, e => Assert.True(e.Id > 0));
            Assert.True(stored[0].Id < stored[1].Id);
            Assert.Equal(stored[1].Id, await store.GetLatestIdAsync(Ct));
            Assert.Equal(4711, (await store.GetDcStateAsync(Domain, Ct))?.HighestUsn);
            Assert.Equal(1, await store.CountLiveObjectsAsync(Ct));
            Assert.Equal(user.Dn, (await store.FindByDnAsync(user.Dn, Ct))?.Dn);
        }
        finally
        {
            await DisposeStoreAsync(store);
        }
    }

    [Fact]
    public async Task Snapshot_AttributeLookup_StaysCaseInsensitiveAfterRoundTrip()
    {
        var store = await CreateStoreAsync();
        try
        {
            var user = Snapshot();
            await store.CommitAsync(new DetectionCommit { NewState = State(highestUsn: 10), Upserts = [user] }, Ct);

            var loaded = (await store.GetSnapshotsAsync([user.ObjectGuid], Ct))[user.ObjectGuid];

            Assert.Equal("Księgowość", Assert.Single(loaded.Attributes["DEPARTMENT"].Values));
        }
        finally
        {
            await DisposeStoreAsync(store);
        }
    }

    [Fact]
    public async Task Config_FirstSaveIsVersionOne_AndAStaleVersionConflicts()
    {
        var store = await CreateStoreAsync();
        try
        {
            var by = new PrincipalRef { Sid = "S-1-5-21-1004336348-1177238915-682003330-1105", Account = @"CONTOSO\łukasz.wiśniewski" };

            var saved = await store.SaveAsync(new WatchdogConfiguration(), 0, by, "initial", [], Ct);

            Assert.Equal(1, saved.Version);
            Assert.Equal(1, (await ((IConfigStore)store).GetAsync(Ct))?.Version);
            await Assert.ThrowsAsync<ConfigVersionConflictException>(
                () => store.SaveAsync(new WatchdogConfiguration(), 0, by, "stale", [], Ct));
        }
        finally
        {
            await DisposeStoreAsync(store);
        }
    }

    [Fact]
    public async Task Outbox_LeasedMessage_IsSentOnce()
    {
        var store = await CreateStoreAsync();
        try
        {
            var id = await store.EnqueueAsync(
                new OutboxMessage
                {
                    MessageId = Guid.CreateVersion7(Now).ToString("N"),
                    ChannelType = DeliveryChannelType.Webhook,
                    ChannelId = "siem",
                    Kind = DeliveryKind.Alert,
                    Http = new RenderedHttpMessage { Body = "{\"type\":\"user.created\"}"u8.ToArray() },
                    CreatedAt = Now,
                    NextAttemptAt = Now,
                },
                Ct);

            var leased = await store.LeaseDueAsync(Now, 10, TimeSpan.FromMinutes(1), Ct);
            Assert.Equal(id, Assert.Single(leased).Id);

            await store.MarkSentAsync(id, Now, Ct);

            Assert.Empty(await store.LeaseDueAsync(Now.AddMinutes(5), 10, TimeSpan.FromMinutes(1), Ct));
            var item = Assert.Single((await store.QueryAsync(new OutboxQuery(), Ct)).Items);
            Assert.Equal(OutboxStatus.Sent, item.Status);
            Assert.Equal(1, item.Attempts);
        }
        finally
        {
            await DisposeStoreAsync(store);
        }
    }

    protected static DcState State(long highestUsn) => new()
    {
        Domain = Domain,
        DcHostName = "dc01.contoso.test",
        DsServiceName = "CN=NTDS Settings,CN=DC01,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=contoso,DC=test",
        InvocationId = Guid.Parse("6f1c7a52-3d2e-4b1a-9c55-0d7e8f9a1b2c"),
        HighestUsn = highestUsn,
        LastPollAt = Now,
    };

    protected static SnapshotObject Snapshot() => new()
    {
        ObjectGuid = Guid.Parse("9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f"),
        Dn = "CN=Zofia Żak,OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test",
        Kind = WatchObjectKind.User,
        ObjectClasses = ["top", "person", "organizationalPerson", "user"],
        Sid = "S-1-5-21-1004336348-1177238915-682003330-1106",
        SamAccountName = "zofia.zak",
        Name = "Zofia Żak",
        DisplayName = "Zofia Żak",
        UsnCreated = 4700,
        UsnChanged = 4711,
        WhenCreated = Now.AddMinutes(-5),
        Attributes = new Dictionary<string, SnapshotAttribute>(StringComparer.OrdinalIgnoreCase)
        {
            ["department"] = new() { Values = ["Księgowość"], Kind = AttributeValueKind.Text },
        },
    };

    protected static WatchEvent Event(WatchEventKind kind, SnapshotObject target) => new()
    {
        EventId = Guid.CreateVersion7(Now),
        Kind = kind,
        Severity = EventKindCatalog.DefaultSeverity(kind, target.Kind),
        Object = target.ToRef(),
        Domain = Domain,
        DetectedAt = Now,
        Source = DetectionSource.IncrementalPoll,
    };
}
