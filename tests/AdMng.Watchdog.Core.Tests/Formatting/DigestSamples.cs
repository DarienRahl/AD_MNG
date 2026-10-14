using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Notifications.Digest;

namespace AdMng.Watchdog.Core.Tests.Formatting;

/// <summary>Polish digest models for the digest formatter tests.</summary>
internal static class DigestSamples
{
    public static readonly DateTimeOffset PeriodEnd = new(2026, 10, 14, 5, 30, 0, TimeSpan.Zero);   // 07:30 in Warsaw

    public static DigestModel Full(int newAccounts = 3, int maxRows = 50)
    {
        var created = Enumerable.Range(1, newAccounts).Select(i => FormattingSamples.UserCreated(i) with { DetectedAt = PeriodEnd.AddMinutes(-i) }).ToList();
        var attributes = FormattingSamples.AttributesChanged();
        var lockout = FormattingSamples.LockedOut() with { DetectedAt = PeriodEnd.AddHours(-2), Lockout = FormattingSamples.LockedOut().Lockout! with { LockedAt = PeriodEnd.AddHours(-2) } };
        var critical = FormattingSamples.PrivilegeGranted();
        var acknowledged = FormattingSamples.PrivilegeGranted() with
        {
            Id = 18350,
            Severity = Severity.High,
            AcknowledgedAt = PeriodEnd.AddHours(-1),
            AcknowledgedBy = @"CONTOSO\adm.wójcik",
        };
        var exclusionRule = FormattingSamples.Rule("account-attributes", "Zmiany parametrów kont") with
        {
            Exclusions =
            [
                new RuleExclusion
                {
                    Id = "hr-sync", Actor = "S-1-5-21-1004336348-1177238915-682003330-3105", Attribute = "department",
                    Reason = "Synchronizacja kadr (HR)", CreatedBy = @"CONTOSO\adm.wójcik", CreatedAt = PeriodEnd.AddDays(-20),
                },
            ],
        };

        return new DigestModel
        {
            PeriodStart = PeriodEnd.AddDays(-1),
            PeriodEnd = PeriodEnd,
            Domain = "contoso.com",
            IsWeekly = true,
            Health = new DigestHealth
            {
                DirectoryState = HealthState.Ok,
                DcHostName = "DC01.contoso.com",
                LastPollAt = PeriodEnd.AddSeconds(-15),
                AttributionCoverage = 0.97,
                BrokenChannels = ["Teams – dział IT"],
            },
            Counts = new DigestCounts
            {
                Total = newAccounts + 4, Critical = 1, High = 1, NewUsers = newAccounts, Lockouts = 1, Attention = 2, Unacknowledged = 1,
            },
            Attention = [critical, acknowledged],
            Sections =
            [
                new DigestSection
                {
                    Kind = DigestSectionKind.NewAccounts, Total = created.Count,
                    Rows = [.. created.Take(maxRows).Select(e => new DigestEventRow(e))],
                },
                new DigestSection
                {
                    Kind = DigestSectionKind.AccountParameters, Total = 1,
                    Rows = [new DigestObjectRow(attributes.Object, [attributes])],
                },
                new DigestSection
                {
                    Kind = DigestSectionKind.Lockouts, Total = 1,
                    Rows = [new DigestLockoutRow(lockout.Object, new DateOnly(2026, 10, 14), [lockout], 1, lockout.Lockout!.Sources)],
                },
                new DigestSection
                {
                    Kind = DigestSectionKind.UpcomingExpiry, Total = 1,
                    Rows =
                    [
                        new DigestExpiryRow(new UpcomingExpiry
                        {
                            Object = FormattingSamples.Jan with { DisplayName = "Żaneta Kołodziej", SamAccountName = "z.kolodziej" },
                            Kind = ExpiryKind.Account,
                            ExpiresAt = PeriodEnd.AddDays(5),
                        }),
                    ],
                },
                new DigestSection
                {
                    Kind = DigestSectionKind.InactiveAccounts, Total = 15,
                    Facts = [new DigestFact("InactiveUsers", 12), new DigestFact("InactiveComputers", 3), new DigestFact("InactivePrivileged", 1)],
                    Rows =
                    [
                        new DigestInactiveRow(new InactiveAccount(
                            new ObjectRef { Dn = "CN=Stary Użytkownik,OU=Księgowość,OU=Firma,DC=contoso,DC=com", Name = "Stary Użytkownik", SamAccountName = "s.uzytkownik", Kind = WatchObjectKind.User },
                            null, true)),
                    ],
                },
                new DigestSection
                {
                    Kind = DigestSectionKind.Hygiene,
                    Facts = [new DigestFact("PasswordNeverExpires", 4), new DigestFact("KrbtgtPasswordAgeDays", null)],
                },
                new DigestSection { Kind = DigestSectionKind.ActiveExceptions, Total = 1, Rows = [new DigestExceptionRow(exclusionRule, exclusionRule.Exclusions[0])] },
            ],
            EventIds = [.. created.Select(e => e.EventId), critical.EventId],
            ShownEventIds = [.. created.Select(e => e.Id), critical.Id],
        };
    }

    public static DigestModel Empty() => new()
    {
        PeriodStart = PeriodEnd.AddDays(-1),
        PeriodEnd = PeriodEnd,
        Domain = "contoso.com",
        Health = new DigestHealth { DirectoryState = HealthState.Ok, DcHostName = "DC01.contoso.com" },
    };
}
