using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Configuration;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Api;
using AdMng.Watchdog.Service.Secrets;

namespace AdMng.Watchdog.Service.Tests.Api;

/// <summary>Secret fields → secret changes with the bindings of the new settings (spec 17.2 step 3, 18).</summary>
public sealed class ConfigSecretsApplierTests
{
    private const string Url = "https://contoso.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/abc/triggers/manual/paths/invoke?sig=TAJNE";

    [Fact]
    public void NewValues_AreStoredWithTheNewBindings_AndStripped()
    {
        var configuration = Configuration(url: new SecretField { NewValue = Url }, password: new SecretField { NewValue = "Hasło-1!" });
        var hosts = new Dictionary<string, string?> { ["teams-pomoc"] = SecretBindings.UrlHost(Url) };

        var result = ConfigSecretsApplier.Apply(configuration, new WatchdogConfiguration(), new Dictionary<string, string>(), hosts);

        var password = Assert.Single(result.Changes, c => c.Name == SecretNames.SmtpPassword);
        Assert.Equal("Hasło-1!", password.Value?.Reveal());
        Assert.Equal(SecretBindings.ForSmtpPassword(configuration.Smtp), password.Binding);
        var url = Assert.Single(result.Changes, c => c.Name == SecretNames.ChannelUrl("teams-pomoc"));
        Assert.Equal("channel|teams-pomoc|teams", url.Binding);
        Assert.True(result.Configuration.Smtp.Password.IsSet);
        Assert.Null(result.Configuration.Smtp.Password.NewValue);
        Assert.Equal("https://contoso.environment.api.powerplatform.com/…", result.Configuration.Channels[0].Url.Hint);
        Assert.DoesNotContain("TAJNE", result.Configuration.Channels[0].Url.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("Hasło", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnchangedSecrets_KeepIsSetAndHint_FromTheStore()
    {
        var previous = Configuration(url: new SecretField { IsSet = true, Hint = "https://contoso.environment.api.powerplatform.com/powerautomate/…" }, password: new SecretField { IsSet = true });
        var incoming = Configuration(url: new SecretField { IsSet = false, Hint = "https://attacker.example/…" }, password: new SecretField());
        var stored = new Dictionary<string, string>
        {
            [SecretNames.SmtpPassword] = SecretBindings.ForSmtpPassword(previous.Smtp),
            [SecretNames.ChannelUrl("teams-pomoc")] = "channel|teams-pomoc|teams",
        };

        var result = ConfigSecretsApplier.Apply(incoming, previous, stored, new Dictionary<string, string?>());

        Assert.Empty(result.Changes);
        Assert.True(result.Configuration.Smtp.Password.IsSet);
        Assert.True(result.Configuration.Channels[0].Url.IsSet);
        Assert.Equal("https://contoso.environment.api.powerplatform.com/…", result.Configuration.Channels[0].Url.Hint);   // the stored hint, in the safe form
    }

    [Fact]
    public void Clear_AndOrphans_AreRemoved()
    {
        var incoming = Configuration(url: new SecretField { IsSet = true }, password: new SecretField { Clear = true });
        var stored = new Dictionary<string, string>
        {
            [SecretNames.SmtpPassword] = "smtp|x|587|y|basic",
            [SecretNames.ChannelUrl("teams-pomoc")] = "channel|teams-pomoc|teams",
            [SecretNames.ChannelUrl("usuniety")] = "channel|usuniety|webhook",
            [SecretNames.WebhookSigningSecret("usuniety")] = "webhook|usuniety|siem.contoso.test",
        };

        var result = ConfigSecretsApplier.Apply(incoming, incoming, stored, new Dictionary<string, string?>());

        Assert.Equal(
            new[] { SecretNames.SmtpPassword, SecretNames.ChannelUrl("usuniety"), SecretNames.WebhookSigningSecret("usuniety") }.Order(StringComparer.Ordinal),
            result.Changes.Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.All(result.Changes, c => Assert.Null(c.Value));
        Assert.False(result.Configuration.Smtp.Password.IsSet);
    }

    [Fact]
    public void RebindIssues_UseTheRealUrlHost_NotTheClientHint()
    {
        var channel = new ChannelSettings
        {
            Id = "siem",
            Type = ChannelType.Webhook,
            Name = "SIEM",
            Url = new SecretField { IsSet = true, Hint = "https://siem.contoso.test/hooks/…" },
            Webhook = new WebhookChannelOptions { SigningSecret = new SecretField { IsSet = true } },
        };
        var configuration = new WatchdogConfiguration { Channels = [channel] };
        var stored = new Dictionary<string, string> { [SecretNames.WebhookSigningSecret("siem")] = "webhook|siem|siem.contoso.test" };

        var honest = ConfigSecretsApplier.RebindIssues(configuration, stored, new Dictionary<string, string?> { ["siem"] = "siem.contoso.test" });
        var redirected = ConfigSecretsApplier.RebindIssues(configuration, stored, new Dictionary<string, string?> { ["siem"] = "attacker.example" });

        Assert.Empty(honest);
        Assert.Equal("channels[0].webhook.signingSecret", Assert.Single(redirected).Path);
    }

    [Theory]
    [InlineData("channel.siem.url", "https://siem.contoso.test:8443/hooks/abc?token=x", "https://siem.contoso.test:8443/…")]
    [InlineData("channel.siem.url", "https://siem.contoso.test", "https://siem.contoso.test")]
    [InlineData("channel.siem.signingSecret", "whsec_MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIz", "whsec_…MTIz")]
    [InlineData("channel.siem.previousSigningSecret", "whsec_MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIz", "whsec_…MTIz")]
    [InlineData("smtp.password", "Hasło-1!", null)]
    [InlineData("channel.teams.clientSecret", "sekret", null)]
    public void Hints(string name, string value, string? hint)
    {
        Assert.Equal(hint, ConfigSecretsApplier.Hint(name, value));
    }

    /// <summary>Services that put the credential in the first path segment: the hint never contains a path segment, query
    /// or user info (spec 18: channel URLs only through ISecretStore). The host stays – the validator reads it.</summary>
    [Theory]
    [InlineData("https://webhook.site/0f8fad5b-d9cb-469f-a165-70867728950e", "https://webhook.site/…", "0f8fad5b")]
    [InlineData("https://ntfy.sh/alarmy-łódź-kadry-7f3k", "https://ntfy.sh/…", "alarmy")]
    [InlineData("https://jan:Hasło1@siem.contoso.test/hooks", "https://siem.contoso.test/…", "Hasło1")]
    [InlineData("https://siem.contoso.test/?token=tajny-żeton", "https://siem.contoso.test/…", "tajny")]
    public void UrlHints_NeverRevealTheSecretPart(string url, string hint, string secretPart)
    {
        var actual = ConfigSecretsApplier.Hint(SecretNames.ChannelUrl("siem"), url);

        Assert.Equal(hint, actual);
        Assert.NotEqual(url, actual);
        Assert.DoesNotContain(secretPart, actual, StringComparison.Ordinal);
        Assert.Equal(actual, SecretHints.Url(actual));   // idempotent
    }

    /// <summary>A hint stored before the rule (the whole secret URL) is repaired at the next save of the unchanged secret.</summary>
    [Fact]
    public void UnchangedSecrets_WithALeakyStoredHint_GetASafeHint()
    {
        var leaky = new SecretField { IsSet = true, Hint = "https://webhook.site/0f8fad5b-d9cb-469f-a165-70867728950e" };
        var previous = Configuration(url: leaky, password: new SecretField());
        var stored = new Dictionary<string, string> { [SecretNames.ChannelUrl("teams-pomoc")] = "channel|teams-pomoc|teams" };

        var result = ConfigSecretsApplier.Apply(previous, previous, stored, new Dictionary<string, string?>());

        Assert.Equal("https://webhook.site/…", result.Configuration.Channels[0].Url.Hint);
    }

    [Fact]
    public void AuditSummary_NamesSecrets_NeverValues()
    {
        var audit = new ConfigChangeAudit { Severity = Contracts.Events.Severity.High, Summary = "smtp.host changed", Items = [new ConfigChangeItem { Path = "smtp.host", Kind = ConfigChangeKind.Modified, Severity = Contracts.Events.Severity.High }] };

        var summary = ConfigAuditSummarizer.Summarize(audit, [new SecretChange(SecretNames.SmtpPassword, new SecretValue("Hasło-1!"), "b"), new SecretChange(SecretNames.ChannelUrl("x"), null, null)]);

        Assert.Equal("smtp.host changed; secrets replaced: smtp.password; secrets removed: channel.x.url", summary);
    }

    private static WatchdogConfiguration Configuration(SecretField url, SecretField password) => new()
    {
        Smtp = new SmtpSettings
        {
            Host = "smtp.contoso.test",
            Authentication = SmtpAuthentication.Basic,
            UserName = "watchdog@contoso.test",
            Password = password,
        },
        Channels = [new ChannelSettings { Id = "teams-pomoc", Type = ChannelType.Teams, Name = "Teams", Url = url }],
    };
}
