using System.Net;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Service.Api;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Service.Tests.Api;

/// <summary><c>/status</c>, the HTML event page, digest and rule preview endpoints (spec 17.2).</summary>
public sealed class StatusAndPageApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Status_ReportsVersionStorageCountsAndChannels()
    {
        await using var factory = new WatchdogFactory();
        await factory.Store.AppendAsync(TestData.Event(WatchEventKind.UserCreated), Ct);
        await factory.Store.EnqueueAsync(TestData.Message(channelId: "siem"), Ct);
        factory.Services.GetRequiredService<IHealthRegistry>().ReportChannel(new ChannelHealthDto
        {
            ChannelId = "siem",
            ChannelType = DeliveryChannelType.Webhook,
            State = HealthState.Ok,
        });
        using var viewer = factory.Viewer();
        using var admin = factory.Admin();

        var status = await ApiJson.ReadAsync<StatusDto>(await viewer.GetAsync(WatchdogRoutes.Status, Ct));
        var adminStatus = await ApiJson.ReadAsync<StatusDto>(await admin.GetAsync(WatchdogRoutes.Status, Ct));

        Assert.Equal(WatchdogRoutes.ApiVersion, status.ApiVersion);
        Assert.Equal(TestStore.Start, status.Now);
        Assert.Equal(1, status.Last24Hours.Total);
        Assert.Equal(1, Assert.Single(status.Channels).Pending);
        Assert.Equal(string.Empty, status.Storage.DataDirectory);
        Assert.Equal(factory.DataDirectory.Path, adminStatus.Storage.DataDirectory);
    }

    [Fact]
    public async Task Status_CarriesTheStageBSections_OnceReported()
    {
        await using var factory = new WatchdogFactory();
        using var viewer = factory.Viewer();

        var before = await ApiJson.ReadAsync<StatusDto>(await viewer.GetAsync(WatchdogRoutes.Status, Ct));
        var health = factory.Services.GetRequiredService<IHealthRegistry>();
        health.ReportServiceAccount(new ServiceAccountStatusDto { State = HealthState.Ok, Principal = "ADMNG-WD$@CONTOSO.TEST", KeytabKvno = 2, DirectoryKvno = 2 });
        health.ReportCollector(new CollectorStatusDto
        {
            State = HealthState.Error,
            DomainControllers = [new CollectorDcStatusDto { Dc = "dc01.contoso.test", State = HealthState.Error, RejectedRecords24h = 3 }],
        });
        health.ReportGpoContent(new GpoContentStatusDto { State = HealthState.Warning, Enabled = true, TotalGpos = 4, ReadGpos = 4, SysvolLaggingGpos = 1 });
        health.ReportHost(new HostStatusDto { OperatingSystem = "Debian GNU/Linux 13 (trixie)", CredentialKey = HostCredentialKeys.Tpm2Host, LdapSsf = 256 });
        var after = await ApiJson.ReadAsync<StatusDto>(await viewer.GetAsync(WatchdogRoutes.Status, Ct));

        Assert.Null(before.ServiceAccount);
        Assert.Null(before.Collector);
        Assert.Null(before.GpoContent);
        Assert.Null(before.Host);
        Assert.Equal(2, after.ServiceAccount?.KeytabKvno);
        Assert.Equal(3, Assert.Single(after.Collector!.DomainControllers).RejectedRecords24h);
        Assert.Equal(1, after.GpoContent?.SysvolLaggingGpos);
        Assert.Equal(HostCredentialKeys.Tpm2Host, after.Host?.CredentialKey);
    }

    [Fact]
    public async Task EventPage_IsRenderedWithStrictHeaders_AtTheViewerLevel()
    {
        var renderer = new CapturingRenderer();
        await using var factory = new WatchdogFactory(services: s => s.AddSingleton<IEventHtmlRenderer>(renderer));
        var change = new AttributeChange { Attribute = "mobile", NewValues = ["+48 601 234 567"] };
        var watchEvent = TestData.Event(WatchEventKind.AttributesChanged, changes: [change]);
        await factory.Store.AppendAsync(watchEvent, Ct);
        using var viewer = factory.Viewer();

        using var response = await viewer.GetAsync(WatchdogRoutes.EventPagePath(watchEvent.EventId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(EventPageEndpoint.ContentSecurityPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(Assert.Single(renderer.Rendered).Event.Changes[0].Redacted);
        Assert.Contains(factory.Log.Entries, e => e.EventId.Id == 2601 && e.Message.Contains(watchEvent.EventId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task EventPage_UsesTheCoreRenderer_AtTheCallersLevel_AndUnknownEvents_404()
    {
        await using var factory = new WatchdogFactory();
        var change = new AttributeChange { Attribute = "mobile", OldValues = ["+48 601 234 567"], NewValues = ["+48 602 765 432"], OldCount = 1, NewCount = 1 };
        var watchEvent = TestData.Event(WatchEventKind.AttributesChanged, changes: [change]);
        await factory.Store.AppendAsync(watchEvent, Ct);
        using var viewer = factory.Viewer();
        using var admin = factory.Admin();

        using var missing = await viewer.GetAsync(WatchdogRoutes.EventPagePath(Guid.NewGuid()), Ct);
        var viewerPage = await (await viewer.GetAsync(WatchdogRoutes.EventPagePath(watchEvent.EventId), Ct)).Content.ReadAsStringAsync(Ct);
        var adminPage = await (await admin.GetAsync(WatchdogRoutes.EventPagePath(watchEvent.EventId), Ct)).Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("Zofia Żak", viewerPage, StringComparison.Ordinal);
        Assert.DoesNotContain("+48 602 765 432", viewerPage, StringComparison.Ordinal);   // personal data: Standard hides it
        Assert.Contains("+48 602 765 432", adminPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DigestAndRulePreview_UseTheCoreServices_OrRegisteredOnes()
    {
        await using var core = new WatchdogFactory();
        using var coreAdmin = core.Admin();
        var corePreview = await ApiJson.ReadAsync<DigestPreviewDto>(await coreAdmin.GetAsync(WatchdogRoutes.DigestPreview, Ct));
        Assert.NotEmpty(corePreview.Subject);
        Assert.Contains("<html", corePreview.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Accepted, (await coreAdmin.PostAsync(WatchdogRoutes.DigestSend, null, Ct)).StatusCode);

        var digest = new FakeDigestService();
        await using var factory = new WatchdogFactory(services: s => s.AddSingleton<IDigestService>(digest));
        using var admin = factory.Admin();
        using var viewer = factory.Viewer();

        var preview = await ApiJson.ReadAsync<DigestPreviewDto>(await viewer.GetAsync(WatchdogRoutes.DigestPreview, Ct));
        using var sent = await admin.PostAsync(WatchdogRoutes.DigestSend, null, Ct);
        using var forbidden = await viewer.PostAsync(WatchdogRoutes.DigestSend, null, Ct);

        Assert.Equal("Podsumowanie dnia", preview.Subject);
        Assert.Equal(Contracts.Configuration.DetailLevel.Standard, digest.PreviewLevel);   // the viewer's level
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(1, digest.Sent);
    }

    private sealed class CapturingRenderer : IEventHtmlRenderer
    {
        public List<EventDetails> Rendered { get; } = [];

        public string Render(EventDetails details)
        {
            Rendered.Add(details);
            return "<!DOCTYPE html><html><body>zdarzenie</body></html>";
        }
    }

    private sealed class FakeDigestService : IDigestService
    {
        public int Sent { get; private set; }

        public DateTimeOffset? NextScheduledAt => TestStore.Start.AddHours(1);

        public Contracts.Configuration.DetailLevel? PreviewLevel { get; private set; }

        public Task<DigestPreviewDto> PreviewAsync(Contracts.Configuration.DetailLevel callerLevel, CancellationToken cancellationToken)
        {
            PreviewLevel = callerLevel;
            return PreviewAsync(cancellationToken);
        }

        public Task<DigestPreviewDto> PreviewAsync(CancellationToken cancellationToken) => Task.FromResult(new DigestPreviewDto
        {
            PeriodStart = TestStore.Start.AddDays(-1),
            PeriodEnd = TestStore.Start,
            Subject = "Podsumowanie dnia",
            Text = "Brak zdarzeń.",
            Html = "<p>Brak zdarzeń.</p>",
        });

        public Task SendNowAsync(CancellationToken cancellationToken)
        {
            Sent++;
            return Task.CompletedTask;
        }
    }
}
