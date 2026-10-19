using System.Net;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Service.Api;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Service.Tests.Api;

/// <summary><c>/api/v1/config*</c> (spec 17.2): role-dependent redaction, ETag/If-Match, secrets in the save transaction,
/// destination binding, audit and the configuration-changed event.</summary>
public sealed class ConfigApiTests : IAsyncLifetime
{
    private const string Password = "Hasło-Wąż-2026!";
    private const string HookUrl = "https://siem.contoso.test/hooks/watchdog?token=tajny-żeton";
    private const string SigningSecret = "whsec_c2VrcmV0LXNla3JldC1zZWtyZXQtc2VrcmV0LTEyMw==";

    private readonly WatchdogFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Put_StoresSecrets_ReturnsOnlyIsSetAndHints()
    {
        using var admin = _factory.Admin();

        var saved = await PutAsync(admin, WithSecrets(), version: 0);

        Assert.Equal(1, saved.Version);
        Assert.True(saved.Configuration.Smtp.Password.IsSet);
        Assert.Null(saved.Configuration.Smtp.Password.NewValue);
        Assert.Null(saved.Configuration.Smtp.Password.Hint);
        var channel = Assert.Single(saved.Configuration.Channels);
        Assert.Equal("https://siem.contoso.test/hooks/…", channel.Url.Hint);
        Assert.Equal("whsec_…Mw==", channel.Webhook!.SigningSecret.Hint);
        Assert.Equal(Password, (await _factory.Store.GetAsync(SecretNames.SmtpPassword, Ct))!.Value.Reveal());
        Assert.Equal(SecretBindings.ForSmtpPassword(saved.Configuration.Smtp), (await _factory.Store.ListAsync(Ct))[SecretNames.SmtpPassword]);

        using var get = await admin.GetAsync(WatchdogRoutes.Config, Ct);
        var text = await get.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain("tajny", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SigningSecret, text, StringComparison.Ordinal);
        Assert.Equal("\"1\"", get.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task Viewer_GetsARedactedConfiguration_ThatCannotBePut()
    {
        using var admin = _factory.Admin();
        await PutAsync(admin, WithSecrets(), version: 0);
        using var viewer = _factory.Viewer();

        var redacted = await ApiJson.ReadAsync<ConfigEnvelope>(await viewer.GetAsync(WatchdogRoutes.Config, Ct));

        Assert.True(redacted.IsRedacted);
        Assert.Null(redacted.Configuration.Smtp.Host);
        using var put = await admin.PutAsync(WatchdogRoutes.Config, ApiJson.Content(redacted), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("redactedConfig", await ApiJson.IssueCodesAsync(put));
    }

    [Fact]
    public async Task Clear_RemovesTheSecret()
    {
        using var admin = _factory.Admin();
        var saved = await PutAsync(admin, WithSecrets(), version: 0);

        var cleared = saved.Configuration with
        {
            Smtp = saved.Configuration.Smtp with { Authentication = SmtpAuthentication.None, Password = new SecretField { Clear = true } },
        };
        var after = await PutAsync(admin, cleared, saved.Version);

        Assert.False(after.Configuration.Smtp.Password.IsSet);
        Assert.Null(await _factory.Store.GetAsync(SecretNames.SmtpPassword, Ct));
    }

    [Fact]
    public async Task VersionConflict_Is412_AndLeavesSecretsUntouched()
    {
        using var admin = _factory.Admin();
        var saved = await PutAsync(admin, WithSecrets(), version: 0);
        var changed = saved.Configuration with { Smtp = saved.Configuration.Smtp with { Password = new SecretField { NewValue = "Inne-Hasło-9!" } } };

        using var request = new HttpRequestMessage(HttpMethod.Put, WatchdogRoutes.Config)
        {
            Content = ApiJson.Content(new ConfigEnvelope { Version = saved.Version, Configuration = changed }),
        };
        request.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("\"7\""));
        using var response = await admin.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal(ApiProblem.VersionConflict, (await ApiJson.ProblemAsync(response)).GetProperty("type").GetString());
        Assert.Equal(Password, (await _factory.Store.GetAsync(SecretNames.SmtpPassword, Ct))!.Value.Reveal());
    }

    [Fact]
    public async Task ChangedSmtpHost_WithoutANewPassword_IsRejected()
    {
        using var admin = _factory.Admin();
        var saved = await PutAsync(admin, WithSecrets(), version: 0);
        var redirected = saved.Configuration with { Smtp = saved.Configuration.Smtp with { Host = "smtp.attacker.example" } };

        using var response = await admin.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = saved.Version, Configuration = redirected }), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("secretRebindRequired", await ApiJson.IssueCodesAsync(response));
        Assert.Equal(SecretBindings.ForSmtpPassword(saved.Configuration.Smtp), (await _factory.Store.ListAsync(Ct))[SecretNames.SmtpPassword]);
    }

