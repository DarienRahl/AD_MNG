using System.Globalization;
using AdMng.Watchdog.Contracts.Events;
using static AdMng.Watchdog.Testing.Client.SamplePrincipals;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Testing.Client;

/// <summary>
/// Thirty realistic events of the last two days (ids 1–30, oldest first): every category, every attribution status,
/// lockouts with failed-logon sources, a privilege escalation with its nesting path, expiry, bursts, Security-log-only
/// detections, health and configuration changes – with Polish names.
/// </summary>
public static class SampleEvents
{
    /// <summary>The newest sample event is 10 minutes older than this.</summary>
    public static DateTimeOffset Now { get; } = new(2026, 10, 14, 10, 30, 0, TimeSpan.Zero);

    /// <summary>Change set shared by the creation of Agnieszka Zielińska and the enabling of her account (ids 1 and 2).</summary>
    public static Guid NewUserChangeSet { get; } = Guid.Parse("0192a3b4-7c00-7000-8000-00000000a001");

    /// <summary>Id of the Critical, unacknowledged PrivilegeGranted event (Łukasz Wiśniewski → Domain Admins).</summary>
    public const long PrivilegeGrantedId = 4;

    /// <summary>Id of the lockout of Żaneta Szymańska (5 lockouts today, failed-logon sources).</summary>
    public const long LockoutId = 11;

    /// <summary>Id of the deleted computer KRK-LT001 (attribution unknown: audit policy missing).</summary>
    public const long DeletedComputerId = 8;

    /// <summary>Id of the AttributesChanged event of Małgorzata Wójcik (title and department).</summary>
    public const long AttributesChangedId = 6;

    /// <summary>Id of the KeyCredentialChanged event whose attribution is still pending.</summary>
    public const long PendingAttributionId = 27;

    /// <summary>Id of the ambiguous ServicePrincipalNameAdded event.</summary>
    public const long AmbiguousId = 17;

