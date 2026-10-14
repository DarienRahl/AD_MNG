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
    public async Task EventPage_WithoutARenderer_Is503_AndUnknownEvents_404()
    {
        await using var factory = new WatchdogFactory();
        using var viewer = factory.Viewer();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await viewer.GetAsync(WatchdogRoutes.EventPagePath(Guid.NewGuid()), Ct)).StatusCode);

        await using var withRenderer = new WatchdogFactory(services: s => s.AddSingleton<IEventHtmlRenderer>(new CapturingRenderer()));
        using var client = withRenderer.Viewer();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(WatchdogRoutes.EventPagePath(Guid.NewGuid()), Ct)).StatusCode);
    }

    [Fact]
    public async Task DigestAndRulePreview_AreUnavailableWithoutTheirServices_AndUseThemOtherwise()
    {
        await using var bare = new WatchdogFactory();
        using var bareAdmin = bare.Admin();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await bareAdmin.GetAsync(WatchdogRoutes.DigestPreview, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await bareAdmin.PostAsync(WatchdogRoutes.DigestSend, null, Ct)).StatusCode);

        var digest = new FakeDigestService();
        await using var factory = new WatchdogFactory(services: s => s.AddSingleton<IDigestService>(digest));
        using var admin = factory.Admin();
        using var viewer = factory.Viewer();

        var preview = await ApiJson.ReadAsync<DigestPreviewDto>(await viewer.GetAsync(WatchdogRoutes.DigestPreview, Ct));
        using var sent = await admin.PostAsync(WatchdogRoutes.DigestSend, null, Ct);
        using var forbidden = await viewer.PostAsync(WatchdogRoutes.DigestSend, null, Ct);

        Assert.Equal("Podsumowanie dnia", preview.Subject);
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
