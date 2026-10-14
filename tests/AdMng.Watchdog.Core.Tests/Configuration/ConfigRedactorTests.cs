using System.Text.Encodings.Web;
using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Configuration;

namespace AdMng.Watchdog.Core.Tests.Configuration;

public class ConfigRedactorTests
{
    private static readonly ConfigEnvelope Envelope = new()
    {
        Version = 42,
        Configuration = SampleConfiguration.Create(),
        UpdatedAt = SampleConfiguration.Created,
        UpdatedBy = "CONTOSO\\adm.jkowalska",
    };

    [Fact]
    public void ViewerEnvelope_IsMarkedRedacted_AndKeepsVersionMetadata()
    {
        var redacted = new ConfigRedactor().Redact(Envelope);

        Assert.True(redacted.IsRedacted);
        Assert.Equal(42, redacted.Version);
        Assert.Equal(Envelope.UpdatedAt, redacted.UpdatedAt);
        Assert.Equal(Envelope.UpdatedBy, redacted.UpdatedBy);
    }

    [Fact]
    public void RecipientAddressesAndSmtpIdentity_AreRemoved()
    {
        var configuration = new ConfigRedactor().Redact(Envelope).Configuration;

        Assert.All(configuration.RecipientGroups, g => Assert.Empty(g.Addresses));
        Assert.Equal(["Administratorzy IT", "Helpdesk Łódź"], configuration.RecipientGroups.Select(g => g.Name));
        Assert.Null(configuration.Smtp.Host);
        Assert.Null(configuration.Smtp.UserName);
        Assert.Null(configuration.Smtp.From);
        Assert.Null(configuration.Smtp.ReplyTo);
        Assert.Null(configuration.Smtp.LocalDomain);
        Assert.Null(configuration.Smtp.OAuth.TenantId);
        Assert.Null(configuration.Smtp.OAuth.ClientId);
        Assert.Null(configuration.Smtp.OAuth.CertificateThumbprint);
        Assert.True(configuration.Smtp.Enabled);
        Assert.Equal(587, configuration.Smtp.Port);
    }

    [Fact]
    public void Secrets_KeepOnlyIsSet_AndChannelHintsAndOAuthIdsAreRemoved()
    {
        var configuration = new ConfigRedactor().Redact(Envelope).Configuration;
        var teams = configuration.Channels[0];
        var siem = configuration.Channels[1];

        Assert.Equal(new SecretField { IsSet = true }, configuration.Smtp.Password);
        Assert.Equal(new SecretField { IsSet = true }, configuration.Smtp.OAuth.ClientSecret);
        Assert.Equal(new SecretField { IsSet = true }, teams.Url);
        Assert.Equal(new SecretField { IsSet = true }, teams.Teams!.ClientSecret);
        Assert.Null(teams.Teams.TenantId);
        Assert.Null(teams.Teams.ClientId);
        Assert.Null(teams.Teams.CertificateThumbprint);
        Assert.Equal(new SecretField { IsSet = true }, siem.Url);
        Assert.Equal(new SecretField { IsSet = true }, siem.Webhook!.SigningSecret);
        Assert.Equal(new SecretField { IsSet = false }, siem.Webhook.PreviousSigningSecret);
        Assert.Equal("SIEM – Łódź", siem.Name);
    }

    [Fact]
    public void Exclusions_AreReplacedByPlaceholders_SoOnlyTheCountRemains()
    {
        var source = SampleConfiguration.Create();
        var lockouts = source.Rules[1];
        var twoExclusions = source with
        {
            Rules = [source.Rules[0], lockouts with { Exclusions = [lockouts.Exclusions[0], lockouts.Exclusions[0] with { Id = "drugi" }] }, source.Rules[2]],
        };

        var rules = new ConfigRedactor().Redact(twoExclusions).Rules;

        Assert.Empty(rules[0].Exclusions);
        Assert.Equal(["redacted-1", "redacted-2"], rules[1].Exclusions.Select(e => e.Id));
        Assert.All(rules[1].Exclusions, e =>
        {
            Assert.StartsWith(ConfigRedactor.RedactedExclusionIdPrefix, e.Id, StringComparison.Ordinal);
            Assert.Equal(string.Empty, e.Reason);
            Assert.Equal(string.Empty, e.CreatedBy);
            Assert.Equal(default, e.CreatedAt);
            Assert.Null(e.SubtreeDn);
            Assert.Null(e.Actor);
            Assert.Null(e.ObjectGuid);
            Assert.Null(e.Attribute);
            Assert.Null(e.ExpiresAt);
        });
        Assert.Equal(source.Rules.Select(r => r.Name), rules.Select(r => r.Name));
    }

    [Fact]
    public void SerializedViewerDocument_ContainsNoneOfTheRedactedValues()
    {
        // Relaxed encoder: Polish values must be searchable as written (the default encoder escapes non-ASCII).
        var json = JsonSerializer.Serialize(new ConfigRedactor().Redact(Envelope), new JsonSerializerOptions(WatchdogJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        string[] hidden =
        [
            "łukasz.wiśniewski", "zofia.żak", "helpdesk.łódź", "smtp.contoso.test", "svc-wd@", "nadzor@", "zespół.it",
            "admng-wd.contoso.test\"", SampleConfiguration.TenantId, SampleConfiguration.ClientId, "A1B2C3D4", "00112233",
            "powerplatform", "siem.contoso.test/hooks", "whsec_", "…abcd", "…wxyz",
            SampleConfiguration.ExclusionReason, SampleConfiguration.ExclusionActorSid, "OU=Serwery", "konto-testowe", "adm.jkowalska\",\"createdAt",
        ];
        Assert.All(hidden, value => Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"isRedacted\":true", json, StringComparison.Ordinal);
        Assert.Contains("https://admng-wd.contoso.test:8443", json, StringComparison.Ordinal);   // PublicBaseUrl stays (event links)
    }

    [Fact]
    public void Redact_DoesNotModifyTheInput()
    {
        var source = SampleConfiguration.Create();
        var before = JsonSerializer.Serialize(source, WatchdogJson.Options);

        _ = new ConfigRedactor().Redact(source);

        Assert.Equal(before, JsonSerializer.Serialize(source, WatchdogJson.Options));
        Assert.Equal(SampleConfiguration.AdminAddress, source.RecipientGroups[0].Addresses[0]);
    }

    [Fact]
    public void NullListEntries_DoNotBreakTheViewerResponse()
    {
        var broken = SampleConfiguration.Create() with { Channels = [null!], RecipientGroups = [null!], Rules = [null!] };

        var redacted = new ConfigRedactor().Redact(broken);

        Assert.Null(Assert.Single(redacted.Channels));
        Assert.Null(Assert.Single(redacted.RecipientGroups));
        Assert.Null(Assert.Single(redacted.Rules));
        Assert.Null(redacted.Smtp.Host);
    }

    [Fact]
    public void DefaultConfiguration_IsRedactedWithoutErrors()
    {
        var redacted = new ConfigRedactor().Redact(new ConfigEnvelope { Version = 1, Configuration = new() });

        Assert.True(redacted.IsRedacted);
        Assert.Empty(redacted.Configuration.Channels);
        Assert.False(redacted.Configuration.Smtp.Password.IsSet);
    }
}
