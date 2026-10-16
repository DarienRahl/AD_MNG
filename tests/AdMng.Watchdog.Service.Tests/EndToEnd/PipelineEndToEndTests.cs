using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Testing;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Core.Retention;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Scans;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Testing.Attribution;
using AdMng.Watchdog.Testing.Certificates;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;
using AdMng.Watchdog.Testing.Http;
using AdMng.Watchdog.Testing.Smtp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace AdMng.Watchdog.Service.Tests.EndToEnd;

/// <summary>
/// The whole watchdog pipeline in-process (spec 22.2 end to end): the real service host (test authentication) with its
/// SQLite store in a temporary data folder, the fake directory with the watchdog emulation as the DC, the fake Security
/// log with 47xx fixtures, the SMTP sink (STARTTLS + Basic), a Teams Workflows sink and a Standard Webhooks sink (HTTPS),
/// one fake clock. Baseline → seven directory changes → every job run deterministically (detection, scans, attribution,
/// rules, dispatch, outbox, digest) → the API, the three sinks and the log are checked; then a second host on the same
/// data folder must not notify anything again.
/// </summary>
public sealed class PipelineEndToEndTests : IAsyncLifetime
{
    /// <summary>Wednesday 14 October 2026, 10:00 in Warsaw – business time.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private const string SmtpUser = "watchdog@contoso.test";
    private const string SmtpPassword = "Hasło-SMTP-Żuraw-2026!";
    private const string TeamsSignature = "sig-Tajny-Podpis-Teams-0123456789";
    private const string WebhookToken = "zeton-tajny-SIEM-42";
    private const string AdminsMailbox = "zespol.it@contoso.test";
    private const string HelpdeskMailbox = "pomoc@contoso.test";
    private const string NewUserDisplayName = "Łucja Ćwik <Kadry & Płace>";
    private const string EncodedDisplayName = "Łucja Ćwik &lt;Kadry &amp; Płace&gt;";
    private const string NewDepartment = "Badania & Rozwój <R&D>";
    private const string CallerComputer = "KRK-PC017";
    private const string TeamsChannel = "teams-it";
    private const string WebhookChannel = "siem";

    private static readonly string SigningSecret = "whsec_" + Convert.ToBase64String(SHA256.HashData("klucz-podpisu-Watchdoga-e2e"u8));

    private readonly TestClock _clock = new(Start);
    private readonly TestDataDirectory _data = new();
    private readonly EphemeralDataProtectionProvider _keys = new();
    private readonly FakeSecurityEventSource _securityLog = new();
    private readonly SmtpSink _smtp = new(new SmtpSinkOptions
    {
        Security = SmtpSinkSecurity.StartTls,
        RequireAuthentication = true,
        Accounts = new Dictionary<string, string> { [SmtpUser] = SmtpPassword },
    });

    private readonly HttpSink _teams = new(TestCertificates.Localhost);
    private readonly HttpSink _webhook = new(TestCertificates.Localhost);
    private readonly FakeDirectoryBuilder _directory;
    private readonly SampleCompany _company;
    private readonly string _dc;

    public PipelineEndToEndTests()
    {
        _directory = new FakeDirectoryBuilder(timeProvider: _clock);
        _directory.AddWatchdogInfrastructure();
        _company = _directory.AddSampleCompany();
        _dc = _directory.Session.Info.Server;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _smtp.StartAsync(Ct);
        await _teams.StartAsync(Ct);
        await _webhook.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _smtp.DisposeAsync();
        await _teams.DisposeAsync();
        await _webhook.DisposeAsync();
        _data.Dispose();
    }

