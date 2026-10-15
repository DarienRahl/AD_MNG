using System.Net;
using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary><c>GET /api/v1/digest/preview</c> (spec 13.4, 17.2): the preview is served to Viewers, who never see rule
/// exclusions (the redacted configuration keeps only their number) – so the preview shows only the number.</summary>
public sealed class DigestPreviewApiTests : IAsyncLifetime
{
    private const string ActorSid = "S-1-5-21-1004336348-1177238915-682003330-7777";
    private const string Subtree = "OU=Ukryte konta,OU=Firma,DC=contoso,DC=test";
    private const string Reason = "Konto synchronizacji Entra Connect – zgłoszenie ZGŁ-4711";

    private readonly WatchdogFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Viewer_SeesOnlyTheNumberOfActiveExceptions()
    {
        using var admin = _factory.Admin();
        var current = await ApiJson.ReadAsync<ConfigEnvelope>(await admin.GetAsync(WatchdogRoutes.Config, Ct));
        var rule = current.Configuration.Rules[0];
        var exclusions = new[]
        {
            new RuleExclusion { Id = "konto-synchronizacji", Actor = ActorSid, Reason = Reason, CreatedBy = @"CONTOSO\adm.wójcik", CreatedAt = TestStore.Start },
            new RuleExclusion { Id = "ukryte-konta", SubtreeDn = Subtree, Reason = "Konta techniczne działu księgowości", CreatedBy = @"CONTOSO\adm.wójcik", CreatedAt = TestStore.Start },
        };
        var changed = current.Configuration with { Rules = [rule with { Exclusions = exclusions }, .. current.Configuration.Rules.Skip(1)] };
        using (var put = await admin.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = current.Version, Configuration = changed }), Ct))
        {
            Assert.True(put.StatusCode == HttpStatusCode.OK, $"{put.StatusCode}: {await put.Content.ReadAsStringAsync(Ct)}");
        }

        using var viewer = _factory.Viewer();
        foreach (var client in new[] { viewer, admin })
        {
            using var response = await client.GetAsync(WatchdogRoutes.DigestPreview, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(Ct);
            var preview = JsonSerializer.Deserialize<DigestPreviewDto>(body, WatchdogJson.Options)!;

            foreach (var hidden in new[] { "7777", "Ukryte konta", "ZGŁ-4711", "księgowości", "adm.wójcik" })
            {
                Assert.DoesNotContain(hidden, preview.Text, StringComparison.Ordinal);
                Assert.DoesNotContain(hidden, preview.Html, StringComparison.Ordinal);
                Assert.DoesNotContain(hidden, body, StringComparison.Ordinal);
            }

            Assert.Contains("Liczba aktywnych wyjątków (szczegóły tylko w wysłanym raporcie): 2", preview.Text, StringComparison.Ordinal);
        }
    }
}
