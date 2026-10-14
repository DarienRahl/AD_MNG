using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Configuration;

namespace AdMng.Watchdog.Core.Tests.Configuration;

public class SecretBindingsTests
{
    private const string TeamsUrl =
        "https://Default72F988BF86F141AF91AB2D7CD011DB.47.Environment.Api.PowerPlatform.com:443/powerautomate/automations/direct/workflows/"
        + "0b1c2d3e4f5a/triggers/manual/paths/invoke?api-version=1&sp=%2Ftriggers%2Fmanual%2Frun&sv=1.0&sig=TajnyPodpisŻółć123";

    [Fact]
    public void SmtpBindings_FollowTheSection18Table()
    {
        var smtp = SampleConfiguration.Create().Smtp with { Host = "SMTP.Contoso.TEST.", UserName = "Svc-WD@Contoso.test" };

        Assert.Equal("smtp|smtp.contoso.test|587|svc-wd@contoso.test|basic", SecretBindings.ForSmtpPassword(smtp));
        Assert.Equal(
            "smtp-oauth|public|72f988bf-86f1-41af-91ab-2d7cd011db47|0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0|smtp.contoso.test",
            SecretBindings.ForSmtpOAuthClientSecret(smtp));
    }

    [Fact]
    public void Guids_AreNormalized_AndCloudsAreLowerCase()
    {
        var smtp = new SmtpSettings
        {
            Host = "smtp.office365.us",
            OAuth = new SmtpOAuthSettings
            {
                Cloud = EntraCloud.UsGovernment,
                TenantId = "{72F988BF-86F1-41AF-91AB-2D7CD011DB47}",
                ClientId = "0F1E2D3C4B5A69788796A5B4C3D2E1F0",
            },
        };

        Assert.Equal(
            "smtp-oauth|usgovernment|72f988bf-86f1-41af-91ab-2d7cd011db47|0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0|smtp.office365.us",
            SecretBindings.ForSmtpOAuthClientSecret(smtp));
    }

    [Fact]
    public void ChannelBindings_FollowTheSection18Table_AndContainOnlyTheHostOfTheUrl()
    {
        var configuration = SampleConfiguration.Create();
        var teams = configuration.Channels[0];
        var siem = configuration.Channels[1];
        var teamsHost = SecretBindings.UrlHost(TeamsUrl);

        Assert.Equal("channel|teams-it|teams", SecretBindings.ForChannelUrl(teams));
        Assert.Equal("channel|siem|webhook", SecretBindings.ForChannelUrl(siem));
        Assert.Equal(
            "teams-oauth|public|72f988bf-86f1-41af-91ab-2d7cd011db47|0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0|"
            + "https://service.flow.microsoft.com//.default|default72f988bf86f141af91ab2d7cd011db.47.environment.api.powerplatform.com",
            SecretBindings.ForTeamsClientSecret(teams, teamsHost));
        Assert.Equal("webhook|siem|siem.contoso.test", SecretBindings.ForWebhookSigningSecret(siem, "Siem.Contoso.Test"));

        var binding = SecretBindings.ForTeamsClientSecret(teams, teamsHost);
        Assert.DoesNotContain("sig", binding, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tajny", binding, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workflows", binding, StringComparison.Ordinal);
    }

    [Fact]
    public void Separators_InsideValues_AreEscaped_SoDifferentSettingsNeverCollide()
    {
        var a = new SmtpSettings { Host = "relay.contoso.test", Port = 25, UserName = "jan|25" };
        var b = new SmtpSettings { Host = "relay.contoso.test|25|jan", Port = 25, UserName = string.Empty };

        Assert.Equal("smtp|relay.contoso.test|25|jan%7c25|basic", SecretBindings.ForSmtpPassword(a));
        Assert.NotEqual(SecretBindings.ForSmtpPassword(a), SecretBindings.ForSmtpPassword(b));
        Assert.Equal("smtp|relay.contoso.test|25|50%25|basic", SecretBindings.ForSmtpPassword(a with { UserName = "50%" }));
    }

