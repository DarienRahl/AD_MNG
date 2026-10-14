using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Notifications;
using AdMng.Watchdog.Service.Secrets;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Service.Tests.Fakes;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Watchdog.Service.Tests.Notifications;

/// <summary>Secrets are released only to the destination they were stored for (spec 15.6, 15.7, 18).</summary>
public sealed class DestinationGuardTests : IAsyncLifetime
{
    private const string PowerPlatformUrl = "https://contoso.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/abc/triggers/manual/paths/invoke";

    private readonly TestStore _store = TestStore.Create();
    private readonly StaticOptionsMonitor<ServiceOptions> _options = new(new ServiceOptions());
    private DestinationGuard _guard = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _guard = new DestinationGuard(_store.Store, _options);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    [Fact]
    public async Task MatchingBinding_ReleasesTheStoredSecret()
    {
        var smtp = Smtp();
        await _store.Store.SetAsync(SecretNames.SmtpPassword, new SecretValue("Hasło-Wąż-2026!"), SecretBindings.ForSmtpPassword(smtp), Ct);

        var released = await _guard.SmtpPasswordAsync(smtp, Ct);

        Assert.True(released.IsAvailable);
        Assert.Equal("Hasło-Wąż-2026!", released.Value!.Reveal());
        Assert.DoesNotContain("Hasło", released.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("smtp.attacker.example", 587, "watchdog@contoso.test")]
    [InlineData("smtp.contoso.test", 25, "watchdog@contoso.test")]
    [InlineData("smtp.contoso.test", 587, "ktos.inny@contoso.test")]
    public async Task ChangedDestination_RequiresANewSecret(string host, int port, string userName)
    {
        await _store.Store.SetAsync(SecretNames.SmtpPassword, new SecretValue("Hasło-Wąż-2026!"), SecretBindings.ForSmtpPassword(Smtp()), Ct);

        var refused = await _guard.SmtpPasswordAsync(Smtp() with { Host = host, Port = port, UserName = userName }, Ct);

        Assert.False(refused.IsAvailable);
        Assert.Equal(DestinationGuard.RebindRequired, refused.FailureCode);
        Assert.Equal(DeliveryHints.SecretRebindRequired, refused.Hint);
    }

    [Fact]
    public async Task NewValue_IsReleasedForTheUnsavedSettings_WithoutTheStore()
    {
        var released = await _guard.SmtpPasswordAsync(Smtp() with { Password = new SecretField { NewValue = "Nowe-Hasło-1!" } }, Ct);

        Assert.Equal("Nowe-Hasło-1!", released.Value!.Reveal());
    }

    [Fact]
    public async Task ClearedOrMissing_IsRefused()
    {
        await _store.Store.SetAsync(SecretNames.SmtpPassword, new SecretValue("Hasło-Wąż-2026!"), SecretBindings.ForSmtpPassword(Smtp()), Ct);

        Assert.Equal("secret.missing", (await _guard.SmtpPasswordAsync(Smtp() with { Password = new SecretField { Clear = true } }, Ct)).FailureCode);
        await _store.Store.RemoveAsync(SecretNames.SmtpPassword, Ct);
        Assert.Equal("secret.missing", (await _guard.SmtpPasswordAsync(Smtp(), Ct)).FailureCode);
    }

    [Fact]
    public async Task UnreadableSecret_IsRefusedWithItsHealthCode()
    {
        using var directory = new TestDataDirectory();
        var original = SqliteWatchdogStore.Open(directory.Path, new SecretProtector(new EphemeralDataProtectionProvider()), TimeProvider.System, NullLogger<MigrationRunner>.Instance);
        await original.SetAsync(SecretNames.SmtpPassword, new SecretValue("Hasło-Wąż-2026!"), SecretBindings.ForSmtpPassword(Smtp()), Ct);
        await original.DisposeAsync();
        var reopened = SqliteWatchdogStore.Open(directory.Path, new SecretProtector(new EphemeralDataProtectionProvider()), TimeProvider.System, NullLogger<MigrationRunner>.Instance);
        try
        {
            var refused = await new DestinationGuard(reopened, _options).SmtpPasswordAsync(Smtp(), Ct);

            Assert.Equal("secret.unreadable", refused.FailureCode);
        }
        finally
        {
            await reopened.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("smtp.office365.com", true)]
    [InlineData("SMTP.Office365.COM.", true)]
    [InlineData("smtp.office365.com.attacker.example", false)]
    [InlineData("smtp-office365.com", false)]
    [InlineData(null, false)]
    public void OAuthHosts_DefaultToExchangeOnline(string? host, bool allowed)
    {
        Assert.Equal(allowed, _guard.IsOAuthSmtpHostAllowed(Smtp() with { Host = host }));
    }

    [Fact]
    public async Task OAuthClientSecret_NeverLeavesForAHostOutsideTheList()
    {
        var smtp = Smtp() with
        {
            Host = "smtp.attacker.example",
            Authentication = SmtpAuthentication.OAuth2ClientCredentials,
            OAuth = new SmtpOAuthSettings { TenantId = Guid.NewGuid().ToString(), ClientId = Guid.NewGuid().ToString(), ClientSecret = new SecretField { NewValue = "x" } },
        };

        var refused = await _guard.SmtpOAuthClientSecretAsync(smtp, Ct);

        Assert.Equal("oauthHostNotAllowed", refused.FailureCode);
    }

    [Fact]
    public void OAuthHosts_ComeFromTheBootstrapOptions()
    {
        _options.CurrentValue = new ServiceOptions { Notifications = new NotificationBootstrapOptions { OAuthSmtpHosts = ["smtp.office365.us"] } };

        Assert.True(_guard.IsOAuthSmtpHostAllowed(Smtp() with { Host = "smtp.office365.us" }));
        Assert.False(_guard.IsOAuthSmtpHostAllowed(Smtp() with { Host = "smtp.office365.com" }));
    }

    [Fact]
    public async Task TeamsClientSecret_OnlyForPowerPlatformUrls_AndBoundToTheirHost()
    {
        var channel = new ChannelSettings
        {
            Id = "teams-pomoc",
            Type = ChannelType.Teams,
            Name = "Teams",
            Teams = new TeamsChannelOptions
            {
                AuthMode = TeamsAuthMode.EntraClientCredentials,
                TenantId = Guid.NewGuid().ToString(),
                ClientId = Guid.NewGuid().ToString(),
                ClientSecret = new SecretField { IsSet = true },
            },
        };
        await _store.Store.SetAsync(
            SecretNames.ChannelClientSecret(channel.Id),
            new SecretValue("sekret-aplikacji-ąę"),
            SecretBindings.ForTeamsClientSecret(channel, SecretBindings.UrlHost(PowerPlatformUrl)),
            Ct);

        Assert.True((await _guard.TeamsClientSecretAsync(channel, PowerPlatformUrl, Ct)).IsAvailable);
        Assert.Equal("teams.hostNotAllowed", (await _guard.TeamsClientSecretAsync(channel, "https://attacker.example/powerautomate/automations/direct/workflows/x", Ct)).FailureCode);
        Assert.Equal(
            DestinationGuard.RebindRequired,
            (await _guard.TeamsClientSecretAsync(channel, PowerPlatformUrl.Replace("contoso.", "fabrikam.", StringComparison.Ordinal), Ct)).FailureCode);
        Assert.True(DestinationGuard.IsTeamsTokenDestination(PowerPlatformUrl));
        Assert.False(DestinationGuard.IsTeamsTokenDestination("https://contoso.environment.api.powerplatform.com:8443/powerautomate/automations/direct/workflows/x"));
    }

    [Fact]
    public async Task WebhookSigningSecret_IsBoundToTheUrlHost()
    {
        var channel = new ChannelSettings
        {
            Id = "siem",
            Type = ChannelType.Webhook,
            Name = "SIEM",
            Webhook = new WebhookChannelOptions { SigningSecret = new SecretField { IsSet = true } },
        };
        await _store.Store.SetAsync(
            SecretNames.WebhookSigningSecret("siem"),
            new SecretValue("whsec_c2VrcmV0LXNla3JldC1zZWtyZXQtc2VrcmV0LTEyMw=="),
            SecretBindings.ForWebhookSigningSecret(channel, "siem.contoso.test"),
            Ct);

        Assert.True((await _guard.WebhookSigningSecretAsync(channel, "https://siem.contoso.test/hooks", previous: false, Ct)).IsAvailable);
        Assert.Equal(
            DestinationGuard.RebindRequired,
            (await _guard.WebhookSigningSecretAsync(channel, "https://siem.attacker.example/hooks", previous: false, Ct)).FailureCode);
        Assert.Equal("secret.missing", (await _guard.WebhookSigningSecretAsync(channel, "https://siem.contoso.test/hooks", previous: true, Ct)).FailureCode);
    }

    private static SmtpSettings Smtp() => new()
    {
        Enabled = true,
        Host = "smtp.contoso.test",
        Port = 587,
        Authentication = SmtpAuthentication.Basic,
        UserName = "watchdog@contoso.test",
        Password = new SecretField { IsSet = true },
    };
}
