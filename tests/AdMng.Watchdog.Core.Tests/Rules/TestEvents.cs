using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Rules;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Rules;

/// <summary>Watch events with Polish sample data for the rules tests.</summary>
internal static class TestEvents
{
    public const string DomainDn = "DC=contoso,DC=test";
    public const string DomainSid = "S-1-5-21-1004336348-1177238915-682003330";
    public const string UserDn = "CN=Łukasz Wiśniewski,OU=Kraków,DC=contoso,DC=test";
    public const string ComputerDn = "CN=KRK-WS01,OU=Komputery,OU=Kraków,DC=contoso,DC=test";
    public const string AdminSid = DomainSid + "-1104";
    public const string SyncSid = DomainSid + "-1601";

    public static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    public static readonly Guid UserGuid = Guid.Parse("4fe80a66-5f93-4f73-b215-68678058e613");

    public static readonly ConfigTokens Tokens = new() { DomainDn = DomainDn, DomainSid = DomainSid, RootDomainSid = DomainSid };

    public static WatchEvent Create(WatchEventKind kind, string dn = UserDn, WatchObjectKind objectKind = WatchObjectKind.User,
        Severity? severity = null, EventAttribution? attribution = null, IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? changedAttributes = null, DateTimeOffset? detectedAt = null, Guid? objectGuid = null, string? sid = null) => new()
    {
        EventId = Guid.CreateVersion7(),
        Kind = kind,
        Severity = severity ?? EventKindCatalog.DefaultSeverity(kind, objectKind),
        Object = new ObjectRef
        {
            Dn = dn,
            Kind = objectKind,
            ObjectGuid = objectGuid ?? UserGuid,
            Sid = sid ?? DomainSid + "-6609",
            Name = dn.Contains('=', StringComparison.Ordinal) ? dn.Split(',')[0].Split('=', 2)[1] : null,
            SamAccountName = "lwisniewski",
        },
        Changes = [.. (changedAttributes ?? []).Select(a => new AttributeChange { Attribute = a, NewValues = ["nowa wartość"] })],
        DetectedAt = detectedAt ?? Now,
        Source = DetectionSource.IncrementalPoll,
        Attribution = attribution ?? EventAttribution.Pending,
        Tags = tags ?? [],
    };

    public static WatchEvent Membership(WatchEventKind kind, string groupDn, string? groupSid = null, string? groupSam = null) =>
        Create(kind, groupDn, WatchObjectKind.Group, sid: groupSid ?? DomainSid + "-1500") with
        {
            Object = new ObjectRef { Dn = groupDn, Kind = WatchObjectKind.Group, Sid = groupSid ?? DomainSid + "-1500", SamAccountName = groupSam, ObjectGuid = Guid.NewGuid() },
            Member = new ObjectRef { Dn = UserDn, Kind = WatchObjectKind.User, ObjectGuid = UserGuid },
        };

    public static EventAttribution Resolved(string sid, string account = "CONTOSO\\adm.jkowalska") => new()
    {
        Status = AttributionStatus.Resolved,
        Confidence = AttributionConfidence.High,
        Actor = new PrincipalRef { Sid = sid, Account = account },
        ResolvedAt = Now,
    };

    public static EventAttribution Unknown(string reason = AttributionReasons.NoMatch) => new()
    {
        Status = AttributionStatus.Unknown,
        Reason = reason,
    };
}