    [Theory]
    [InlineData("SMTP.Zażółć.Example.", "smtp.xn--za-6ja4f8n1l.example")]
    [InlineData("smtp.xn--za-6ja4f8n1l.example", "smtp.xn--za-6ja4f8n1l.example")]
    [InlineData("ｓｍｔｐ.contoso.test", "smtp.contoso.test")]   // full-width letters map to ASCII (IDNA)
    [InlineData("smtp.contoso.test。", "smtp.contoso.test")]               // ideographic full stop = trailing dot
    public void HostsGivenAsText_AreCanonicalIdna_LikeUrlHosts(string host, string expected)
    {
        // Section 18 / 25.6: hosts in IDNA form without a trailing dot – also the SMTP host and an explicit URL host,
        // so the same destination always yields the same binding (and a Unicode spelling cannot dodge a comparison).
        var smtp = new SmtpSettings { Host = host, Port = 587, UserName = "svc-wd" };
        var siem = SampleConfiguration.Create().Channels[1];

        Assert.Equal($"smtp|{expected}|587|svc-wd|basic", SecretBindings.ForSmtpPassword(smtp));
        Assert.EndsWith("|" + expected, SecretBindings.ForSmtpOAuthClientSecret(smtp), StringComparison.Ordinal);
        Assert.Equal($"webhook|siem|{expected}", SecretBindings.ForWebhookSigningSecret(siem, host));
        Assert.Equal(SecretBindings.UrlHost($"https://{host}/hooks"), expected);
    }