    [Fact]
    public async Task ChangedWebhookUrl_KeepsNoSigningSecretBoundToTheOldHost()
    {
        using var admin = _factory.Admin();
        var saved = await PutAsync(admin, WithSecrets(), version: 0);
        var channel = saved.Configuration.Channels[0] with { Url = new SecretField { NewValue = "https://attacker.example/hook" } };

        using var response = await admin.PutAsync(
            WatchdogRoutes.Config,
            ApiJson.Content(new ConfigEnvelope { Version = saved.Version, Configuration = saved.Configuration with { Channels = [channel] } }),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("secretRebindRequired", await ApiJson.IssueCodesAsync(response));
    }

    [Fact]
    public async Task RemovedChannel_RemovesItsSecrets()
    {
        using var admin = _factory.Admin();
        var saved = await PutAsync(admin, WithSecrets(), version: 0);

        await PutAsync(admin, saved.Configuration with { Channels = [] }, saved.Version);

        var names = (await _factory.Store.ListAsync(Ct)).Keys;
        Assert.DoesNotContain(SecretNames.ChannelUrl("siem"), names);
        Assert.DoesNotContain(SecretNames.WebhookSigningSecret("siem"), names);
        Assert.Contains(SecretNames.SmtpPassword, names);
    }

    [Fact]
    public async Task Audit_NamesTheChange_NeverTheSecretValues()
    {
        using var admin = _factory.Admin();
        await PutAsync(admin, WithSecrets(), version: 0);

        var audit = await ApiJson.ReadAsync<ConfigAuditPage>(await admin.GetAsync(WatchdogRoutes.ConfigAudit, Ct));

        var entry = Assert.Single(audit.Items);
        Assert.Equal(TestRoles.AdminAccount, entry.ByAccount);
        Assert.Contains("secrets replaced", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("tajny", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("whsec_c2", entry.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_LoadsTheNewConfiguration_BeforeTheChangeEventIsStored()
    {
        // The stored change event wakes the rule evaluation job at once; it must already see the new configuration
        // (found in the live lab run: the event was planned with the old one, SMTP off, and nobody was notified).
        var probe = new ProviderProbe();
        await using var factory = new WatchdogFactory(services: s => s.AddSingleton<IConfigChangeNotificationPlanner>(probe));
        probe.Provider = factory.Services.GetRequiredService<IWatchdogConfigProvider>();
        using var admin = factory.Admin();

        var saved = await PutAsync(admin, WithSecrets(), version: 0);

        Assert.Equal(saved.Version, probe.VersionWhenPlanned);
        Assert.True(probe.SmtpEnabledWhenPlanned);
    }

    [Fact]
    public async Task Save_RecordsAConfigurationChangedEvent_AttributedToTheCaller()
    {
        using var admin = _factory.Admin();
        await PutAsync(admin, WithSecrets(), version: 0);

        var page = await _factory.Store.QueryAsync(new EventQuery { Kinds = [WatchEventKind.WatchdogConfigurationChanged] }, Ct);

        var watchEvent = Assert.Single(page.Items);
        Assert.Equal(TestRoles.AdminAccount, watchEvent.Attribution.Actor?.Account);
        Assert.Equal(DetectionSource.Health, watchEvent.Source);
        Assert.DoesNotContain(watchEvent.Changes.SelectMany(c => c.NewValues), v => v.Contains(Password, StringComparison.Ordinal));
        Assert.Contains(_factory.Log.Entries, e => e.EventId.Id == 1006 && e.Message.Contains(TestRoles.AdminAccount, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidConfiguration_Is400_WithIssues()
    {
        using var admin = _factory.Admin();
        var channel = new ChannelSettings { Id = "siem", Type = ChannelType.Webhook, Name = "SIEM" };

        using var response = await admin.PutAsync(
            WatchdogRoutes.Config,
            ApiJson.Content(new ConfigEnvelope { Version = 0, Configuration = new WatchdogConfiguration { Channels = [channel, channel] } }),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiProblem.Validation, (await ApiJson.ProblemAsync(response)).GetProperty("type").GetString());
        Assert.NotEmpty(await ApiJson.IssueCodesAsync(response));
    }

    [Fact]
    public async Task Validate_HasNoSideEffects()
    {
        using var admin = _factory.Admin();

        var result = await ApiJson.ReadAsync<ConfigValidationResult>(await admin.PostAsync(WatchdogRoutes.ConfigValidate, ApiJson.Content(WithSecrets()), Ct));

        Assert.True(result.IsValid);
        Assert.Empty(await _factory.Store.ListAsync(Ct));
        Assert.Empty((await _factory.Store.GetAuditAsync(null, 10, Ct)).Items);
    }

    [Fact]
    public async Task Defaults_AreAvailableToViewers()
    {
        using var viewer = _factory.Viewer();

        var defaults = await ApiJson.ReadAsync<WatchdogConfiguration>(await viewer.GetAsync(WatchdogRoutes.ConfigDefaults, Ct));

        Assert.Equal(WatchdogConfiguration.CurrentSchemaVersion, defaults.SchemaVersion);
    }

    [Fact]
    public async Task MalformedBody_Is400_WithoutEchoingTheInput()
    {
        using var admin = _factory.Admin();
        using var content = new StringContent("{\"version\": \"Zofia Żak\"", System.Text.Encoding.UTF8, "application/json");

        using var response = await admin.PutAsync(WatchdogRoutes.Config, content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("Zofia", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExcludingASubtreeWithPrivilegedObjects_NeedsTheBootstrapFlag()
    {
        // Tiered environments keep admin accounts and nested admin groups in their own OU – outside CN=Users and
        // CN=Builtin, which the static catalog protects. Excluding that OU through the API silenced their changes.
        var domainAdmins = Guid.NewGuid();
        var tier0Group = Guid.NewGuid();
        var admin = Guid.NewGuid();
        await _factory.Store.CommitAsync(new AdMng.Watchdog.Core.Model.DetectionCommit
        {
            Upserts =
            [
                new() { ObjectGuid = domainAdmins, Dn = "CN=Domain Admins,CN=Users,DC=contoso,DC=test", Kind = WatchObjectKind.Group },
                new() { ObjectGuid = tier0Group, Dn = "CN=Administratorzy Tier0,OU=Tier0,OU=Administracja,DC=contoso,DC=test", Kind = WatchObjectKind.Group },
                new() { ObjectGuid = admin, Dn = "CN=adm.łukasz,OU=Konta,OU=Administracja,DC=contoso,DC=test", Kind = WatchObjectKind.User },
            ],
            Privileges = new AdMng.Watchdog.Core.Model.PrivilegeState
            {
                Principals =
                [
                    new() { PrincipalKey = admin.ToString("N"), GroupGuid = domainAdmins, Tier = AdMng.Watchdog.Core.Model.PrivilegeTier.Critical, Path = [tier0Group] },
                ],
            },
        }, Ct);
        using var client = _factory.Admin();
        var saved = await PutAsync(client, TestData.Configuration(), version: 0);

        foreach (var subtree in new[] { "OU=Tier0,OU=Administracja,{domain}", "OU=Konta,OU=Administracja,DC=contoso,DC=test", "OU=Administracja,{domain}" })
        {
            var excluded = saved.Configuration with { Directory = saved.Configuration.Directory with { ExcludedSubtrees = [subtree] } };
            using var response = await client.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = saved.Version, Configuration = excluded }), Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("tier0Suppression", await ApiJson.IssueCodesAsync(response));
        }

        var unrelated = saved.Configuration with { Directory = saved.Configuration.Directory with { ExcludedSubtrees = ["OU=Testy,{domain}"] } };
        Assert.Equal(saved.Version + 1, (await PutAsync(client, unrelated, saved.Version)).Version);
    }

    [Fact]
    public async Task ExcludingASubtreeWithPrivilegedObjects_IsAllowedWithTheBootstrapFlag()
    {
        await using var factory = new WatchdogFactory(new Dictionary<string, string?> { ["Watchdog:Authorization:AllowTier0Suppression"] = "true" });
        var admin = Guid.NewGuid();
        var domainAdmins = Guid.NewGuid();
        await factory.Store.CommitAsync(new AdMng.Watchdog.Core.Model.DetectionCommit
        {
            Upserts = [new() { ObjectGuid = admin, Dn = "CN=adm.łukasz,OU=Konta,OU=Administracja,DC=contoso,DC=test", Kind = WatchObjectKind.User }],
            Privileges = new AdMng.Watchdog.Core.Model.PrivilegeState
            {
                Principals = [new() { PrincipalKey = admin.ToString("N"), GroupGuid = domainAdmins, Tier = AdMng.Watchdog.Core.Model.PrivilegeTier.Critical }],
            },
        }, Ct);
        using var client = factory.Admin();
        var saved = await PutAsync(client, TestData.Configuration(), version: 0);

        var excluded = saved.Configuration with { Directory = saved.Configuration.Directory with { ExcludedSubtrees = ["OU=Administracja,{domain}"] } };

        Assert.Equal(saved.Version + 1, (await PutAsync(client, excluded, saved.Version)).Version);
    }

    private static WatchdogConfiguration WithSecrets() => TestData.Configuration() with
    {
        Smtp = new SmtpSettings
        {
            Enabled = true,
            Host = "smtp.contoso.test",
            Port = 587,
            Authentication = SmtpAuthentication.Basic,
            UserName = "watchdog@contoso.test",
            Password = new SecretField { NewValue = Password },
            From = "AD Watchdog <watchdog@contoso.test>",
        },
        Channels =
        [
            new ChannelSettings
            {
                Id = "siem",
                Type = ChannelType.Webhook,
                Name = "SIEM – Centrum bezpieczeństwa",
                Url = new SecretField { NewValue = HookUrl },
                Webhook = new WebhookChannelOptions { SigningSecret = new SecretField { NewValue = SigningSecret } },
            },
        ],
    };

    private static async Task<ConfigEnvelope> PutAsync(HttpClient client, WatchdogConfiguration configuration, long version)
    {
        using var response = await client.PutAsync(WatchdogRoutes.Config, ApiJson.Content(new ConfigEnvelope { Version = version, Configuration = configuration }), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await ApiJson.ReadAsync<ConfigEnvelope>(response);
    }

    private sealed class ProviderProbe : IConfigChangeNotificationPlanner
    {
        public IWatchdogConfigProvider? Provider { get; set; }

        public long? VersionWhenPlanned { get; private set; }

        public bool SmtpEnabledWhenPlanned { get; private set; }

        public Task PlanWithPreviousConfigurationAsync(WatchEvent stored, WatchdogConfiguration previous, CancellationToken cancellationToken)
        {
            VersionWhenPlanned = Provider?.Current.Version;
            SmtpEnabledWhenPlanned = Provider?.Current.Configuration.Smtp.Enabled == true;
            return Task.CompletedTask;
        }
    }
}
