using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Notifications.Digest;
using AdMng.Watchdog.Core.Tests.Notifications.Fakes;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class DigestBuilderTests
{
    private static readonly DateTimeOffset PeriodStart = Now.AddHours(-24);

    private static readonly RuleSettings NewAccounts = Rule("new-accounts", "Nowe konta", immediate: false);
    private static readonly RuleSettings Lockouts = Rule("lockouts", "Blokady kont", immediate: false);
    private static readonly RuleSettings Passwords = Rule("passwords", "Hasła", immediate: false);
    private static readonly RuleSettings Attributes = Rule("account-attributes", "Zmiany parametrów kont", immediate: false);
    private static readonly RuleSettings NoDigest = Rule("only-immediate") with { Delivery = new RuleDelivery { Immediate = true, IncludeInDigest = false } };

    private readonly FakeDigestReports _reports = new();

    [Fact]
    public void Period_FirstDigestCoversTheLast24Hours_ThenStartsWhereThePreviousEnded()
    {
        var configuration = ConfigWith();
        var calendar = BusinessCalendar.For(configuration);

        Assert.Equal((Now.AddHours(-24), false), DigestBuilder.ResolvePeriodStart(configuration, calendar, null, Now));
        Assert.Equal((Now.AddHours(-30), false), DigestBuilder.ResolvePeriodStart(configuration, calendar, Now.AddHours(-30), Now));
        Assert.Equal((Now.AddHours(-24), false), DigestBuilder.ResolvePeriodStart(configuration, calendar, Now.AddMinutes(1), Now));
    }

    [Fact]
    public void Period_MondayAfterAWeekend_CoversFridayToMonday()
    {
        var configuration = ConfigWith();
        var monday = new DateTimeOffset(2026, 10, 19, 5, 30, 0, TimeSpan.Zero);          // 07:30 in Warsaw
        var sunday = monday.AddDays(-1);

        var (start, covers) = DigestBuilder.ResolvePeriodStart(configuration, BusinessCalendar.For(configuration), sunday, monday);
        var (saturdayStart, saturdayCovers) = DigestBuilder.ResolvePeriodStart(configuration, BusinessCalendar.For(configuration), monday.AddDays(-3), monday.AddDays(-2));

        Assert.Equal((new DateTimeOffset(2026, 10, 16, 5, 30, 0, TimeSpan.Zero), true), (start, covers));
        Assert.Equal((monday.AddDays(-3), false), (saturdayStart, saturdayCovers));
    }

    [Fact]
    public void Period_SendNowOnMondayAfterTheMondayDigest_DoesNotCoverTheWeekendAgain()
    {
        var configuration = ConfigWith();
        var calendar = BusinessCalendar.For(configuration);
        var mondayDigest = new DateTimeOffset(2026, 10, 19, 5, 30, 0, TimeSpan.Zero);     // 07:30 in Warsaw
        var mondayAfternoon = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero); // 14:00

        Assert.Equal((mondayDigest, false), DigestBuilder.ResolvePeriodStart(configuration, calendar, mondayDigest, mondayAfternoon));
    }

    [Fact]
    public void Period_SendNowEarlyOnMonday_IsNotCoveredAgainByTheScheduledDigest()
    {
        var configuration = ConfigWith();
        var calendar = BusinessCalendar.For(configuration);
        var sundayDigest = new DateTimeOffset(2026, 10, 18, 5, 30, 0, TimeSpan.Zero);
        var mondayEarly = new DateTimeOffset(2026, 10, 19, 4, 0, 0, TimeSpan.Zero);      // 06:00, before the 07:30 digest
        var mondayDigest = new DateTimeOffset(2026, 10, 19, 5, 30, 0, TimeSpan.Zero);

        Assert.Equal((new DateTimeOffset(2026, 10, 16, 5, 30, 0, TimeSpan.Zero), true),
            DigestBuilder.ResolvePeriodStart(configuration, calendar, sundayDigest, mondayEarly));
        Assert.Equal((mondayEarly, false), DigestBuilder.ResolvePeriodStart(configuration, calendar, mondayEarly, mondayDigest));
    }

    [Fact]
    public void Period_MondayAfterALateFridayDigest_StartsWhereFridaysDigestEnded()
    {
        var configuration = ConfigWith();
        var calendar = BusinessCalendar.For(configuration);
        var lateFriday = new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);       // catch-up at 10:00 in Warsaw
        var monday = new DateTimeOffset(2026, 10, 19, 5, 30, 0, TimeSpan.Zero);

        // Weekday schedule: Friday's digest is the last one.
        Assert.Equal((lateFriday, false), DigestBuilder.ResolvePeriodStart(configuration, calendar, lateFriday, monday));
        // Weekend digests were sent too: the end of Friday's digest is known to the job.
        Assert.Equal((lateFriday, true), DigestBuilder.ResolvePeriodStart(configuration, calendar, monday.AddDays(-1), monday, lateFriday));
        // A known end from an older business day is not used (the nominal Friday time is).
        Assert.Equal((new DateTimeOffset(2026, 10, 16, 5, 30, 0, TimeSpan.Zero), true),
            DigestBuilder.ResolvePeriodStart(configuration, calendar, monday.AddDays(-1), monday, lateFriday.AddDays(-1)));
    }

    [Fact]
    public void Period_DayAfterAHoliday_CoversTheHoliday_UnlessDisabled()
    {
        var configuration = ConfigWith();
        var thursday = new DateTimeOffset(2026, 11, 12, 6, 30, 0, TimeSpan.Zero);         // 07:30 CET, after 11 November
        var disabled = configuration with { Digest = configuration.Digest with { CoverNonBusinessDays = false } };

        Assert.Equal((new DateTimeOffset(2026, 11, 10, 6, 30, 0, TimeSpan.Zero), true),
            DigestBuilder.ResolvePeriodStart(configuration, BusinessCalendar.For(configuration), thursday.AddDays(-1), thursday));
        Assert.Equal((thursday.AddDays(-1), false), DigestBuilder.ResolvePeriodStart(disabled, BusinessCalendar.For(disabled), thursday.AddDays(-1), thursday));
    }

    [Fact]
    public async Task Sections_OnlyShowEventsOfDigestRules_ButTheCountersSeeEverything()
    {
        var harness = new NotificationHarness(ConfigWith(NewAccounts, NoDigest));
        for (var i = 1; i <= 3; i++)
        {
            await StoreAsync(harness, Event(WatchEventKind.UserCreated, Severity.Low, name: $"Zażółć Gęślą {i}", detectedAt: Now.AddHours(-i)), NewAccounts);
        }

        await StoreAsync(harness, Event(WatchEventKind.UserCreated, Severity.Low, name: "Poza raportem", detectedAt: Now.AddHours(-1)), NoDigest);
        await StoreAsync(harness, Event(WatchEventKind.UserCreated, Severity.Low, name: "Za stare", detectedAt: Now.AddHours(-25)), NewAccounts);

        var model = await BuildAsync(harness);

        var section = Assert.Single(model.Sections, s => s.Kind == DigestSectionKind.NewAccounts);
        Assert.Equal(3, section.Total);
        Assert.Equal(["Zażółć Gęślą 3", "Zażółć Gęślą 2", "Zażółć Gęślą 1"], section.Rows.Cast<DigestEventRow>().Select(r => r.Event.Object.Name));
        Assert.Equal((4, 4), (model.Counts.Total, model.Counts.NewUsers));
        Assert.Equal(3, model.EventIds.Count);
        Assert.Equal(3, model.ShownEventIds.Count);
        Assert.Equal("contoso.com", model.Domain);
    }

    [Fact]
    public async Task Sections_AreCappedAtMaxRowsPerSection()
    {
        var harness = new NotificationHarness(ConfigWith(NewAccounts) with { Digest = new DigestSettings { MaxRowsPerSection = 2 } });
        for (var i = 0; i < 5; i++)
        {
            await StoreAsync(harness, Event(WatchEventKind.UserCreated, Severity.Low, detectedAt: Now.AddMinutes(-10 - i)), NewAccounts);
        }

        var section = (await BuildAsync(harness)).Section(DigestSectionKind.NewAccounts)!;

        Assert.Equal((5, 2, 3), (section.Total, section.Rows.Count, section.MoreCount));
    }

    [Fact]
    public async Task Attention_ListsEventsOfHighOrAbove_MostSevereFirst_WithTheirAcknowledgement()
    {
        var harness = new NotificationHarness(ConfigWith(NewAccounts));
        var high = await StoreAsync(harness, Event(WatchEventKind.PrivilegeGranted, Severity.High, detectedAt: Now.AddHours(-3)));
        var critical = await StoreAsync(harness, Event(WatchEventKind.PrivilegeGranted, Severity.Critical, detectedAt: Now.AddHours(-2)));
        await StoreAsync(harness, Event(WatchEventKind.UserCreated, Severity.Medium, detectedAt: Now.AddHours(-1)));
        await harness.Store.AcknowledgeAsync(high.Id, @"CONTOSO\adm.wójcik", "Sprawdzone – zgłoszenie 4711", Now.AddHours(-1), Ct);

        var model = await BuildAsync(harness);

        Assert.Equal([critical.Id, high.Id], model.Attention.Select(e => e.Id));
        Assert.NotNull(model.Attention[1].AcknowledgedAt);
        Assert.Equal((2, 1, 1, 1), (model.Counts.Attention, model.Counts.Unacknowledged, model.Counts.Critical, model.Counts.High));
        Assert.Contains(critical.EventId, model.EventIds);
    }

    [Fact]
    public async Task Attention_SaysWhenAnEscalationRaisedTheSeverityAfterTheAlert()
    {
        // Shadow credentials on Zofia's account went out as Medium, the escalation said High (13.2 #7); Jędrzej's privilege
        // grant was High from the start – its follow-up only brought the actor; Anna's escalation never reached anybody.
        var harness = new NotificationHarness(ConfigWith(NewAccounts));
        var zofia = await StoreAsync(harness, Event(WatchEventKind.KeyCredentialChanged, Severity.High, name: "Zofia Ćwiklińska", detectedAt: Now.AddHours(-3)));
        var jedrzej = await StoreAsync(harness, Event(WatchEventKind.PrivilegeGranted, Severity.High, name: "Jędrzej Łęcki", detectedAt: Now.AddHours(-2)));
        var anna = await StoreAsync(harness, Event(WatchEventKind.KeyCredentialChanged, Severity.High, name: "Anna Zając", detectedAt: Now.AddHours(-1)));
        await EnqueueAsync(harness, zofia, DeliveryKind.Alert, Severity.Medium);
        await EnqueueAsync(harness, zofia, DeliveryKind.FollowUp, Severity.High);
        await EnqueueAsync(harness, jedrzej, DeliveryKind.Alert, Severity.High);
        await EnqueueAsync(harness, jedrzej, DeliveryKind.FollowUp, Severity.High);
        await EnqueueAsync(harness, anna, DeliveryKind.Alert, Severity.Medium);
        var lost = await EnqueueAsync(harness, anna, DeliveryKind.FollowUp, Severity.High);
        await harness.Store.LeaseDueAsync(Now, 100, TimeSpan.FromMinutes(5), Ct);
        await harness.Store.MarkDeadAsync(lost, "smtp.550.5.1.1", null, Ct);

        var model = await BuildAsync(harness);

        Assert.Equal([zofia.Id, jedrzej.Id, anna.Id], model.Attention.Select(e => e.Id));
        Assert.Equal(Severity.Medium, Assert.Single(model.SeverityChanges).Value);
        Assert.Equal(zofia.Id, model.SeverityChanges.Single().Key);
    }

    [Fact]
    public async Task Lockouts_AreRolledUpPerAccountAndDay_WithTopSourcesAndUnlocks()
    {
        var harness = new NotificationHarness(ConfigWith(Lockouts));
        var jan = Guid.NewGuid();
        var anna = Guid.NewGuid();
        var yesterdayEvening = new DateTimeOffset(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);  // 22:00 local, 13 October
        for (var i = 0; i < 5; i++)
        {
            await StoreAsync(harness, Lockout(jan, "Jan Kowalski", Now.AddMinutes(-60 + (i * 10)), ("10.0.0.12", "WS-ŻÓŁW-01", 3), ("10.0.0.13", null, 1)), Lockouts);
        }

        await StoreAsync(harness, Lockout(jan, "Jan Kowalski", yesterdayEvening, ("10.0.0.99", "LAPTOP-ŁĄCKA", 5)), Lockouts);
        await StoreAsync(harness, Lockout(anna, "Anna Żółkiewska", Now.AddMinutes(-30), ("10.0.5.1", null, 7)), Lockouts);
        await StoreAsync(harness, Lockout(anna, "Anna Żółkiewska", Now.AddMinutes(-20), ("10.0.5.1", null, 2)), Lockouts);
        await StoreAsync(harness, Event(WatchEventKind.AccountUnlocked, Severity.Info, objectGuid: jan, name: "Jan Kowalski", detectedAt: Now.AddMinutes(-5)), Lockouts);

        var section = (await BuildAsync(harness)).Section(DigestSectionKind.Lockouts)!;

        var rows = section.Rows.Cast<DigestLockoutRow>().ToList();
        Assert.Equal(3, section.Total);
        Assert.Equal([(5, new DateOnly(2026, 10, 14)), (2, new DateOnly(2026, 10, 14)), (1, new DateOnly(2026, 10, 13))], rows.Select(r => (r.Count, r.Day)));
        Assert.Equal(1, rows[0].Unlocks);
        Assert.Equal([("10.0.0.12", 15), ("10.0.0.13", 5)], rows[0].TopSources.Select(s => (s.IpAddress, s.Count)));
        Assert.Equal(("10.0.5.1", 9), (rows[1].TopSources.Single().IpAddress, rows[1].TopSources.Single().Count));
        Assert.Equal(Now.AddMinutes(-20), rows[0].LastAt);
        Assert.Equal(8, (await BuildAsync(harness)).Counts.Lockouts);
    }

    [Fact]
    public async Task Passwords_CountResetsOwnChangesAndAgeResetsSeparately_AndListPrivilegedAccounts()
    {
        var harness = new NotificationHarness(ConfigWith(Passwords));
        await StoreAsync(harness, Event(WatchEventKind.PasswordChanged, Severity.Low, tags: [EventTags.PasswordReset]), Passwords);
        await StoreAsync(harness, Event(WatchEventKind.PasswordChanged, Severity.Low, tags: [EventTags.PasswordReset]), Passwords);
        await StoreAsync(harness, Event(WatchEventKind.PasswordChanged, Severity.Info, tags: [EventTags.SelfChange]), Passwords);
        await StoreAsync(harness, Event(WatchEventKind.PasswordChanged, Severity.Info, new EventAttribution { Status = AttributionStatus.Unknown, Reason = "NO_MATCH" }), Passwords);
        var privileged = await StoreAsync(harness,
            Event(WatchEventKind.PasswordChanged, Severity.High, name: "Administrator", tags: [EventTags.PasswordReset, EventTags.Privileged]), Passwords);
        await StoreAsync(harness, Event(WatchEventKind.PasswordAgeReset, Severity.Medium), Passwords);

        var model = await BuildAsync(harness);

        var section = model.Section(DigestSectionKind.Passwords)!;
        Assert.Equal(
            [("PasswordResets", 3L), ("OwnPasswordChanges", 1L), ("OtherPasswordChanges", 1L), ("PasswordAgeResets", 1L)],
            section.Facts.Select(f => (f.Key, f.Value!.Value)));
        Assert.Equal(privileged.Id, Assert.Single(section.Rows.Cast<DigestEventRow>()).Event.Id);
        Assert.Equal((3, 1, 1, 1), (model.Counts.PasswordResets, model.Counts.OwnPasswordChanges, model.Counts.OtherPasswordChanges, model.Counts.PasswordAgeResets));
    }

    [Fact]
    public async Task NewComputers_AreOrderedByOu_AndUnusualOnesFlagged()
    {
        var newComputers = Rule("new-computers", "Nowe komputery", immediate: false);
        var harness = new NotificationHarness(ConfigWith(newComputers));
        await StoreAsync(harness, Computer("WS-200", "OU=Stacje,OU=Kraków,DC=contoso,DC=com", Now.AddHours(-1)), newComputers);
        await StoreAsync(harness, Computer("WS-100", "CN=Computers,DC=contoso,DC=com", Now.AddHours(-2), EventTags.DefaultContainer), newComputers);
        await StoreAsync(harness, Computer("WS-150", "OU=Stacje,OU=Gdańsk,DC=contoso,DC=com", Now.AddHours(-3), EventTags.CreatedByNonAdmin), newComputers);

        var rows = (await BuildAsync(harness)).Section(DigestSectionKind.NewComputers)!.Rows.Cast<DigestEventRow>().ToList();

        Assert.Equal(["WS-100", "WS-150", "WS-200"], rows.Select(r => r.Event.Object.Name));
        Assert.Equal([true, true, false], rows.Select(r => r.Flagged));
    }

    [Fact]
    public async Task AccountParameters_AreGroupedPerObject()
    {
        var harness = new NotificationHarness(ConfigWith(Attributes));
        var łukasz = Guid.NewGuid();
        await StoreAsync(harness, Changed(łukasz, "Łukasz Wiśniewski", Now.AddHours(-5), "department", "Sprzedaż", "Księgowość"), Attributes);
        await StoreAsync(harness, Changed(Guid.NewGuid(), "Zofia Żak", Now.AddHours(-4), "title", "Specjalista", "Kierownik"), Attributes);
        await StoreAsync(harness, Changed(łukasz, "Łukasz Wiśniewski", Now.AddHours(-3), "telephoneNumber", "+48 12 000 00 01", "+48 12 000 00 02"), Attributes);

        var section = (await BuildAsync(harness)).Section(DigestSectionKind.AccountParameters)!;

        Assert.Equal(2, section.Total);
        var first = Assert.IsType<DigestObjectRow>(section.Rows[0]);
        Assert.Equal(("Łukasz Wiśniewski", 2), (first.Object.Name, first.Events.Count));
        Assert.Equal(["department", "telephoneNumber"], first.Events.Select(e => e.Changes[0].Attribute));
    }

    [Fact]
    public async Task HealthAndConfigurationChanges_AreAlwaysReported()
    {
        var harness = new NotificationHarness(ConfigWith());
        await harness.StoreAsync(HealthEvents.Create(WatchEventKind.HealthEventLogGap, Now.AddHours(-2), ("code", "eventlog.gap"), ("dc", "dc01.contoso.com")));
        await harness.StoreAsync(HealthEvents.Create(WatchEventKind.WatchdogConfigurationChanged, Now.AddHours(-1), ("code", "config.saved")));

        var model = await BuildAsync(harness);

        Assert.Equal([DigestSectionKind.Health, DigestSectionKind.WatchdogConfiguration], model.Sections.Select(s => s.Kind));
        Assert.Equal(1, model.Health.EventLogGaps);
    }

    [Fact]
    public async Task HealthLine_ComesFromTheRegistry()
    {
        var harness = new NotificationHarness(ConfigWith());
        harness.Health.ReportDirectory(new DirectoryStatusDto
        {
            State = HealthState.Ok, DcHostName = "dc01.contoso.com", LastPollAt = Now.AddSeconds(-15), BaselineCompletedAt = Now.AddDays(-2),
        });
        harness.Health.ReportAttribution(new AttributionStatusDto
        {
            Enabled = true,
            DomainControllers =
            [
                new DcLogStatusDto { Host = "dc01.contoso.com", State = HealthState.Ok, Coverage24h = 0.97 },
                new DcLogStatusDto { Host = "dc02.contoso.com", State = HealthState.Ok, Coverage24h = 0.81 },
            ],
        });
        harness.Health.ReportChannel(new ChannelHealthDto { ChannelId = TeamsChannel, ChannelType = DeliveryChannelType.Teams, State = HealthState.Error });
        harness.Health.ReportChannel(new ChannelHealthDto { ChannelId = "email", ChannelType = DeliveryChannelType.Email, State = HealthState.Ok });

        var model = await BuildAsync(harness);

        Assert.Equal((HealthState.Ok, "dc01.contoso.com", Now.AddSeconds(-15)), (model.Health.DirectoryState, model.Health.DcHostName, model.Health.LastPollAt));
        Assert.Equal(0.81, model.Health.AttributionCoverage);
        Assert.Equal(["Teams – dział IT"], model.Health.BrokenChannels);
        Assert.Equal(Now.AddDays(5), model.Health.RolloutUntil);
        Assert.Equal("contoso.com", model.Domain);
        Assert.True(model.IsEmpty);
    }

    [Fact]
    public async Task WeeklySections_AreAddedOnTheirDays()
    {
        var monday = new DateTimeOffset(2026, 10, 19, 5, 30, 0, TimeSpan.Zero);
        var harness = new NotificationHarness(ConfigWith());
        _reports.Inactive = new InactiveAccountsResult
        {
            Users = 12, Computers = 3, Privileged = 1,
            Rows = [new InactiveAccount(new ObjectRef { Dn = "CN=Stary Użytkownik,OU=Firma,DC=contoso,DC=com", Name = "Stary Użytkownik", Kind = WatchObjectKind.User }, null, true)],
        };
        _reports.Hygiene = new HygieneCounters { PasswordNeverExpires = 4, KrbtgtPasswordAgeDays = 400 };

        var weekly = await BuildAsync(harness, monday.AddDays(-1), monday);
        var tuesday = await BuildAsync(harness, monday, monday.AddDays(1));

        Assert.True(weekly.IsWeekly);
        Assert.Equal((15, 1), (weekly.Section(DigestSectionKind.InactiveAccounts)!.Total, weekly.Section(DigestSectionKind.InactiveAccounts)!.Rows.Count));
        Assert.Equal(400, weekly.Section(DigestSectionKind.Hygiene)!.Facts.Single(f => f.Key == "KrbtgtPasswordAgeDays").Value);
        Assert.False(tuesday.IsWeekly);
        Assert.Null(tuesday.Section(DigestSectionKind.Hygiene));
        Assert.Equal((1, 1), (_reports.InactiveQueries, _reports.HygieneQueries));
    }

    [Fact]
    public async Task WeeklySections_SayWhenTheDirectoryCouldNotBeQueried()
    {
        var monday = new DateTimeOffset(2026, 10, 19, 5, 30, 0, TimeSpan.Zero);
        var harness = new NotificationHarness(ConfigWith());
        _reports.Inactive = null;
        _reports.Hygiene = null;

        var model = await BuildAsync(harness, monday.AddDays(-1), monday);

        Assert.All(model.Section(DigestSectionKind.Hygiene)!.Facts, f => Assert.Null(f.Value));
        Assert.All(model.Section(DigestSectionKind.InactiveAccounts)!.Facts, f => Assert.Null(f.Value));
    }

    [Fact]
    public async Task UpcomingExpiries_AndActiveExceptions_AreListed()
    {
        var exclusionRule = Rule("account-attributes", immediate: false) with
        {
            Exclusions =
            [
                new RuleExclusion { Id = "x1", Actor = "S-1-5-21-1004336348-1177238915-682003330-3105", Reason = "Konto synchronizacji HR", CreatedBy = @"CONTOSO\adm.wójcik", CreatedAt = Now.AddDays(-10) },
                new RuleExclusion { Id = "x2", Attribute = "mobile", Reason = "Wygasły wyjątek", ExpiresAt = Now.AddDays(-1), CreatedBy = @"CONTOSO\adm.wójcik", CreatedAt = Now.AddDays(-30) },
            ],
        };
        var harness = new NotificationHarness(ConfigWith(exclusionRule));
        var soon = new UpcomingExpiry { Object = new ObjectRef { Dn = "CN=Ewa Łoś,DC=contoso,DC=com", Name = "Ewa Łoś", Kind = WatchObjectKind.User }, Kind = ExpiryKind.Account, ExpiresAt = Now.AddDays(3) };
        var later = soon with { Kind = ExpiryKind.Password, ExpiresAt = Now.AddDays(30) };
        await harness.Store.ReplaceUpcomingAsync(ExpiryKind.Account, [soon], Ct);
        await harness.Store.ReplaceUpcomingAsync(ExpiryKind.Password, [later], Ct);

        var model = await BuildAsync(harness);

        Assert.Equal(soon.ExpiresAt, Assert.IsType<DigestExpiryRow>(Assert.Single(model.Section(DigestSectionKind.UpcomingExpiry)!.Rows)).Expiry.ExpiresAt);
        Assert.Equal("x1", Assert.IsType<DigestExceptionRow>(Assert.Single(model.Section(DigestSectionKind.ActiveExceptions)!.Rows)).Exclusion.Id);
    }

    private DigestBuilder Builder(NotificationHarness harness) =>
        new(harness.Store, harness.Store, harness.Health, harness.Rollout, _reports, harness.Calendars, harness.Store);

    /// <summary>An alert or follow-up of <paramref name="e"/> to the it-admins mailbox that showed it with
    /// <paramref name="severity"/>; returns the outbox id.</summary>
    private static Task<long> EnqueueAsync(NotificationHarness harness, WatchEvent e, DeliveryKind kind, Severity severity)
    {
        var target = AdminsTarget(harness.Current);
        return harness.Store.EnqueueAsync(new OutboxMessage
        {
            MessageId = Guid.CreateVersion7().ToString("N"),
            ChannelType = target.ChannelType,
            ChannelId = target.ChannelId,
            TargetKey = target.Key,
            Kind = kind,
            Recipients = target.Recipients,
            EventIds = [e.Id],
            EventStates = [new OutboxEventState(e.Id, severity, kind == DeliveryKind.Alert)],
            Severity = severity,
            SentWithPendingAttribution = kind == DeliveryKind.Alert,
            CreatedAt = Now,
            NextAttemptAt = Now,
        }, Ct);
    }

    /// <summary>Builds [start, end); by default the 24 hours up to one minute after <see cref="NotificationHarness.Now"/>
    /// (events default to <c>Now</c>).</summary>
    private Task<DigestModel> BuildAsync(NotificationHarness harness, DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        Builder(harness).BuildAsync(harness.Current, start ?? PeriodStart, end ?? Now.AddMinutes(1), false, Ct);

    private static Task<WatchEvent> StoreAsync(NotificationHarness harness, WatchEvent watchEvent, RuleSettings? rule = null) =>
        harness.StoreAsync(rule is null ? watchEvent : watchEvent with { MatchedRules = [rule.Id] });

    private static WatchEvent Lockout(Guid account, string name, DateTimeOffset at, params (string Ip, string? Workstation, int Count)[] sources) =>
        Event(WatchEventKind.AccountLockedOut, Severity.Low, EventAttribution.NotApplicable, name, account, at) with
        {
            Lockout = new LockoutInfo { LockedAt = at, Sources = [.. sources.Select(s => new LockoutSource(s.Ip, s.Workstation, s.Count, 4771))] },
        };

    private static WatchEvent Computer(string name, string parentDn, DateTimeOffset at, params string[] tags) =>
        Event(WatchEventKind.ComputerCreated, Severity.Low, detectedAt: at, tags: tags) with
        {
            Object = new ObjectRef { ObjectGuid = Guid.NewGuid(), Dn = $"CN={name},{parentDn}", Name = name, SamAccountName = name + "$", Kind = WatchObjectKind.Computer },
        };

    private static WatchEvent Changed(Guid objectGuid, string name, DateTimeOffset at, string attribute, string before, string after) =>
        Event(WatchEventKind.AttributesChanged, Severity.Info, name: name, objectGuid: objectGuid, detectedAt: at) with
        {
            Changes = [new AttributeChange { Attribute = attribute, OldValues = [before], NewValues = [after], OldCount = 1, NewCount = 1 }],
        };
}