    [Fact]
    public void HostsThatAreNotValidIdna_StillNeverCollideWithAValidHost()
    {
        // GetAscii rejects an empty label; the host is kept as written (escaped), which never equals an IDNA host.
        var broken = SecretBindings.ForSmtpPassword(new SmtpSettings { Host = "smtp..zażółć.example", UserName = "u" });
        var valid = SecretBindings.ForSmtpPassword(new SmtpSettings { Host = "smtp.zażółć.example", UserName = "u" });

        Assert.NotEqual(valid, broken);
        Assert.StartsWith("smtp|smtp..zażółć.example|", broken, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://Siem.Contoso.TEST./hooks/zdarzenia?sig=tajne", "siem.contoso.test")]
    [InlineData("https://siem.contoso.test:8443/hooks/…", "siem.contoso.test")]
    [InlineData("https://zażółć.example/hooks", "xn--za-6ja4f8n1l.example")]
    [InlineData("  https://192.168.10.5/x  ", "192.168.10.5")]
    public void UrlHost_IsLowerCaseIdna_WithoutTrailingDot(string url, string expected)
    {
        Assert.Equal(expected, SecretBindings.UrlHost(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("siem.contoso.test/hooks")]
    [InlineData("not a url")]
    public void UrlHost_IsNull_ForAnythingButAnAbsoluteUrl(string? url)
    {
        Assert.Null(SecretBindings.UrlHost(url));
    }

    [Fact]
    public void DeclaredUrlHost_PrefersTheNewValue_OverTheHint()
    {
        var siem = SampleConfiguration.Create().Channels[1];

        Assert.Equal("siem.contoso.test", SecretBindings.DeclaredUrlHost(siem));
        Assert.Equal("siem2.contoso.test", SecretBindings.DeclaredUrlHost(siem with { Url = siem.Url with { NewValue = "https://siem2.contoso.test/x" } }));
        Assert.Null(SecretBindings.DeclaredUrlHost(siem with { Url = new SecretField { IsSet = true } }));
    }

    [Fact]
    public void Enumerate_ListsEverySecretField_WithNamePathAndUsage()
    {
        var secrets = SecretBindings.Enumerate(SampleConfiguration.Create());

        Assert.Equal(
            new[]
            {
                ("smtp.password", "smtp.password", true),
                ("smtp.oauth.clientSecret", "smtp.oAuth.clientSecret", false),
                ("channel.teams-it.url", "channels[0].url", true),
                ("channel.teams-it.clientSecret", "channels[0].teams.clientSecret", true),
                ("channel.siem.url", "channels[1].url", true),
                ("channel.siem.signingSecret", "channels[1].webhook.signingSecret", true),
                ("channel.siem.previousSigningSecret", "channels[1].webhook.previousSigningSecret", true),
            },
            secrets.Select(s => (s.Name, s.Path, s.InUse)).ToArray());
        Assert.Equal("webhook|siem|siem.contoso.test", secrets.Single(s => s.Name == "channel.siem.signingSecret").Binding);
        Assert.Equal("siem", secrets.Single(s => s.Name == "channel.siem.url").ChannelId);
        Assert.Null(secrets[0].ChannelId);
        Assert.DoesNotContain("smtp.contoso", secrets[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Enumerate_UsesTheGivenUrlHostResolver()
    {
        var secrets = SecretBindings.Enumerate(SampleConfiguration.Create(), channel => channel.Id == "siem" ? "stored.contoso.test" : null);

        Assert.Equal("webhook|siem|stored.contoso.test", secrets.Single(s => s.Name == "channel.siem.signingSecret").Binding);
        Assert.EndsWith("|", secrets.Single(s => s.Name == "channel.teams-it.clientSecret").Binding, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresRebind_WhenTheSmtpHostChanges_WithoutANewPassword()
    {
        var configuration = SampleConfiguration.Create();
        var stored = Stored(configuration);
        var moved = configuration with { Smtp = configuration.Smtp with { Host = "smtp.atakujący.example" } };

        var password = Find(moved, SecretNames.SmtpPassword);
        Assert.True(SecretBindings.RequiresRebind(password, stored));

        var reentered = moved with { Smtp = moved.Smtp with { Password = new SecretField { IsSet = true, NewValue = "Nowe!Hasło2026" } } };
        Assert.False(SecretBindings.RequiresRebind(Find(reentered, SecretNames.SmtpPassword), stored));

        var cleared = moved with { Smtp = moved.Smtp with { Password = new SecretField { Clear = true } } };
        Assert.False(SecretBindings.RequiresRebind(Find(cleared, SecretNames.SmtpPassword), stored));

        var anonymous = moved with { Smtp = moved.Smtp with { Authentication = SmtpAuthentication.None } };
        Assert.False(SecretBindings.RequiresRebind(Find(anonymous, SecretNames.SmtpPassword), stored));
    }

    [Theory]
    [InlineData("port")]
    [InlineData("user")]
    public void RequiresRebind_ForEveryBoundSmtpField(string field)
    {
        var configuration = SampleConfiguration.Create();
        var stored = Stored(configuration);
        var smtp = field == "port" ? configuration.Smtp with { Port = 2525 } : configuration.Smtp with { UserName = "ktoś.inny@contoso.test" };

        Assert.True(SecretBindings.RequiresRebind(Find(configuration with { Smtp = smtp }, SecretNames.SmtpPassword), stored));
    }

    [Fact]
    public void RequiresRebind_IsFalse_WhenNothingChangedOrNothingIsStored()
    {
        var configuration = SampleConfiguration.Create();
        var stored = Stored(configuration);

        Assert.All(SecretBindings.Enumerate(configuration), s => Assert.False(SecretBindings.RequiresRebind(s, stored)));
        Assert.All(SecretBindings.Enumerate(configuration), s => Assert.False(SecretBindings.RequiresRebind(s, new Dictionary<string, string>())));
    }

    [Fact]
    public void RequiresRebind_ForTheSigningSecret_WhenTheWebhookUrlMovesToAnotherHost()
    {
        var configuration = SampleConfiguration.Create();
        var stored = Stored(configuration);
        var siem = configuration.Channels[1];
        var moved = configuration with
        {
            Channels = [configuration.Channels[0], siem with { Url = new SecretField { IsSet = true, NewValue = "https://odbiornik.atakujący.example/x" } }],
        };

        Assert.False(SecretBindings.RequiresRebind(Find(moved, "channel.siem.url"), stored));
        Assert.True(SecretBindings.RequiresRebind(Find(moved, "channel.siem.signingSecret"), stored));
    }

    [Fact]
    public void RequiresRebind_ForTheTeamsClientSecret_WhenTenantScopeOrHostChange_AndFailsClosedWithoutAHost()
    {
        var configuration = SampleConfiguration.Create();
        var stored = Stored(configuration);
        var teams = configuration.Channels[0];

        WatchdogConfiguration With(ChannelSettings channel) => configuration with { Channels = [channel, configuration.Channels[1]] };

        Assert.True(SecretBindings.RequiresRebind(
            Find(With(teams with { Teams = teams.Teams! with { TenantId = "11111111-2222-3333-4444-555555555555" } }), "channel.teams-it.clientSecret"), stored));
        Assert.True(SecretBindings.RequiresRebind(
            Find(With(teams with { Teams = teams.Teams! with { Scope = "https://service.flow.microsoft.com/.default" } }), "channel.teams-it.clientSecret"), stored));
        Assert.True(SecretBindings.RequiresRebind(
            Find(With(teams with { Url = new SecretField { IsSet = true, NewValue = "https://evil.example/powerautomate" } }), "channel.teams-it.clientSecret"), stored));
        Assert.True(SecretBindings.RequiresRebind(
            Find(With(teams with { Url = new SecretField { IsSet = true } }), "channel.teams-it.clientSecret"), stored));
        Assert.False(SecretBindings.RequiresRebind(
            Find(With(teams with { Teams = teams.Teams! with { AuthMode = TeamsAuthMode.Sas, TenantId = null } }), "channel.teams-it.clientSecret"), stored));
    }

    [Theory]
    [InlineData("smtp.oauth.clientSecret", "cloud")]
    [InlineData("smtp.oauth.clientSecret", "tenant")]
    [InlineData("smtp.oauth.clientSecret", "client")]
    [InlineData("smtp.oauth.clientSecret", "host")]
    [InlineData("channel.teams-it.clientSecret", "cloud")]
    [InlineData("channel.teams-it.clientSecret", "client")]
    public void RequiresRebind_ForEveryBoundOAuthField(string secret, string field)
    {
        // Section 18 table: smtp-oauth|<cloud>|<tenantId>|<clientId>|<host>, teams-oauth|<cloud>|<tenantId>|<clientId>|<scope>|<url host>.
        var configuration = SampleConfiguration.Create() with
        {
            Smtp = SampleConfiguration.Create().Smtp with { Authentication = SmtpAuthentication.OAuth2ClientCredentials },
        };
        var stored = Stored(configuration);
        const string Other = "99999999-8888-7777-6666-555555555555";
        var oauth = configuration.Smtp.OAuth;
        var teams = configuration.Channels[0];
        var changed = (secret, field) switch
        {
            (SecretNames.SmtpOAuthClientSecret, "cloud") => configuration with { Smtp = configuration.Smtp with { OAuth = oauth with { Cloud = EntraCloud.China } } },
            (SecretNames.SmtpOAuthClientSecret, "tenant") => configuration with { Smtp = configuration.Smtp with { OAuth = oauth with { TenantId = Other } } },
            (SecretNames.SmtpOAuthClientSecret, "client") => configuration with { Smtp = configuration.Smtp with { OAuth = oauth with { ClientId = Other } } },
            (SecretNames.SmtpOAuthClientSecret, _) => configuration with { Smtp = configuration.Smtp with { Host = "smtp.office365.com.atakujący.example" } },
            (_, "cloud") => configuration with { Channels = [teams with { Teams = teams.Teams! with { Cloud = EntraCloud.UsGovernment } }, configuration.Channels[1]] },
            _ => configuration with { Channels = [teams with { Teams = teams.Teams! with { ClientId = Other } }, configuration.Channels[1]] },
        };

        Assert.True(SecretBindings.RequiresRebind(Find(changed, secret), stored));
    }

    [Theory]
    [InlineData("https://siem.contoso.test@odbiornik.atakujący.example/hooks")]          // user info: the host is the part after '@'
    [InlineData("https://siem.contoso.test.atakujący.example/hooks")]                   // suffix trick
    [InlineData("https://atakującysiem.contoso.test.example/hooks")]
    [InlineData("https://ѕiem.contoso.test/hooks")]                                // Cyrillic 'ѕ' (homograph → punycode)
    [InlineData("https://siem.contoso.test%2eatakujący.example/hooks")]                // not parseable → no host (fail closed)
    public void RequiresRebind_ForTheSigningSecret_WhenAUrlTrickPointsElsewhere(string url)
    {
        var configuration = SampleConfiguration.Create();
        var stored = Stored(configuration);
        var siem = configuration.Channels[1];
        var moved = configuration with { Channels = [configuration.Channels[0], siem with { Url = new SecretField { IsSet = true, NewValue = url } }] };

        Assert.True(SecretBindings.RequiresRebind(Find(moved, "channel.siem.signingSecret"), stored));
        Assert.True(SecretBindings.RequiresRebind(Find(moved, "channel.siem.previousSigningSecret"), Stored(configuration with
        {
            Channels = [configuration.Channels[0], siem with { Webhook = siem.Webhook! with { PreviousSigningSecret = new SecretField { IsSet = true } } }],
        })));
    }

    [Theory]
    [InlineData("https://SIEM.Contoso.Test./inna/ścieżka?sig=nowy")]
    [InlineData("https://siem.contoso.test:8443/hooks")]                                // the binding is the host (section 18)
    [InlineData("https://ｓiem.contoso.test/hooks")]                                // full-width 's' is the same IDNA host
    public void RequiresRebind_IsFalse_WhenTheUrlStaysOnTheSameHost(string url)
    {
        var configuration = SampleConfiguration.Create();
        var stored = Stored(configuration);
        var siem = configuration.Channels[1];
        var same = configuration with { Channels = [configuration.Channels[0], siem with { Url = new SecretField { IsSet = true, NewValue = url } }] };

        Assert.False(SecretBindings.RequiresRebind(Find(same, "channel.siem.signingSecret"), stored));
    }

    [Fact]
    public void RequiresRebind_ForTheChannelUrl_WhenTheChannelTypeChanges()
    {
        var configuration = SampleConfiguration.Create();
        var stored = Stored(configuration);
        var retyped = configuration with { Channels = [configuration.Channels[0], configuration.Channels[1] with { Type = ChannelType.Teams }] };

        Assert.True(SecretBindings.RequiresRebind(Find(retyped, "channel.siem.url"), stored));
    }

    private static ConfiguredSecret Find(WatchdogConfiguration configuration, string name) =>
        SecretBindings.Enumerate(configuration).Single(s => s.Name == name);

    private static Dictionary<string, string> Stored(WatchdogConfiguration configuration) =>
        SecretBindings.Enumerate(configuration).ToDictionary(s => s.Name, s => s.Binding);
}