    /// <summary>Creates the sample events (new instances on every call).</summary>
    public static List<WatchEvent> Create()
    {
        var at = Now.AddDays(-1).AddHours(-4); // 2026-10-13 06:30 UTC
        var events = new List<WatchEvent>
        {
            // 1-2: a new user (one change set: created + enabled).
            Event(WatchEventKind.UserCreated, AgnieszkaZielinska, at, Severity.Low, Resolved(JanKowalski, "WAW-PC001", "10.10.1.21", 4720), ["new-accounts"]) with
            {
                ChangeSetId = NewUserChangeSet,
                Changes =
                [
                    Text("displayName", [], ["Agnieszka Zielińska"]),
                    Text("department", [], ["IT"]),
                    Text("title", [], ["Administratorka sieci"]),
                ],
            },
            Event(WatchEventKind.AccountEnabled, AgnieszkaZielinska, at.AddSeconds(4), Severity.Low, Resolved(JanKowalski, "WAW-PC001", "10.10.1.21", 4722), ["account-status"]) with
            {
                ChangeSetId = NewUserChangeSet,
                Changes = [Flags("userAccountControl", added: [], removed: ["ACCOUNTDISABLE"])],
            },

            // 3: the IT group gets a member (start of a nesting path).
            Event(WatchEventKind.GroupMemberAdded, ServerAdmins, at.AddMinutes(42), Severity.Info, Resolved(JanKowalski, "WAW-PC001", "10.10.1.21", 4728), ["group-changes"]) with
            {
                Member = LukaszWisniewski,
                Changes = [Text("member", [], [LukaszWisniewski.Dn]) with { ValueKind = AttributeValueKind.Dn, Added = [LukaszWisniewski.Dn] }],
            },

            // 4: ... which is nested in Domain Admins: effective privilege granted.
            Event(WatchEventKind.PrivilegeGranted, LukaszWisniewski, at.AddMinutes(42).AddSeconds(1), Severity.Critical, Resolved(JanKowalski, "WAW-PC001", "10.10.1.21", 4728), ["tier0-privilege"]) with
            {
                Member = DomainAdmins,
                Via = [LukaszWisniewski, ServerAdmins, DomainAdmins],
                Tags = [EventTags.Privileged],
            },

            // 5: a disabled account (acknowledged).
            Event(WatchEventKind.AccountDisabled, ZbigniewKazmierczak, at.AddHours(1), Severity.Info, Resolved(AnnaNowak, "WAW-PC002", "10.10.1.37", 4725), ["account-status"]) with
            {
                Changes = [Flags("userAccountControl", added: ["ACCOUNTDISABLE"], removed: [])],
                AcknowledgedAt = at.AddHours(2),
                AcknowledgedBy = JanKowalski.Account,
                AcknowledgeComment = "Zgodnie z wnioskiem kadr (urlop bezpłatny).",
            },

            // 6: HR attributes changed by the HR sync account.
            Event(WatchEventKind.AttributesChanged, MalgorzataWojcik, at.AddHours(1).AddMinutes(30), Severity.Info, Resolved(HrSync, "HR-SYNC01", "10.10.5.40", 5136), ["account-attributes"]) with
            {
                Changes =
                [
                    Text("title", ["Główna księgowa"], ["Dyrektor finansowa"]),
                    Text("department", ["Księgowość"], ["Finanse i księgowość"]),
                    Text("telephoneNumber", ["+48 22 555 10 01"], ["+48 22 555 10 77"]),
                ],
                Tags = [EventTags.SyncAccount],
            },

            // 7: own password change of a regular user.
            Event(WatchEventKind.PasswordChanged, Grazyna, at.AddHours(2), Severity.Info, Resolved(GrazynaDabrowska, "GDA-PC014", "10.30.2.14", 4723), ["passwords"]) with
            {
                Changes = [new AttributeChange { Attribute = "unicodePwd", Redacted = true }, Text("pwdLastSet", ["2026-07-15 08:02"], ["2026-10-13 08:30"]) with { ValueKind = AttributeValueKind.FileTime }],
                Tags = [EventTags.SelfChange],
            },

            // 8: a deleted computer, attribution impossible (no audit events of that family).
            Event(WatchEventKind.ObjectDeleted, Laptop with { Dn = $"CN=KRK-LT001\\0ADEL:{Laptop.ObjectGuid:D},CN=Deleted Objects,{DomainDn}" }, at.AddHours(3), Severity.Low,
                Unresolved(AttributionStatus.Unknown, AttributionReasons.AuditPolicyMissing), ["deletions"]) with
            {
                PreviousDn = Laptop.Dn,
                Source = DetectionSource.DeletedObjects,
            },

            // 9: a move to another OU.
            Event(WatchEventKind.ObjectMoved, BartlomiejKozlowski with { Dn = $"CN=Bartłomiej Kozłowski,OU=Łódź,{SalesOu}" }, at.AddHours(4), Severity.Info,
                Resolved(AnnaNowak, "WAW-PC002", "10.10.1.37", 5139), ["account-attributes"]) with
            {
                PreviousDn = BartlomiejKozlowski.Dn,
            },

            // 10: password never expires set on a regular account.
            Event(WatchEventKind.AccountFlagsChanged, Grazyna, at.AddHours(5), Severity.Medium, Resolved(JanKowalski, "WAW-PC001", "10.10.1.21", 4738), ["account-flags"]) with
            {
                Changes = [Flags("userAccountControl", added: ["DONT_EXPIRE_PASSWORD"], removed: [])],
            },

            // 11: repeated lockout with failed-logon sources.
            Event(WatchEventKind.AccountLockedOut, ZanetaSzymanska, at.AddHours(6), Severity.Low, new EventAttribution
            {
                Status = AttributionStatus.Resolved,
                Confidence = AttributionConfidence.Medium,
                Workstation = "WRO-LT014",
                IpAddress = "10.20.4.17",
                SourceMethod = "4740",
                SourceConfidence = AttributionConfidence.Low,
                Evidence = [Evidence("dc01.contoso.test", 4740, 1_884_201, at.AddHours(6))],
                ResolvedAt = at.AddHours(6).AddSeconds(40),
            }, ["lockouts"]) with
            {
                Lockout = new LockoutInfo
                {
                    LockedAt = at.AddHours(6),
                    LockedUntil = at.AddHours(6).AddMinutes(30),
                    CallerComputer = "WRO-LT014",
                    Sources =
                    [
                        new LockoutSource("10.20.4.17", "WRO-LT014", 4, 4771),
                        new LockoutSource("10.20.4.33", null, 1, 4776),
                    ],
                    LockoutsToday = 5,
                },
                Source = DetectionSource.IncrementalPoll,
            },

            // 12: account expiring in 7 days.
            Event(WatchEventKind.AccountExpiring, PawelLewandowski, at.AddHours(7), Severity.Info, EventAttribution.NotApplicable, ["expiry"]) with
            {
                Expiry = new ExpiryInfo(ExpiryKind.Account, new DateTimeOffset(2026, 10, 20, 22, 0, 0, TimeSpan.Zero), new DateOnly(2026, 10, 20), 7, 7),
                Source = DetectionSource.ExpiryScan,
            },

            // 13: password of a service account expires tomorrow.
            Event(WatchEventKind.PasswordExpiring, BackupService, at.AddHours(8), Severity.High, EventAttribution.NotApplicable, ["privileged-accounts", "expiry"]) with
            {
                Expiry = new ExpiryInfo(ExpiryKind.Password, new DateTimeOffset(2026, 10, 15, 6, 0, 0, TimeSpan.Zero), new DateOnly(2026, 10, 15), 1, 1),
                Tags = [EventTags.ServiceAccount],
                Source = DetectionSource.ExpiryScan,
            },

            // 14: mass lockout burst.
            Event(WatchEventKind.MassLockout, DomainHead, at.AddHours(9), Severity.Medium, EventAttribution.NotApplicable, ["bursts"]) with
            {
                Burst = new BurstInfo
                {
                    Count = 7,
                    Window = TimeSpan.FromMinutes(10),
                    Objects = [ZanetaSzymanska, PawelLewandowski, BartlomiejKozlowski, MalgorzataWojcik],
                },
                Source = DetectionSource.BurstDetector,
            },

            // 15: Security log cleared on a DC.
            Event(WatchEventKind.SecurityLogCleared, Dc02, at.AddHours(10), Severity.Critical, Resolved(Administrator, "DC02", "10.10.0.12", 1102), ["tier0-eventlog"]) with
            {
                Source = DetectionSource.SecurityLog,
                Tags = [EventTags.DomainController],
            },

            // 16: health: Teams channel failing.
            Event(WatchEventKind.HealthDeliveryFailing, WatchdogService, at.AddHours(11), Severity.High, EventAttribution.NotApplicable, ["health"]) with
            {
                Changes = [Text("channel", [], ["teams-it"])],
                Source = DetectionSource.Health,
            },

            // 17: SPN added to a user – two possible actors.
            Event(WatchEventKind.ServicePrincipalNameAdded, SqlService, at.AddHours(12), Severity.High, new EventAttribution
            {
                Status = AttributionStatus.Ambiguous,
                Confidence = AttributionConfidence.Low,
                Reason = AttributionReasons.Ambiguous,
                Candidates = [JanKowalski, AdminKowalski],
                Evidence =
                [
                    Evidence("dc01.contoso.test", 5136, 1_884_377, at.AddHours(12)),
                    Evidence("dc02.contoso.test", 5136, 2_201_004, at.AddHours(12).AddSeconds(1)),
                ],
                ResolvedAt = at.AddHours(12).AddMinutes(2),
            }, ["tier0-security"]) with
            {
                Changes = [Text("servicePrincipalName", [], ["MSSQLSvc/waw-sql01.contoso.test:1433"]) with { Added = ["MSSQLSvc/waw-sql01.contoso.test:1433"] }],
                Tags = [EventTags.ServiceAccount],
            },

            // 18: adminCount set by SDProp (SYSTEM – reduced to Info).
            Event(WatchEventKind.AdminCountSet, AdminAccount, at.AddHours(13), Severity.Info, new EventAttribution
            {
                Status = AttributionStatus.Resolved,
                Confidence = AttributionConfidence.High,
                Reason = AttributionReasons.System,
                Actor = LocalSystem,
                SourceMethod = "local",
                Evidence = [Evidence("dc01.contoso.test", 5136, 1_884_390, at.AddHours(13))],
                ResolvedAt = at.AddHours(13).AddMinutes(1),
            }, ["security-review"]) with
            {
                Changes = [Text("adminCount", [], ["1"]) with { ValueKind = AttributeValueKind.Number }],
                Tags = [EventTags.Privileged],
            },

            // 19: GPO link changed on the Computers OU.
            Event(WatchEventKind.GroupPolicyLinkChanged, ComputersOuRef, at.AddHours(14), Severity.Medium, Resolved(JanKowalski, "WAW-PC001", "10.10.1.21", 5136), ["ou-gpo"]) with
            {
                Changes = [Text("gPLink", [], ["[LDAP://cn={31B2F340-016D-11D2-945F-00C04FB984F9},cn=policies,cn=system,DC=contoso,DC=test;0]"]) with { ValueKind = AttributeValueKind.Text }],
            },

            // 20: a new domain controller (acknowledged).
            Event(WatchEventKind.DomainControllerAdded, Dc03, at.AddHours(15), Severity.Critical, Resolved(Administrator, "DC01", "10.10.0.11", 5137), ["tier0-domain"]) with
            {
                Tags = [EventTags.DomainController],
                AcknowledgedAt = at.AddHours(16),
                AcknowledgedBy = JanKowalski.Account,
                AcknowledgeComment = "Planowane wdrożenie DC03 (zmiana CHG-2041).",
            },

            // 21: a computer joined by a regular user (machine account quota) into the default container.
            Event(WatchEventKind.ComputerCreated, NewComputer, at.AddHours(16), Severity.Medium, Resolved(MarekZajac, "KTW-PC007", "10.40.1.77", 4741), ["new-computers", "computers-unusual"]) with
            {
                Tags = [EventTags.CreatedByNonAdmin, EventTags.DefaultContainer],
            },

            // 22: helpdesk unlocks the account.
            Event(WatchEventKind.AccountUnlocked, ZanetaSzymanska, at.AddHours(17), Severity.Info, Resolved(AnnaNowak, "WAW-PC002", "10.10.1.37", 4767), ["unlocks"]),

            // 23: removal from Protected Users.
            Event(WatchEventKind.GroupMemberRemoved, ProtectedUsers, at.AddHours(18), Severity.High, Resolved(JanKowalski, "WAW-PC001", "10.10.1.21", 4729), ["group-changes"]) with
            {
                Member = AdminAccount,
                Changes = [Text("member", [AdminAccount.Dn], []) with { ValueKind = AttributeValueKind.Dn, Removed = [AdminAccount.Dn] }],
                Tags = [EventTags.Privileged],
            },

            // 24: a group renamed.
            Event(WatchEventKind.ObjectRenamed, SalesGroup, at.AddHours(19), Severity.Info, Resolved(AnnaNowak, "WAW-PC002", "10.10.1.37", 5139), ["account-attributes"]) with
            {
                PreviousDn = "CN=GG_Sprzedaz," + GroupsOu,
            },

            // 25: delegation on an application server, the originating DC's log could not be read.
            Event(WatchEventKind.DelegationChanged, AppServer, at.AddHours(20), Severity.High, Unresolved(AttributionStatus.Unknown, AttributionReasons.DcUnreachable), ["tier0-security"]) with
            {
                Changes = [Flags("userAccountControl", added: ["TRUSTED_FOR_DELEGATION"], removed: [])],
                Tags = [EventTags.Server],
                OriginatingDc = "dc02.contoso.test",
            },

            // 26: password reset of a privileged account by the helpdesk.
            Event(WatchEventKind.PasswordChanged, AdminAccount, at.AddHours(21), Severity.High, Resolved(AnnaNowak, "WAW-PC002", "10.10.1.37", 4724), ["privileged-accounts", "passwords"]) with
            {
                Changes = [new AttributeChange { Attribute = "unicodePwd", Redacted = true }],
                Tags = [EventTags.Privileged, EventTags.PasswordReset],
            },

            // 27: device key registered, attribution still pending.
            Event(WatchEventKind.KeyCredentialChanged, PawelLewandowski, Now.AddMinutes(-40), Severity.Medium, EventAttribution.Pending, ["security-review"]) with
            {
                Changes = [Text("msDS-KeyCredentialLink", [], ["B:854:0002000020000…:CN=Paweł Lewandowski," + SalesOu]) with { ValueKind = AttributeValueKind.Binary, Added = ["B:854:0002000020000…"], Truncated = true }],
            },

            // 28: configuration of the watchdog changed.
            Event(WatchEventKind.WatchdogConfigurationChanged, WatchdogService, Now.AddMinutes(-30), Severity.High, new EventAttribution
            {
                Status = AttributionStatus.Resolved,
                Confidence = AttributionConfidence.High,
                Actor = JanKowalski,
                IpAddress = "10.10.1.21",
                SourceMethod = "local",
                SourceConfidence = AttributionConfidence.High,
                ResolvedAt = Now.AddMinutes(-30),
            }, ["health"]) with
            {
                Changes =
                [
                    Text("rules[lockouts].exclusions", [], ["+1 -0"]),
                    Text("channels[teams-it].url", [], ["replaced"]),
                ],
                Source = DetectionSource.Health,
            },

            // 29: permission gap found by the self-check.
            Event(WatchEventKind.HealthPermissionGap, WatchdogService, Now.AddMinutes(-20), Severity.High, EventAttribution.NotApplicable, ["health"]) with
            {
                Changes = [Text("permission.deletedObjects", ["Ok"], ["Warning"])],
                Source = DetectionSource.Health,
            },

            // 30: another lockout (first one today).
            Event(WatchEventKind.AccountLockedOut, PawelLewandowski, Now.AddMinutes(-10), Severity.Low, new EventAttribution
            {
                Status = AttributionStatus.Resolved,
                Confidence = AttributionConfidence.Medium,
                IpAddress = "10.20.4.52",
                SourceMethod = "4771",
                SourceConfidence = AttributionConfidence.Medium,
                Evidence = [Evidence("dc01.contoso.test", 4740, 1_885_002, Now.AddMinutes(-10))],
                ResolvedAt = Now.AddMinutes(-9),
            }, ["lockouts"]) with
            {
                Lockout = new LockoutInfo
                {
                    LockedAt = Now.AddMinutes(-10),
                    LockedUntil = Now.AddMinutes(20),
                    Sources = [new LockoutSource("10.20.4.52", null, 6, 4771)],
                    LockoutsToday = 1,
                },
            },
        };

        for (var i = 0; i < events.Count; i++)
        {
            var id = i + 1L;
            events[i] = events[i] with
            {
                Id = id,
                EventId = EventGuid(id),
                ChangeSetId = events[i].ChangeSetId == Guid.Empty ? EventGuid(1000 + id) : events[i].ChangeSetId,
            };
        }

        return events;
    }