    [Fact]
    public async Task ChangesFlowFromTheDirectoryToEveryChannel_Once()
    {
        Changes changes;
        await using (var service = Host())
        {
            using var admin = service.Admin();
            await SaveConfigurationAsync(admin);
            var pipeline = new Pipeline(service.Services, _clock);

            // Baseline (no change events), then the operator's changes – each with its Security-log record on the DC.
            await pipeline.RoundAsync();
            Assert.Contains(await EventsAsync(admin, WatchEventKind.HealthBaselineCompleted), e => e.Severity == Severity.Info);
            changes = await MakeChangesAsync();

            // Ten-second rounds first (the Critical alert goes out before its actor is known, the follow-up after), then
            // one-minute rounds through the settle window, attribution retries, holds and aggregation windows (expiry: 60 min).
            await pipeline.RunForAsync(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(10));
            await pipeline.RunForAsync(TimeSpan.FromMinutes(150), TimeSpan.FromMinutes(1));

            await AssertEventsAsync(admin, changes);
            AssertEmails(changes);
            AssertTeamsCards();
            AssertSignedWebhooks();

            // The next morning's digest (07:30 Warsaw) – rendered and sent by the digest job on its schedule.
            var before = _smtp.Messages.Count;
            await pipeline.RunUntilAsync(new DateTimeOffset(2026, 10, 15, 5, 31, 0, TimeSpan.Zero), TimeSpan.FromHours(1));
            var digest = Assert.Single(_smtp.Messages.Skip(before).Select(Mime), m => m.Subject.Contains("Raport dzienny", StringComparison.Ordinal));
            Assert.Equal([AdminsMailbox], digest.To);
            Assert.Contains(EncodedDisplayName, digest.HtmlBody, StringComparison.Ordinal);
            AssertLogIsClean(service.Log);
        }

        // Restart: a new host on the same data folder (and key ring) re-runs every job and notifies nothing again.
        var mails = _smtp.Messages.Count;
        var cards = _teams.Requests.Count;
        var hooks = _webhook.Requests.Count(r => Type(r) != WebhookEventTypes.Heartbeat);
        await using (var restarted = Host())
        {
            var pipeline = new Pipeline(restarted.Services, _clock);
            await pipeline.RunForAsync(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1));

            using var admin = restarted.Admin();
            Assert.Single(await EventsAsync(admin, WatchEventKind.UserCreated));
            Assert.Equal(mails, _smtp.Messages.Count);
            Assert.Equal(cards, _teams.Requests.Count);
            Assert.Equal(hooks, _webhook.Requests.Count(r => Type(r) != WebhookEventTypes.Heartbeat));

            // …while it still notifies about new changes (the stored configuration and secrets were loaded).
            _directory.AddUser("Jerzy", "Żółkiewski", _company.SalesOu);
            await pipeline.RunForAsync(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1));
            var fresh = Assert.Single(_smtp.Messages.Skip(mails).Select(Mime));
            Assert.Contains("Nowe konto", fresh.Subject, StringComparison.Ordinal);
            Assert.Equal(2, (await EventsAsync(admin, WatchEventKind.UserCreated)).Count);
            AssertLogIsClean(restarted.Log);
        }
    }

    private WatchdogFactory Host() => new(
        services: services =>
        {
            services.AddSingleton<IDataProtectionProvider>(_keys);
            services.AddSingleton<ISecurityEventSource>(_securityLog);
        },
        directory: _directory.Session,
        dataDirectory: _data,
        clock: _clock);

    private async Task SaveConfigurationAsync(HttpClient admin)
    {
        var defaults = TestData.Configuration();
        var configuration = defaults with
        {
            General = (defaults.General ?? new GeneralSettings()) with
            {
                InstanceName = "AD Watchdog – Kraków",
                Language = "pl",
                TimeZone = "Europe/Warsaw",
                PublicBaseUrl = "https://admng-wd.contoso.test:8443",
                RolloutEnded = true,
            },
            Smtp = new SmtpSettings
            {
                Enabled = true,
                Host = _smtp.Host,
                Port = _smtp.Port,
                Security = SmtpSecurity.StartTls,
                Authentication = SmtpAuthentication.Basic,
                UserName = SmtpUser,
                Password = new SecretField { NewValue = SmtpPassword },
                From = "AD Watchdog <watchdog@contoso.test>",
                LocalDomain = "watchdog.contoso.test",
                CertificateValidation = CertificateValidationMode.PinnedSha256,
                PinnedSha256 = TestCertificates.Sha256(TestCertificates.Localhost),
                CheckRevocation = false,
            },
            RecipientGroups =
            [
                .. defaults.RecipientGroups.Select(g => g.Id switch
                {
                    DefaultRules.ItAdminsGroup => g with { Addresses = [AdminsMailbox] },
                    DefaultRules.HelpdeskGroup => g with { Addresses = [HelpdeskMailbox] },
                    _ => g,
                }),
            ],
            Channels =
            [
                DefaultConfigurationFactory.CreateChannel(TeamsChannel, ChannelType.Teams, "Teams – Zespół IT") with
                {
                    Url = new SecretField { NewValue = new Uri(_teams.BaseAddress, "workflows/e2e/triggers/manual/paths/invoke?api-version=1&sig=" + TeamsSignature).ToString() },
                },
                DefaultConfigurationFactory.CreateChannel(WebhookChannel, ChannelType.Webhook, "SIEM – Centrum bezpieczeństwa") with
                {
                    Url = new SecretField { NewValue = new Uri(_webhook.BaseAddress, "hooks/watchdog?token=" + WebhookToken).ToString() },
                    Webhook = new WebhookChannelOptions { SigningSecret = new SecretField { NewValue = SigningSecret } },
                },
            ],
            Rules =
            [
                .. defaults.Rules.Select(r => r.Id == "tier0-privilege"
                    ? r with { Delivery = r.Delivery with { ChannelIds = [TeamsChannel, WebhookChannel] } }
                    : r),
            ],
        };

        using var response = await admin.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = 0, Configuration = configuration }), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    /// <summary>The seven changes of the scenario, two seconds apart, with the matching Security-log records.</summary>
    private async Task<Changes> MakeChangesAsync()
    {
        var actor = await IdentityAsync(_company.JanKowalski);
        var subject = SecurityEventXml.Subject(actor.Sid, actor.Sam, "CONTOSO", "0x5a3f1");
        var session = _directory.Session;

        _clock.Advance(TimeSpan.FromMinutes(1));
        var newUser = _directory.AddUser("Łucja", "Ćwik", _company.ItOu, new Dictionary<string, object?> { [Attr.DisplayName] = NewUserDisplayName });
        var created = await IdentityAsync(newUser);
        _securityLog.Add(_dc, id => SecurityEventXml.Account(4720, id, _clock.GetUtcNow(), _dc, created.Sid, created.Sam, subject,
            ("SamAccountName", created.Sam), ("DisplayName", NewUserDisplayName), ("NewUacValue", "0x10")));

        _clock.Advance(TimeSpan.FromSeconds(2));
        var computer = _directory.AddComputer("KRK-PC042", _company.ComputersOu);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await ModifyAsync(_company.GrazynaDabrowska, AttributeModification.Replace(Attr.Department, [NewDepartment]));

        _clock.Advance(TimeSpan.FromSeconds(2));
        var disabled = await IdentityAsync(_company.BartlomiejKozlowski);
        await ModifyAsync(_company.BartlomiejKozlowski, AttributeModification.Replace(Attr.UserAccountControl,
            [((int)(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable)).ToString(CultureInfo.InvariantCulture)]));
        _securityLog.Add(_dc, id => SecurityEventXml.Account(4725, id, _clock.GetUtcNow(), _dc, disabled.Sid, disabled.Sam, subject));

        _clock.Advance(TimeSpan.FromSeconds(2));
        var locked = await IdentityAsync(_company.PawelLewandowski);
        session.SimulateLockout(_company.PawelLewandowski);
        _securityLog.Add(_dc, id => SecurityEventXml.Create(4740, id, _clock.GetUtcNow(), _dc,
        [
            ("TargetUserName", locked.Sam), ("TargetDomainName", CallerComputer), ("TargetSid", locked.Sid),
            ("SubjectUserSid", "S-1-5-18"), ("SubjectUserName", "DC01$"), ("SubjectDomainName", "CONTOSO"), ("SubjectLogonId", "0x3e7"),
        ]));

        _clock.Advance(TimeSpan.FromSeconds(2));
        var promoted = await IdentityAsync(_company.MalgorzataWojcik);
        await ModifyAsync(_directory.DomainAdminsDn, AttributeModification.Add(Attr.Member, [_company.MalgorzataWojcik]));
        _securityLog.Add(_dc, id => SecurityEventXml.Membership(4728, id, _clock.GetUtcNow(), _dc, $"{_directory.DomainSid}-512", "Domain Admins",
            promoted.Sid, _company.MalgorzataWojcik, subject));

        _clock.Advance(TimeSpan.FromSeconds(2));
        var expiring = await IdentityAsync(_company.LukaszWisniewski);
        var expires = _clock.GetUtcNow().AddDays(5);
        await ModifyAsync(_company.LukaszWisniewski, AttributeModification.Replace(Attr.AccountExpires,
            [AdConvert.ToFileTime(expires).ToString(CultureInfo.InvariantCulture)]));
        _securityLog.Add(_dc, id => SecurityEventXml.Account(4738, id, _clock.GetUtcNow(), _dc, expiring.Sid, expiring.Sam, subject,
            ("AccountExpires", expires.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))));

        return new Changes(actor, newUser, computer, disabled, locked, promoted, expiring);
    }

    private async Task AssertEventsAsync(HttpClient admin, Changes changes)
    {
        var created = Assert.Single(await EventsAsync(admin, WatchEventKind.UserCreated));
        Assert.Equal(changes.NewUserDn, created.Object.Dn);
        AssertActor(changes.Actor, created);

        Assert.Single(await EventsAsync(admin, WatchEventKind.ComputerCreated), e => e.Object.Dn == changes.ComputerDn);

        var department = Assert.Single(await EventsAsync(admin, WatchEventKind.AttributesChanged), e => e.Object.Dn == _company.GrazynaDabrowska);
        Assert.Equal(NewDepartment, Assert.Single(department.Changes, c => c.Attribute == Attr.Department).NewValues.Single());
        Assert.NotEqual(AttributionStatus.Pending, department.Attribution.Status);   // no 5136: Unknown with a reason after the retries

        var disabled = Assert.Single(await EventsAsync(admin, WatchEventKind.AccountDisabled), e => e.Object.Sid == changes.Disabled.Sid);
        AssertActor(changes.Actor, disabled);

        var locked = Assert.Single(await EventsAsync(admin, WatchEventKind.AccountLockedOut), e => e.Object.Sid == changes.Locked.Sid);
        Assert.Equal(AttributionStatus.NotApplicable, locked.Attribution.Status);
        Assert.Equal(CallerComputer, locked.Lockout?.CallerComputer);

        var member = Assert.Single(await EventsAsync(admin, WatchEventKind.GroupMemberAdded), e => e.Member?.Sid == changes.Promoted.Sid);
        AssertActor(changes.Actor, member);
        var granted = Assert.Single(await EventsAsync(admin, WatchEventKind.PrivilegeGranted), e => e.Member?.Sid == changes.Promoted.Sid || e.Object.Sid == changes.Promoted.Sid);
        Assert.True(granted.Severity >= Severity.High, $"PrivilegeGranted severity {granted.Severity}");
        AssertActor(changes.Actor, granted);

        var expiration = Assert.Single(await EventsAsync(admin, WatchEventKind.AccountExpirationChanged), e => e.Object.Sid == changes.Expiring.Sid);
        AssertActor(changes.Actor, expiration);
        var expiring = Assert.Single(await EventsAsync(admin, WatchEventKind.AccountExpiring), e => e.Object.Sid == changes.Expiring.Sid);
        Assert.Equal(5, expiring.Expiry?.DaysLeft);
    }

    private static void AssertActor(Identity actor, WatchEvent watchEvent)
    {
        Assert.True(watchEvent.Attribution.Status == AttributionStatus.Resolved,
            $"{watchEvent.Kind}: attribution {watchEvent.Attribution.Status} ({watchEvent.Attribution.Reason})");
        Assert.Equal(actor.Sid, watchEvent.Attribution.Actor?.Sid);
        Assert.Equal(@"CONTOSO\" + actor.Sam, watchEvent.Attribution.Actor?.Account, StringComparer.OrdinalIgnoreCase);
    }

    private void AssertEmails(Changes changes)
    {
        var mails = _smtp.Messages.Select(Mime).ToList();
        Assert.All(_smtp.Messages, m => Assert.Equal(SmtpUser, m.AuthenticatedUser));
        Assert.All(_smtp.Messages, m => Assert.True(m.Secure));
        Assert.All(mails, m => Assert.Matches(@"^(Re: )?\[AD Watchdog\]\[(INFO|NISKI|ŚREDNI|WYSOKI|KRYTYCZNE)\] ", m.Subject));

        // The new account (it-admins, business hours) – Polish, the display name HTML-encoded, never raw.
        var created = Assert.Single(mails, m => m.Subject.Contains("Nowe konto", StringComparison.Ordinal));
        Assert.Contains(AdminsMailbox, created.To);
        Assert.Contains(EncodedDisplayName, created.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(NewUserDisplayName, created.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Kto", created.TextBody, StringComparison.Ordinal);                     // the actor row (Polish label)
        Assert.Contains(changes.Actor.Sam, created.TextBody, StringComparison.OrdinalIgnoreCase);

        // Privileged access (tier0, always): sent at once with the actor still pending, then the follow-up in the same
        // thread once the 4728 record attributed it; lockout and expiry notices go to the helpdesk.
        var subjects = string.Join(" | ", mails.Select(m => m.Subject + " → " + string.Join(",", m.To)));
        var granted = Assert.Single(mails, m => m.Subject.Contains("Dodano do grupy uprzywilejowanej", StringComparison.Ordinal) && m.InReplyTo is null);
        Assert.Contains("[KRYTYCZNE]", granted.Subject, StringComparison.Ordinal);
        Assert.Equal([AdminsMailbox], granted.To);
        var followUp = Assert.Single(mails, m => m.InReplyTo is not null);
        Assert.Equal(granted.MessageId, followUp.InReplyTo);
        Assert.Contains("Uzupełnienie: wykonawca ustalony", followUp.TextBody, StringComparison.Ordinal);
        Assert.Contains(changes.Actor.Sam, followUp.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.True(mails.Any(m => m.To.Contains(HelpdeskMailbox) && m.Subject.Contains("Zablokowano konto", StringComparison.Ordinal)), subjects);
        Assert.True(mails.Any(m => m.To.Contains(HelpdeskMailbox) && m.Subject.Contains("wygaśnie", StringComparison.Ordinal)), subjects);
    }

    private void AssertTeamsCards()
    {
        Assert.NotEmpty(_teams.Requests);
        foreach (var request in _teams.Requests)
        {
            Assert.Equal("POST", request.Method);
            Assert.Contains("sig=" + TeamsSignature, request.PathAndQuery, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(request.Body);
            Assert.Equal("message", json.RootElement.GetProperty("type").GetString());
            var attachment = Assert.Single(json.RootElement.GetProperty("attachments").EnumerateArray());
            Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
            var card = attachment.GetProperty("content");
            Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
            Assert.False(string.IsNullOrEmpty(card.GetProperty("version").GetString()));
            Assert.NotEqual(0, card.GetProperty("body").GetArrayLength());
            Assert.True(request.Body.Length <= 28 * 1024, "the card must fit the Teams payload limit");
        }

        Assert.Contains(_teams.Requests, r => r.BodyText.Contains("Domain Admins", StringComparison.Ordinal));
        Assert.Contains(_teams.Requests, r => r.BodyText.Contains("Uzupełnienie: wykonawca ustalony", StringComparison.Ordinal));
    }

    private void AssertSignedWebhooks()
    {
        var secret = new SecretValue(SigningSecret);
        Assert.NotEmpty(_webhook.Requests);
        foreach (var request in _webhook.Requests)
        {
            Assert.Contains("token=" + WebhookToken, request.PathAndQuery, StringComparison.Ordinal);
            var id = request.Headers["webhook-id"];
            var timestamp = long.Parse(request.Headers["webhook-timestamp"], CultureInfo.InvariantCulture);
            Assert.True(StandardWebhookSigner.Verify(request.Headers["webhook-signature"], secret, id, timestamp, request.Body), $"signature of {id}");
            Assert.DoesNotContain('.', id);
            using var json = JsonDocument.Parse(request.Body);
            Assert.Equal(1, json.RootElement.GetProperty("data").GetProperty("schemaVersion").GetInt32());
        }

        var granted = Assert.Single(_webhook.Requests, r => Type(r) == "ad.privilege.granted");
        Assert.Equal("ad.privilege.granted", Type(granted));
        Assert.Contains(_webhook.Requests, r => Type(r) == WebhookEventTypes.AttributionUpdated);
    }

    private void AssertLogIsClean(TestLog log)
    {
        string[] forbidden =
        [
            SmtpPassword, TeamsSignature, WebhookToken, SigningSecret, "Łucja", "Ćwik", "Kadry", "Badania", "Małgorzata", "Wójcik",
            "Lewandowski", "Kozłowski", "Wiśniewski", "Dąbrowska", "Żółkiewski", "KRK-PC042", CallerComputer, AdminsMailbox, HelpdeskMailbox,
        ];
        var text = log.Text;
        Assert.NotEmpty(log.Entries);
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.All(log.Entries, e => Assert.Null(e.Exception));
    }

    private static async Task<IReadOnlyList<WatchEvent>> EventsAsync(HttpClient admin, WatchEventKind kind)
    {
        var page = await ApiJson.ReadAsync<EventPage>(await admin.GetAsync(WatchdogRoutes.Events + "?" + new EventQuery { Kinds = [kind], Limit = 500 }.ToQueryString(), Ct));
        return page.Items;
    }

    private async Task ModifyAsync(string dn, AttributeModification modification) =>
        await _directory.Session.ModifyAsync(dn, [modification], null, Ct);

    private async Task<Identity> IdentityAsync(string dn)
    {
        var entry = await _directory.Session.GetAsync(dn, [Attr.ObjectSid, Attr.SamAccountName], Ct) ?? throw new InvalidOperationException(dn);
        return new Identity(entry.ObjectSid!, entry.GetString(Attr.SamAccountName)!);
    }

    private static Mail Mime(SmtpSinkMessage message)
    {
        using var stream = new MemoryStream(message.Data);
        var mime = MimeMessage.Load(stream);
        return new Mail(mime.Subject ?? string.Empty, [.. mime.To.Mailboxes.Select(a => a.Address)], mime.HtmlBody ?? string.Empty, mime.TextBody ?? string.Empty,
            mime.MessageId, mime.InReplyTo);
    }

    private static string? Type(HttpSinkRequest request)
    {
        using var json = JsonDocument.Parse(request.Body);
        return json.RootElement.GetProperty("type").GetString();
    }

    private sealed record Identity(string Sid, string Sam);

    /// <summary>A received e-mail, decoded.</summary>
    private sealed record Mail(string Subject, IReadOnlyList<string> To, string HtmlBody, string TextBody, string? MessageId, string? InReplyTo);

    private sealed record Changes(Identity Actor, string NewUserDn, string ComputerDn, Identity Disabled, Identity Locked, Identity Promoted, Identity Expiring);

    /// <summary>The service's jobs, run in pipeline order on the fake clock (no background job hosts).</summary>
    private sealed class Pipeline(IServiceProvider services, TestClock clock)
    {
        private readonly IWatchdogJob[] _jobs =
        [
            services.GetRequiredService<ChangeDetectionEngine>(),
            services.GetRequiredService<ScheduledScanJob>(),
            services.GetRequiredService<AttributionEngine>(),
            services.GetRequiredService<RuleEvaluationJob>(),
            services.GetRequiredService<DigestJob>(),
            services.GetRequiredService<HeartbeatJob>(),
            services.GetRequiredService<NotificationDispatchJob>(),
            services.GetRequiredService<OutboxSenderJob>(),
            services.GetRequiredService<RetentionJob>(),
            services.GetRequiredService<StorageGuardJob>(),
        ];

        /// <summary>One run of every job.</summary>
        public async Task RoundAsync()
        {
            foreach (var job in _jobs)
            {
                var result = await job.RunOnceAsync(Ct);
                Assert.False(result.Failed, $"job {job.Name} failed: {result.FailureCode}");
            }
        }

        /// <summary>Rounds every <paramref name="step"/> for <paramref name="duration"/>.</summary>
        public async Task RunForAsync(TimeSpan duration, TimeSpan step) => await RunUntilAsync(clock.GetUtcNow() + duration, step);

        /// <summary>Rounds every <paramref name="step"/> until <paramref name="until"/>.</summary>
        public async Task RunUntilAsync(DateTimeOffset until, TimeSpan step)
        {
            await RoundAsync();
            while (clock.GetUtcNow() < until)
            {
                clock.Advance(until - clock.GetUtcNow() < step ? until - clock.GetUtcNow() : step);
                await RoundAsync();
            }
        }
    }
}
