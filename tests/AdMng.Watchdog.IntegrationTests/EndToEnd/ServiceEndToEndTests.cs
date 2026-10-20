using System.Globalization;
using System.Text.Json;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.IntegrationTests.Infrastructure;
using AdMng.Watchdog.Service.Directory;
using AdMng.Watchdog.Testing.Http;

namespace AdMng.Watchdog.IntegrationTests.EndToEnd;

/// <summary>
/// The whole service against the lab (spec 22.4 <c>ServiceEndToEndTests</c>): the real host on Kestrel (https, the
/// background job hosts, SQLite), the real LDAP connection as <c>odczyt.test</c>, configured through the API with the
/// AD_MNG client (<see cref="WatchdogHttpClient"/>) – SMTP to Mailpit (or the in-process sink), a signed webhook to
/// <see cref="HttpSink"/> – then a user with a hostile display name created in the lab: the event appears in the API,
/// the Polish e-mail carries the HTML-encoded value and no secret, the webhook signature verifies, the status shows the
/// directory Ok and attribution in collector mode (the Linux service's OpenWEC tailer, stage B design 7.8) with the lab DC
/// not forwarding – Samba has no Windows Event Forwarding – so the event ends Unknown (AUDIT_POLICY_MISSING: no 4720 of
/// that DC ever arrived) after its retries, and the log stays free of directory values and secrets.
/// </summary>
public sealed class ServiceEndToEndTests(LabFixture lab)
{
    private const string DisplayName = "Łucja Ćwik <Kadry & Płace>";
    private const string EncodedDisplayName = "Łucja Ćwik &lt;Kadry &amp; Płace&gt;";
    private const string WebhookChannel = "siem-e2e";
    private const string SmtpUser = "watchdog.e2e@contoso.test";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LabChange_IsDetectedNotifiedAndVisibleThroughTheApi()
    {
        WatchdogLab.RequireLab();
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The service under test is the Linux one (OpenWEC collector mode).");
        var run = Guid.NewGuid().ToString("N")[..8];
        var recipient = $"zespol.it.{run}@contoso.test";
        var smtpPassword = $"Hasło-SMTP-Żuraw-{run}!";
        var webhookToken = $"zeton-SIEM-{run}";
        var signingSecret = StandardWebhookSigner.GenerateSecret();

        await using var mailbox = (ITestMailbox?)MailpitMailbox.FromEnvironment() ?? await SinkMailbox.StartAsync(SmtpUser, smtpPassword, Ct);
        await using var webhook = new HttpSink();
        await webhook.StartAsync(Ct);
        using var ldapPassword = new EnvironmentVariableScope(DirectoryConnectionOptionsFactory.PasswordVariable, WatchdogLab.ReadOnlyPassword);
        await using var service = new LabServiceFactory();
        service.Start();
        using var client = service.Client();

        var me = await client.GetMeAsync(Ct);
        Assert.Contains(WatchdogRole.Admin, me.Roles);
        await Poll.UntilAsync(() => client.GetStatusAsync(Ct), s => s.Directory.BaselineCompletedAt is not null, Timeout, "the baseline", Ct);

        // Configuration through the API (as the GUI does it): SMTP, recipients, a signed webhook, no holds or windows –
        // the new-accounts rule limited to the test's OU (the service watches the whole shared lab).
        var ou = await lab.CreateTestOuAsync("Usługa e2e", Ct);
        var envelope = await client.GetConfigAsync(Ct);
        var webhookUrl = new Uri(webhook.BaseAddress, $"hooks/watchdog?token={webhookToken}").ToString();
        var saved = await client.SaveConfigAsync(envelope with
        {
            Configuration = Configure(envelope.Configuration, mailbox.Settings(SmtpUser, smtpPassword), recipient, webhookUrl, signingSecret, service.Address, ou),
        }, Ct);
        Assert.Equal(envelope.Version + 1, saved.Version);
        Assert.True(saved.Configuration.Smtp.Password.IsSet);
        var stored = JsonSerializer.Serialize(await client.GetConfigAsync(Ct), WatchdogJson.Options);
        Assert.DoesNotContain(smtpPassword, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(signingSecret, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(webhookToken, stored, StringComparison.Ordinal);

        // A change in the lab, made as Administrator.
        var mark = (await client.GetEventsAsync(new EventQuery { Limit = 1 }, Ct)).LatestId;
        var user = await lab.Writer.CreateUserAsync(ou, "Łucja", "Ćwik", TestNames.Password(), new Dictionary<string, string>
        {
            [Attr.DisplayName] = DisplayName,
            [Attr.Department] = "Kadry i płace",
        }, Ct);

        var created = await WaitForEventAsync(client, mark, ou, WatchEventKind.UserCreated, user.Dn);
        Assert.Equal(DisplayName, created.Object.DisplayName);

        bool AboutTheUser(ReceivedMail m) => m.Subject.Contains(user.SamAccountName, StringComparison.OrdinalIgnoreCase);
        var mail = (await Poll.UntilAsync(() => mailbox.ReceivedAsync(recipient, Ct), mails => mails.Any(AboutTheUser), Timeout,
            "the e-mail about the new account", Ct)).First(AboutTheUser);
        Assert.StartsWith("[AD Watchdog]", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("Nowe konto", mail.Subject, StringComparison.Ordinal);
        Assert.Equal([recipient], mail.To);
        Assert.Contains(EncodedDisplayName, mail.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(DisplayName, mail.Html, StringComparison.Ordinal);
        Assert.Contains("Łucja Ćwik", mail.Text, StringComparison.Ordinal);
        foreach (var secret in new[] { smtpPassword, signingSecret, webhookToken, WatchdogLab.ReadOnlyPassword })
        {
            Assert.DoesNotContain(secret, mail.Raw, StringComparison.Ordinal);
        }

        var delivery = await Poll.UntilAsync(() => Task.FromResult(webhook.Requests.FirstOrDefault(r => TypeOf(r) == "ad.user.created" && EventIdOf(r) == created.EventId)),
            r => r is not null, Timeout, "the signed webhook", Ct);
        AssertSigned(delivery!, signingSecret, webhookToken, created.EventId);

        // Final attribution on Linux: collector mode, but the lab DC forwards nothing to OpenWEC – after the (shortened)
        // retries the event ends Unknown, the 4720 family never having been seen from that DC.
        var details = await Poll.UntilAsync(() => client.GetEventAsync(created.Id, Ct), d => d.Event.Attribution.Status != AttributionStatus.Pending,
            Timeout, "the final attribution", Ct);
        Assert.Equal(AttributionStatus.Unknown, details.Event.Attribution.Status);
        Assert.Equal(AttributionReasons.AuditPolicyMissing, details.Event.Attribution.Reason);

        var status = await client.GetStatusAsync(Ct);
        var dc = await LabDcAsync();
        Assert.Equal(HealthState.Ok, status.Directory.State);
        Assert.Equal(dc, status.Directory.DcHostName, StringComparer.OrdinalIgnoreCase);
        Assert.True(status.Attribution.Enabled);
        Assert.Equal(nameof(AttributionSourceKind.OpenWec), status.Attribution.Source);
        Assert.DoesNotContain(status.Checks, c => c.Code == "attribution.source");
        Assert.Contains(status.Checks, c => c.Code == HealthCheckCodes.Wec(dc, HealthCheckCodes.WecNotForwarding) && c.State == HealthState.Error);

        AssertLogIsClean(service.Log, smtpPassword, signingSecret, webhookToken, recipient);
    }

    /// <summary>The test's settings on top of the stored document: SMTP, recipients, the webhook on the new-accounts rule
    /// (limited to <paramref name="scope"/>, always, without aggregation or hourly cap), rollout ended, short poll intervals
    /// and a single attribution retry.</summary>
    private static WatchdogConfiguration Configure(WatchdogConfiguration current, SmtpSettings smtp, string recipient, string webhookUrl,
        string signingSecret, Uri publicBaseUrl, string scope) => current with
        {
            General = current.General with { InstanceName = "AD Watchdog – test e2e", Language = "pl", PublicBaseUrl = publicBaseUrl.ToString().TrimEnd('/'), RolloutEnded = true },
            Directory = current.Directory with { PollInterval = TimeSpan.FromSeconds(5), CreationSettleWindow = TimeSpan.Zero },
            Attribution = current.Attribution with { PollInterval = TimeSpan.FromSeconds(10), PendingRetries = [TimeSpan.FromSeconds(15)] },
            Notifications = current.Notifications with { AggregationWindow = TimeSpan.Zero, AttributionGrace = TimeSpan.Zero, HighSeverityFlushDelay = TimeSpan.Zero },
            Smtp = smtp with { From = "AD Watchdog <watchdog@contoso.test>", LocalDomain = "watchdog.contoso.test" },
            RecipientGroups = [.. current.RecipientGroups.Select(g => g.Id == DefaultRules.ItAdminsGroup ? g with { Addresses = [recipient] } : g)],
            Channels =
            [
                .. current.Channels,
                DefaultConfigurationFactory.CreateChannel(WebhookChannel, ChannelType.Webhook, "SIEM – test e2e") with
                {
                    Url = new SecretField { NewValue = webhookUrl },
                    Webhook = new WebhookChannelOptions { SigningSecret = new SecretField { NewValue = signingSecret } },
                },
            ],
            Rules =
            [
                .. current.Rules.Select(r => r.Id == DefaultRules.NewAccounts
                    ? r with
                    {
                        Scope = new RuleScope { IncludeSubtrees = [scope] },
                        Delivery = r.Delivery with { Hours = DeliveryHours.Always, AggregationWindow = TimeSpan.Zero, MaxMessagesPerHour = 1000, ChannelIds = [WebhookChannel] },
                    }
                    : r),
            ],
        };

    /// <summary>Long-polls the API (wait=25 s) until the event of <paramref name="kind"/> about <paramref name="dn"/> exists.</summary>
    private static async Task<WatchEvent> WaitForEventAsync(IWatchdogClient client, long after, string subtree, WatchEventKind kind, string dn)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(Timeout);
        var cursor = after;
        while (true)
        {
            EventPage page;
            try
            {
                page = await client.GetEventsAsync(new EventQuery { After = cursor, Subtree = subtree, Limit = 500, WaitSeconds = 25 }, deadline.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                Assert.Fail($"No {kind} event for {dn} within {Timeout}.");
                throw;
            }

            if (page.Items.FirstOrDefault(e => e.Kind == kind && DistinguishedName.AreEqual(e.Object.Dn, dn)) is { } found)
            {
                return found;
            }

            cursor = Math.Max(cursor, page.Items.Count > 0 ? page.Items.Max(e => e.Id) : cursor);
        }
    }

    private static void AssertSigned(HttpSinkRequest request, string signingSecret, string token, Guid eventId)
    {
        Assert.Equal("POST", request.Method);
        Assert.Contains("token=" + token, request.PathAndQuery, StringComparison.Ordinal);
        var id = request.Headers[StandardWebhookSigner.IdHeader];
        var timestamp = long.Parse(request.Headers[StandardWebhookSigner.TimestampHeader], CultureInfo.InvariantCulture);
        Assert.True(StandardWebhookSigner.Verify(request.Headers[StandardWebhookSigner.SignatureHeader], new SecretValue(signingSecret), id, timestamp, request.Body),
            "the webhook signature does not verify");
        Assert.Equal(eventId, EventIdOf(request));
    }

    private static Guid? EventIdOf(HttpSinkRequest request)
    {
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            return json.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("eventId", out var id) && id.TryGetGuid(out var guid) ? guid : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TypeOf(HttpSinkRequest request)
    {
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            return json.RootElement.GetProperty("type").GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AssertLogIsClean(CapturingLoggerProvider log, params string[] secrets)
    {
        Assert.NotEmpty(log.Lines);
        foreach (var value in secrets.Concat(["Łucja", "Ćwik", "Kadry", WatchdogLab.ReadOnlyPassword, "OU=", "CN="]))
        {
            var leaks = log.Lines.Where(l => l.Contains(value, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            Assert.True(leaks.Count == 0, $"the log contains a forbidden value: {string.Join(" | ", leaks.Select(l => l.Split('\n')[0]))}");
        }
    }

    private async Task<string> LabDcAsync()
    {
        var rootDse = await lab.Admin.GetAsync(string.Empty, [Attr.DnsHostNameRootDse], Ct);
        return Assert.IsType<string>(rootDse?.GetString(Attr.DnsHostNameRootDse));
    }
}
