using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Formatting;

/// <summary>Polish sample events and configurations for the formatter tests.</summary>
internal static class FormattingSamples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 32, 5, TimeSpan.Zero);

    /// <summary>Right-to-left override – must never reach a notification raw.</summary>
    public const string Rlo = "‮";

    public const string HostileDescription = "<script>alert('x')</script> [Kliknij](https://evil.example/x) " + Rlo + "gpj.exe www.zly.example";

    public static WatchdogConfiguration Configuration(string language = "pl", string? publicBaseUrl = "https://admng-wd.contoso.com:8443") => new()
    {
        General = new GeneralSettings { Language = language, TimeZone = "Europe/Warsaw", PublicBaseUrl = publicBaseUrl, InstanceName = "AD Watchdog" },
    };

    public static NotificationContext Context(string language = "pl", DetailLevel level = DetailLevel.Full, string? publicBaseUrl = "https://admng-wd.contoso.com:8443") =>
        NotificationContext.Create(Configuration(language, publicBaseUrl), level);

    public static RuleSettings Rule(string id = "tier0-privilege", string name = "Dostęp uprzywilejowany", bool builtIn = true) =>
        new() { Id = id, Name = name, BuiltIn = builtIn };

    public static ObjectRef Jan => new()
    {
        ObjectGuid = Guid.Parse("6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7"),
        Dn = "CN=Jan Kowalski,OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=com",
        Name = "Jan Kowalski",
        DisplayName = "Jan Kowalski",
        SamAccountName = "jan.kowalski",
        Sid = "S-1-5-21-1004336348-1177238915-682003330-1107",
        Kind = WatchObjectKind.User,
    };

    public static ObjectRef DomainAdmins => new()
    {
        ObjectGuid = Guid.Parse("0f1e2d3c-4b5a-4968-8776-655443322110"),
        Dn = "CN=Domain Admins,CN=Users,DC=contoso,DC=com",
        Name = "Domain Admins",
        SamAccountName = "Domain Admins",
        Sid = "S-1-5-21-1004336348-1177238915-682003330-512",
        Kind = WatchObjectKind.Group,
    };

    public static EventAttribution ResolvedNowak => new()
    {
        Status = AttributionStatus.Resolved,
        Confidence = AttributionConfidence.High,
        Actor = new PrincipalRef { Account = @"CONTOSO\adm.nowak", DisplayName = "Anna Nowak", Sid = "S-1-5-21-1004336348-1177238915-682003330-1104" },
        Workstation = "WS-ADM-01",
        IpAddress = "10.0.12.34",
        SourceMethod = "dns",
        LogonType = 3,
        SourceConfidence = AttributionConfidence.Low,
        Evidence = [new EvidenceRef { Dc = "DC01.contoso.com", EventCode = 4728, RecordId = 918273, TimeCreated = Now }],
        ResolvedAt = Now,
    };

    public static WatchEvent PrivilegeGranted(EventAttribution? attribution = null) => new()
    {
        Id = 18342,
        EventId = Guid.Parse("0192f3a4-5b6c-7d8e-9f00-112233445566"),
        Kind = WatchEventKind.PrivilegeGranted,
        Severity = Severity.Critical,
        Object = Jan,
        Member = DomainAdmins,
        Via = [new ObjectRef { Dn = "CN=GG_Admins_Serwery,OU=Grupy,DC=contoso,DC=com", Name = "GG_Admins_Serwery", Kind = WatchObjectKind.Group }, DomainAdmins],
        Domain = "contoso.com",
        DetectedAt = Now,
        OriginatingTime = Now,
        OriginatingDc = "DC01.contoso.com",
        Source = DetectionSource.IncrementalPoll,
        Attribution = attribution ?? ResolvedNowak,
        MatchedRules = ["tier0-privilege"],
    };

    public static WatchEvent AttributesChanged(EventAttribution? attribution = null) => new()
    {
        Id = 18343,
        EventId = Guid.Parse("0192f3a4-5b6c-7d8e-9f00-112233445567"),
        Kind = WatchEventKind.AttributesChanged,
        Severity = Severity.Info,
        Object = Jan with { Name = "Łukasz Wiśniewski", DisplayName = "Łukasz Wiśniewski", SamAccountName = "lwisniewski",
            Dn = "CN=Łukasz Wiśniewski,OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=com" },
        Changes =
        [
            new AttributeChange { Attribute = "department", OldValues = ["Sprzedaż"], NewValues = ["Księgowość"], OldCount = 1, NewCount = 1 },
            new AttributeChange
            {
                Attribute = "userAccountControl", ValueKind = AttributeValueKind.Flags, OldValues = ["512"], NewValues = ["514"],
                FlagsAdded = ["ACCOUNTDISABLE"], OldCount = 1, NewCount = 1,
            },
            new AttributeChange { Attribute = "unicodePwd", Redacted = true, OldCount = 1, NewCount = 1 },
            new AttributeChange { Attribute = "mobile", OldValues = ["+48 600 100 200"], NewValues = ["+48 600 100 201"], OldCount = 1, NewCount = 1 },
            new AttributeChange { Attribute = "description", OldValues = [], NewValues = [HostileDescription], OldCount = 0, NewCount = 1 },
        ],
        Domain = "contoso.com",
        DetectedAt = Now,
        OriginatingTime = Now.AddSeconds(-30),
        OriginatingDc = "dc01.contoso.com",
        Source = DetectionSource.IncrementalPoll,
        Attribution = attribution ?? ResolvedNowak,
        MatchedRules = ["account-attributes"],
    };

    public static WatchEvent UserCreated(int index, EventAttribution? attribution = null) => new()
    {
        Id = 20000 + index,
        EventId = Guid.CreateVersion7(Now.AddSeconds(index)),
        Kind = WatchEventKind.UserCreated,
        Severity = Severity.Low,
        Object = new ObjectRef
        {
            ObjectGuid = Guid.NewGuid(),
            Dn = $"CN=Zażółć Gęślą {index:000},OU=Sprzedaż,DC=contoso,DC=com",
            Name = $"Zażółć Gęślą {index:000}",
            DisplayName = $"Zażółć Gęślą Jaźń {index:000}",
            SamAccountName = $"zgj{index:000}",
            Kind = WatchObjectKind.User,
        },
        Domain = "contoso.com",
        DetectedAt = Now.AddSeconds(index),
        Source = DetectionSource.IncrementalPoll,
        Attribution = attribution ?? ResolvedNowak,
    };

    public static WatchEvent LockedOut() => new()
    {
        Id = 18400,
        EventId = Guid.Parse("0192f3a4-5b6c-7d8e-9f00-112233445570"),
        Kind = WatchEventKind.AccountLockedOut,
        Severity = Severity.Low,
        Object = Jan,
        Lockout = new LockoutInfo
        {
            LockedAt = Now,
            LockedUntil = Now.AddMinutes(30),
            Sources = [new LockoutSource("10.0.0.12", "WS-ŻÓŁW-01", 5, 4771), new LockoutSource(null, "LAPTOP-ŁĄCKA", 2, 4776)],
            LockoutsToday = 3,
        },
        Domain = "contoso.com",
        DetectedAt = Now,
        Source = DetectionSource.IncrementalPoll,
        Attribution = EventAttribution.NotApplicable,
    };
}
