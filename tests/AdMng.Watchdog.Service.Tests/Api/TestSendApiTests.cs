using System.Net;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Service.Tests.Notifications;
using AdMng.Watchdog.Testing.Smtp;

namespace AdMng.Watchdog.Service.Tests.Api;

/// <summary><c>/api/v1/test/*</c> (spec 17.2): stage results, Admin only, and the binding preflight that sends nothing.</summary>
public sealed class TestSendApiTests : IAsyncLifetime
{
    private const string Password = "Hasło-Wąż-2026!";

    private readonly SmtpSink _sink = new(new SmtpSinkOptions
    {
        Security = SmtpSinkSecurity.StartTls,
        RequireAuthentication = true,
        Accounts = new Dictionary<string, string> { ["watchdog@contoso.test"] = Password },
    });

    private readonly WatchdogFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _sink.StartAsync(Ct);

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _sink.DisposeAsync();
    }

    [Fact]
    public async Task EmailTest_WithSavedSettings_SendsAndReportsStages()
    {
        using var admin = _factory.Admin();
        await SaveAsync(admin, Smtp());

        var result = await ApiJson.ReadAsync<DeliveryTestResult>(await admin.PostAsync(
            WatchdogRoutes.TestEmail, ApiJson.Content(new TestEmailRequest { Recipients = ["jan.kowalski@contoso.test"] }), Ct));

        Assert.True(result.Success);
        Assert.Contains(result.Stages, s => s.Stage == DeliveryStage.Authenticate && s.Success);
        Assert.Equal("watchdog@contoso.test", Assert.Single(_sink.Messages).AuthenticatedUser);
        Assert.Empty(await _factory.Store.CountPendingByChannelAsync(Ct));
    }

    [Fact]
    public async Task EmailTest_WithAnotherHost_AndTheStoredPassword_Is400_AndNothingIsSent()
    {
        using var admin = _factory.Admin();
        await SaveAsync(admin, Smtp());
        var redirected = Smtp() with { Host = "localhost", Password = new SecretField { IsSet = true } };

        using var response = await admin.PostAsync(
            WatchdogRoutes.TestEmail,
            ApiJson.Content(new TestEmailRequest { Recipients = ["jan.kowalski@contoso.test"], Settings = redirected }),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("secretRebindRequired", await ApiJson.IssueCodesAsync(response));
        Assert.Empty(_sink.AuthenticationAttempts);
        Assert.Empty(_sink.Messages);
    }

    [Fact]
    public async Task EmailTest_WithOAuthToAForeignHost_RequestsNoToken()
    {
        using var admin = _factory.Admin();
        var settings = Smtp() with
        {
            Host = "smtp.attacker.example",
            Authentication = SmtpAuthentication.OAuth2ClientCredentials,
            Password = new SecretField(),
            OAuth = new SmtpOAuthSettings
            {
                TenantId = "6f1d3c2a-8b4e-4f5a-9c7d-2e1f0a3b4c5d",
                ClientId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d",
                ClientSecret = new SecretField { NewValue = "sekret-klienta" },
            },
        };

        var result = await ApiJson.ReadAsync<DeliveryTestResult>(await admin.PostAsync(
            WatchdogRoutes.TestEmail, ApiJson.Content(new TestEmailRequest { Recipients = ["jan.kowalski@contoso.test"], Settings = settings }), Ct));

        Assert.False(result.Success);
        Assert.Empty(_factory.Tokens.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task EmailTest_NeedsOneToTenRecipients(int count)
    {
        using var admin = _factory.Admin();

        using var response = await admin.PostAsync(
            WatchdogRoutes.TestEmail,
            ApiJson.Content(new TestEmailRequest { Recipients = [.. Enumerable.Range(0, count).Select(i => $"pomoc{i}@contoso.test")] }),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ViewerCannotTest()
    {
        using var viewer = _factory.Viewer();

        using var response = await viewer.PostAsync(WatchdogRoutes.TestEmail, ApiJson.Content(new TestEmailRequest { Recipients = ["a@contoso.test"] }), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ChannelTest_ForAnUnknownChannel_Is404_AndAMismatchedId_400()
    {
        using var admin = _factory.Admin();

        using var unknown = await admin.PostAsync(WatchdogRoutes.TestChannelPath("brak"), null, Ct);
        using var mismatch = await admin.PostAsync(
            WatchdogRoutes.TestChannelPath("siem"),
            ApiJson.Content(new TestChannelRequest { Settings = new ChannelSettings { Id = "inny", Type = ChannelType.Webhook, Name = "Inny" } }),
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
    }

    private SmtpSettings Smtp() =>
        SmtpEmailTransportTests.Configuration(_sink.Port, SmtpSecurity.StartTls, pinned: true, basic: true).Smtp with
        {
            Password = new SecretField { NewValue = Password },
        };

    private static async Task SaveAsync(HttpClient admin, SmtpSettings smtp)
    {
        using var response = await admin.PutAsync(
            WatchdogRoutes.Config,
            ApiJson.Content(new ConfigEnvelope { Version = 0, Configuration = TestData.Configuration() with { Smtp = smtp } }),
            Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
    }
}