    /// <summary>Deterministic Guid-v7-shaped event id of sample event <paramref name="id"/>.</summary>
    public static Guid EventGuid(long id) =>
        Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"0192a3b4-7c00-7000-8000-{id:D12}"));

    /// <summary>A further event (live updates in tests): a new user in the sales unit, attribution pending.</summary>
    public static WatchEvent NewUser(string name, DateTimeOffset detectedAt, Severity severity = Severity.Low) =>
        Event(WatchEventKind.UserCreated, new ObjectRef
        {
            ObjectGuid = Guid.NewGuid(),
            Dn = $"CN={name},{SalesOu}",
            Name = name,
            DisplayName = name,
            Kind = WatchObjectKind.User,
        }, detectedAt, severity, EventAttribution.Pending, ["new-accounts"]);

    private static WatchEvent Event(
        WatchEventKind kind,
        ObjectRef obj,
        DateTimeOffset detectedAt,
        Severity severity,
        EventAttribution attribution,
        IReadOnlyList<string> rules) => new()
        {
            EventId = Guid.NewGuid(),
            Kind = kind,
            Severity = severity,
            Object = obj,
            Domain = Domain,
            DetectedAt = detectedAt,
            OriginatingTime = detectedAt.AddSeconds(-12),
            OriginatingDc = "dc01.contoso.test",
            LocalChangeTime = detectedAt.AddSeconds(-12),
            Source = kind is WatchEventKind.SecurityLogCleared ? DetectionSource.SecurityLog : DetectionSource.IncrementalPoll,
            Attribution = attribution,
            MatchedRules = rules,
        };

    private static EventAttribution Resolved(PrincipalRef actor, string workstation, string ip, int eventCode) => new()
    {
        Status = AttributionStatus.Resolved,
        Confidence = AttributionConfidence.High,
        Actor = actor,
        Workstation = workstation,
        IpAddress = ip,
        SourceMethod = "4624",
        SourceConfidence = AttributionConfidence.High,
        LogonType = 3,
        Evidence =
        [
            Evidence("dc01.contoso.test", eventCode, 1_880_000 + eventCode, Now.AddDays(-1)),
            Evidence("dc01.contoso.test", 4624, 1_879_990, Now.AddDays(-1).AddMinutes(-3)),
        ],
        ResolvedAt = Now.AddDays(-1).AddMinutes(1),
    };

    private static EventAttribution Unresolved(AttributionStatus status, string reason) => new()
    {
        Status = status,
        Confidence = AttributionConfidence.None,
        Reason = reason,
        ResolvedAt = Now.AddDays(-1),
    };

    private static EvidenceRef Evidence(string dc, int code, long record, DateTimeOffset time) =>
        new() { Dc = dc, EventCode = code, RecordId = record, TimeCreated = time };

    private static AttributeChange Text(string attribute, IReadOnlyList<string> oldValues, IReadOnlyList<string> newValues) => new()
    {
        Attribute = attribute,
        ValueKind = AttributeValueKind.Text,
        OldValues = oldValues,
        NewValues = newValues,
        OldCount = oldValues.Count,
        NewCount = newValues.Count,
    };

    private static AttributeChange Flags(string attribute, IReadOnlyList<string> added, IReadOnlyList<string> removed) => new()
    {
        Attribute = attribute,
        ValueKind = AttributeValueKind.Flags,
        FlagsAdded = added,
        FlagsRemoved = removed,
        OldValues = ["0x200"],
        NewValues = ["0x202"],
        OldCount = 1,
        NewCount = 1,
    };
}
