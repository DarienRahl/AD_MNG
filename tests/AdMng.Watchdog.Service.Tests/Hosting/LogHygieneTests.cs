using System.Net;
using System.Xml;
using AdMng.Core.Directory;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Notifications;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Service.Tests.Notifications;
using AdMng.Watchdog.Testing.Smtp;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>
/// Spec 0.1 / 22.2: a service scenario with fault injections – LDAP NoSuchObject on a Polish DN, malformed event XML,
/// an AD value that fails to parse, a recipient rejected with 550, an unhandled exception in an endpoint and in jobs –
/// and no directory value, recipient address or secret reaches any log line or error response.
/// </summary>
public sealed class LogHygieneTests
{
    private const string PolishDn = "CN=Zofia Żak,OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test";
    private const string Password = "Hasło-Wąż-2026!";

    private static readonly string[] Forbidden = ["Zofia", "Żak", "Księgowość", "zofia.zak@contoso.test", Password, "tajny-żeton", "whsec_c2Vr"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FaultsAndTheNormalScenario_LeaveNoDirectoryValuesOrSecretsInTheLog()
    {
        await using var smtp = new ScriptedSmtpServer
        {
            Script = line => line.Contains("zofia.zak", StringComparison.OrdinalIgnoreCase) ? "550 5.1.1 <zofia.zak@contoso.test>: Recipient address rejected (Zofia Żak)" : null,
        };
        await smtp.StartAsync();
        var faults = new FaultyJob();
        await using var factory = new WatchdogFactory(services: services =>
        {
            services.AddSingleton<IWatchdogJob>(faults);
            services.AddSingleton<IDigestService>(new ThrowingDigest());
        });
        using var admin = factory.Admin();

        // Normal scenario: configuration with secrets, events, details, acknowledgement.
        var configuration = SmtpEmailTransportTests.Configuration(smtp.Port, SmtpSecurity.None) with
        {
            Channels =
            [
                new ChannelSettings
                {
                    Id = "siem",
                    Type = ChannelType.Webhook,
                    Name = "SIEM",
                    Url = new SecretField { NewValue = "https://siem.contoso.test/hooks?token=tajny-żeton" },
                    Webhook = new WebhookChannelOptions { SigningSecret = new SecretField { NewValue = "whsec_c2VrcmV0LXNla3JldC1zZWtyZXQtc2VrcmV0LTEyMw==" } },
                },
            ],
        };
        using var put = await admin.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = 0, Configuration = configuration }), Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var id = await factory.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated, attribution: TestData.Resolved()), Ct);
        await admin.GetAsync(WatchdogRoutes.EventByIdPath(id), Ct);
        await admin.GetAsync(WatchdogRoutes.Events + "?text=" + Uri.EscapeDataString("Zofia Żak"), Ct);
        await admin.PostAsync(WatchdogRoutes.EventAcknowledgePath(id), ApiJson.Content(new AcknowledgeRequest { Comment = "Zofia Żak – sprawdzone" }), Ct);

        // Fault: SMTP 550 for a recipient (the only one) – through the real transport of the service.
        var transport = factory.Services.GetRequiredService<SmtpEmailTransport>();
        var result = await transport.SendAsync(SmtpEmailTransportTests.Message() with { Recipients = ["zofia.zak@contoso.test"] }, Ct);
        Assert.Equal(Core.Model.DeliveryOutcome.Permanent, result.Outcome);

        // Fault: unhandled exception in an endpoint.
        using var boom = await admin.GetAsync(WatchdogRoutes.DigestPreview, Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, boom.StatusCode);
        var body = await boom.Content.ReadAsStringAsync(Ct);

        // Faults in a job: LDAP NoSuchObject on a Polish DN, malformed XML, an int.Parse failure on an AD value
        // (each failure backs off on the fake clock).
        while (!faults.Done.IsCompleted)
        {
            var timer = factory.Clock.WaitForAnyTimerAsync();
            factory.Clock.Advance(TimeSpan.FromMinutes(5));
            await Task.WhenAny(timer, faults.Done).WaitAsync(Ct);
        }

        var log = factory.Log.Text;
        Assert.Contains("DirectoryException", log, StringComparison.Ordinal);
        Assert.Contains("XmlException", log, StringComparison.Ordinal);
        Assert.Contains("FormatException", log, StringComparison.Ordinal);
        foreach (var value in Forbidden)
        {
            Assert.DoesNotContain(value, log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(value, body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.All(factory.Log.Entries, e => Assert.Null(e.Exception));
    }

    /// <summary>Throws one fault per iteration (the job host logs and backs off), then reports done.</summary>
    private sealed class FaultyJob : IWatchdogJob
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _iteration;

        public Task Done => _done.Task;

        public string Name => "faults";

        public TimeSpan DefaultInterval => TimeSpan.Zero;

        public IReadOnlyList<WatchdogSignal> WakeSignals => [];

        public Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
        {
            switch (++_iteration)
            {
                case 1:
                    throw new DirectoryException(DirectoryErrorKind.NoSuchObject, $"No such object: {PolishDn}");
                case 2:
                    var xml = new XmlDocument();
                    xml.LoadXml($"<Event><EventData><Data Name='ObjectDN'>{PolishDn}</Data></Event>");
                    break;
                case 3:
                    _ = int.Parse("Zofia Żak", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    _done.TrySetResult();
                    break;
            }

            return Task.FromResult(new JobResult(TimeSpan.FromHours(1)));
        }
    }

    private sealed class ThrowingDigest : IDigestService
    {
        public DateTimeOffset? NextScheduledAt => null;

        public Task<DigestPreviewDto> PreviewAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Digest failed for {PolishDn} (zofia.zak@contoso.test)");

        public Task SendNowAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
