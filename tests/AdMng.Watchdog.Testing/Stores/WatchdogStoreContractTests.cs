using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AdMng.Watchdog.Testing.Stores;

/// <summary>
/// Behaviour every store implementation must have: the in-memory store (Core tests) and the SQLite stores (Service
/// tests, through one type implementing all store interfaces). Subclass it in a test project with the implementation
/// under test – xUnit runs the inherited facts there. The suite is split per interface into partial files.
/// </summary>
/// <remarks>
/// <para><see cref="CreateStoreAsync"/> must give the store <see cref="Clock"/> as its <see cref="TimeProvider"/>: the
/// facts check the times the store writes itself (watermarks, configuration saves, audit entries,
/// <c>MembershipsChangedAt</c>, <c>BaselineCompletedAt</c>) and the long-poll timeout of
/// <c>IEventStore.WaitForNewerAsync</c>.</para>
/// <para>Semantics pinned here (the SQLite schema of section 16.2 implements them): AUTOINCREMENT event, outbox and
/// pending ids that are never reused; event time ranges half-open <c>[from, to)</c>; Security-event windows closed
/// <c>[From, To]</c>; contract payloads round-trip through <c>WatchdogJson</c>; snapshot attribute dictionaries stay
/// case-insensitive; foreign-key cascades of events to pending notifications and outbox links.</para>
/// </remarks>
/// <typeparam name="TStore">One object implementing every store interface of section 6.2.</typeparam>
public abstract partial class WatchdogStoreContractTests<TStore>
    where TStore : IDetectionStore, IEventStore, INotificationQueueStore, IOutboxStore, ISecurityEventStore,
        IExpiryNoticeStore, IDigestRunStore, IConfigStore, ISecretStore, IStoreMaintenance
{
    protected static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    protected const string Domain = "contoso.test";

    protected const string DomainDn = "DC=contoso,DC=test";

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The clock the store under test must use (starts at <see cref="Now"/>).</summary>
    protected FakeTimeProvider Clock { get; } = new(Now);

    /// <summary>A new, empty store (its own temporary database, if any) that uses <see cref="Clock"/>.</summary>
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

    /// <summary>Runs <paramref name="test"/> against a new store and disposes it afterwards.</summary>
    protected async Task WithStoreAsync(Func<TStore, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        var store = await CreateStoreAsync();
        try
        {
            await test(store);
        }
        finally
        {
            await DisposeStoreAsync(store);
        }
    }

    [Fact]
    public Task EmptyStore_HasNoStateEventsOrConfiguration() => WithStoreAsync(async store =>
    {
        Assert.Null(await store.GetDcStateAsync(Domain, Ct));
        Assert.Equal(0, await store.GetLatestIdAsync(Ct));
        Assert.Equal(0, await store.CountLiveObjectsAsync(Ct));
        Assert.Null(await ((IConfigStore)store).GetAsync(Ct));
        Assert.Null(await store.GetPrivilegeStateAsync(Ct));
        Assert.Empty(await store.GetAllAsync(Ct));
        Assert.Empty((await store.QueryAsync(new OutboxQuery(), Ct)).Items);
        Assert.Empty(await store.GetUpcomingAsync(Ct));
        Assert.Null(await store.GetLastPeriodEndAsync(Ct));
        Assert.Empty(await store.ListAsync(Ct));
        Assert.Null(await store.GetCursorAsync("dc01.contoso.test", Ct));
    });

    // ---------------------------------------------------------------- shared test data

    protected static readonly Guid InvocationId = Guid.Parse("6f1c7a52-3d2e-4b1a-9c55-0d7e8f9a1b2c");

    protected const string DsServiceName =
        "CN=NTDS Settings,CN=DC01,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=contoso,DC=test";

    protected static DcState State(long highestUsn) => new()
    {
        Domain = Domain,
        DcHostName = "dc01.contoso.test",
        DsServiceName = DsServiceName,
        InvocationId = InvocationId,
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

    /// <summary>A live snapshot row with its own GUID, DN, kind and optional SID / attributes.</summary>
    protected static SnapshotObject Snapshot(
        string dn,
        WatchObjectKind kind = WatchObjectKind.User,
        string? sid = null,
        Guid? guid = null,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        var name = dn[(dn.IndexOf('=', StringComparison.Ordinal) + 1)..dn.IndexOf(',', StringComparison.Ordinal)];
        var map = new Dictionary<string, SnapshotAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var (attribute, value) in attributes ?? new Dictionary<string, string>())
        {
            map[attribute] = new SnapshotAttribute { Values = [value], Kind = AttributeValueKind.Text };
        }

        return new SnapshotObject
        {
            ObjectGuid = guid ?? Guid.NewGuid(),
            Dn = dn,
            Kind = kind,
            Sid = sid,
            Name = name,
            SamAccountName = kind is WatchObjectKind.OrganizationalUnit ? null : name.Replace(' ', '.').ToLowerInvariant(),
            UsnCreated = 100,
            UsnChanged = 100,
            Attributes = map,
        };
    }

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

    /// <summary>An event with a fresh EventId detected at <paramref name="detectedAt"/> (default <see cref="Now"/>).</summary>
    protected static WatchEvent Event(
        WatchEventKind kind,
        SnapshotObject target,
        DateTimeOffset? detectedAt,
        Severity? severity = null,
        AdMng.Watchdog.Contracts.Events.Attribution? attribution = null,
        IReadOnlyList<string>? tags = null) => Event(kind, target) with
    {
        EventId = Guid.NewGuid(),
        DetectedAt = detectedAt ?? Now,
        Severity = severity ?? EventKindCatalog.DefaultSeverity(kind, target.Kind),
        Attribution = attribution ?? AdMng.Watchdog.Contracts.Events.Attribution.Pending,
        Tags = tags ?? [],
    };

    /// <summary>A resolved attribution with an actor, workstation and IP address.</summary>
    protected static AdMng.Watchdog.Contracts.Events.Attribution Resolved(string account, string sid, string? workstation = null, string? ip = null) => new()
    {
        Status = AttributionStatus.Resolved,
        Confidence = AttributionConfidence.High,
        Actor = new PrincipalRef { Account = account, Sid = sid, DisplayName = account },
        Workstation = workstation,
        IpAddress = ip,
        SourceMethod = "4624",
        SourceConfidence = AttributionConfidence.Medium,
        ResolvedAt = Now,
    };

    protected static PendingNotification Pending(long eventId, string ruleId, NotificationTarget target, bool followUp = false) => new()
    {
        EventId = eventId,
        RuleId = ruleId,
        Target = target,
        Severity = Severity.Medium,
        BucketKey = ruleId + "|" + target.Key,
        DueAt = Now.AddMinutes(5),
        IsFollowUp = followUp,
        CreatedAt = Now,
    };

    protected static NotificationTarget EmailTarget(params string[] recipients) => new()
    {
        ChannelType = DeliveryChannelType.Email,
        ChannelId = "email",
        Recipients = recipients,
        DetailLevel = DetailLevel.Full,
    };

    protected static NotificationTarget WebhookTarget(string channelId = "siem") => new()
    {
        ChannelType = DeliveryChannelType.Webhook,
        ChannelId = channelId,
        DetailLevel = DetailLevel.Minimal,
    };

    protected static OutboxMessage Message(
        string channelId = "siem",
        DeliveryKind kind = DeliveryKind.Alert,
        IReadOnlyList<long>? eventIds = null,
        string? ruleId = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? nextAttemptAt = null) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        ChannelType = channelId == "email" ? DeliveryChannelType.Email : DeliveryChannelType.Webhook,
        ChannelId = channelId,
        Kind = kind,
        RuleId = ruleId,
        EventIds = eventIds ?? [],
        Email = channelId == "email"
            ? new RenderedEmail
            {
                Subject = "[AD Watchdog][NISKI] Nowe konto: Zofia Żak (zofia.zak)",
                HtmlBody = "<p>Nowe konto: Zofia Żak</p>",
                TextBody = "Nowe konto: Zofia Żak",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-AdMng-Rule"] = "new-accounts" },
            }
            : null,
        Http = channelId == "email"
            ? null
            : new RenderedHttpMessage { Body = "{\"type\":\"ad.user.created\"}"u8.ToArray(), WebhookEventType = "ad.user.created", Title = "Nowe konto" },
        Recipients = channelId == "email" ? ["jan.kowalski@contoso.test", "łucja.nowak@contoso.test"] : [],
        CreatedAt = createdAt ?? Now,
        NextAttemptAt = nextAttemptAt ?? createdAt ?? Now,
    };

    /// <summary>Asserts that two contract payloads serialize identically (records with lists compare by reference).</summary>
    protected static void AssertSameJson<T>(T expected, T actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected, WatchdogJson.Options), JsonSerializer.Serialize(actual, WatchdogJson.Options));

    /// <summary>Stores <paramref name="events"/> in one detection commit and returns them with their ids.</summary>
    protected static async Task<IReadOnlyList<WatchEvent>> StoreEventsAsync(TStore store, params WatchEvent[] events) =>
        await store.CommitAsync(new DetectionCommit { Events = events }, Ct);

    protected static WatchdogConfiguration SampleConfiguration() => new()
    {
        General = new GeneralSettings { InstanceName = "Watchdog – Zakład Główny", PublicBaseUrl = "https://admng-wd.contoso.test:8443" },
        RecipientGroups =
        [
            new RecipientGroup { Id = "it-admins", Name = "Administratorzy IT", Addresses = ["łucja.nowak@contoso.test"] },
            new RecipientGroup { Id = "helpdesk", Name = "Pomoc techniczna", DetailLevel = DetailLevel.Standard },
        ],
        Rules =
        [
            new RuleSettings
            {
                Id = "lockouts",
                Name = "Blokady kont",
                Kinds = [WatchEventKind.AccountLockedOut],
                Delivery = new RuleDelivery { Immediate = true, RecipientGroupIds = ["helpdesk"], RepeatSuppression = TimeSpan.FromHours(1) },
            },
        ],
    };
}
