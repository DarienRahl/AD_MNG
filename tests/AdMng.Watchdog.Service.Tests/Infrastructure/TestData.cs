using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Tests.Infrastructure;

/// <summary>Polish sample data for service tests.</summary>
public static class TestData
{
    public const string Domain = "contoso.test";

    public static readonly Guid ZofiaGuid = Guid.Parse("9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f");

    public const string ZofiaDn = "CN=Zofia Żak,OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test";

    public const string ZofiaSid = "S-1-5-21-1004336348-1177238915-682003330-1106";

    public static ObjectRef Zofia => new()
    {
        ObjectGuid = ZofiaGuid,
        Dn = ZofiaDn,
        Name = "Zofia Żak",
        DisplayName = "Zofia Żak",
        SamAccountName = "zofia.zak",
        Sid = ZofiaSid,
        Kind = WatchObjectKind.User,
    };

    public static ObjectRef User(string cn, string ou = "OU=Pracownicy,DC=contoso,DC=test") => new()
    {
        ObjectGuid = Guid.NewGuid(),
        Dn = $"CN={cn},{ou}",
        Name = cn,
        DisplayName = cn,
        SamAccountName = cn.ToLowerInvariant().Replace(' ', '.'),
        Kind = WatchObjectKind.User,
    };

    public static WatchEvent Event(
        WatchEventKind kind,
        ObjectRef? target = null,
        DateTimeOffset? detectedAt = null,
        Severity? severity = null,
        Attribution? attribution = null,
        IReadOnlyList<AttributeChange>? changes = null)
    {
        var obj = target ?? Zofia;
        return new WatchEvent
        {
            EventId = Guid.CreateVersion7(),
            ChangeSetId = Guid.CreateVersion7(),
            Kind = kind,
            Severity = severity ?? EventKindCatalog.DefaultSeverity(kind, obj.Kind),
            Object = obj,
            Domain = Domain,
            DetectedAt = detectedAt ?? TestStore.Start,
            Source = DetectionSource.IncrementalPoll,
            Attribution = attribution ?? Attribution.Pending,
            Changes = changes ?? [],
        };
    }

    public static Attribution Resolved(string account = @"CONTOSO\adm.łukasz", string sid = "S-1-5-21-1004336348-1177238915-682003330-1104") => new()
    {
        Status = AttributionStatus.Resolved,
        Confidence = AttributionConfidence.High,
        Actor = new PrincipalRef { Account = account, Sid = sid, DisplayName = "Łukasz Wiśniewski" },
        Workstation = "WS-ŁÓDŹ-12",
        IpAddress = "10.0.12.34",
        SourceMethod = "4624",
        SourceConfidence = AttributionConfidence.Medium,
    };

    public static OutboxMessage Message(
        DeliveryChannelType type = DeliveryChannelType.Webhook,
        string channelId = "siem",
        IReadOnlyList<long>? eventIds = null,
        DateTimeOffset? createdAt = null) => new()
    {
        MessageId = Guid.CreateVersion7().ToString("N"),
        ChannelType = type,
        ChannelId = channelId,
        Kind = DeliveryKind.Alert,
        EventIds = eventIds ?? [],
        Recipients = type == DeliveryChannelType.Email ? ["łucja.nowak@contoso.test"] : [],
        Email = type == DeliveryChannelType.Email
            ? new RenderedEmail { Subject = "[AD Watchdog][NISKI] Nowe konto: Zofia Żak", HtmlBody = "<p>Zofia Żak</p>", TextBody = "Zofia Żak" }
            : null,
        Http = type == DeliveryChannelType.Email ? null : new RenderedHttpMessage { Body = "{\"type\":\"ad.user.created\",\"name\":\"Zofia Żak\"}"u8.ToArray(), Title = "Nowe konto" },
        CreatedAt = createdAt ?? TestStore.Start,
        NextAttemptAt = createdAt ?? TestStore.Start,
    };
}
