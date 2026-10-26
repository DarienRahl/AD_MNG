using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Rules;
using AdMng.Watchdog.Core.Rules.Validation;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Watchdog.Core.Tests.Contracts;

/// <summary>The contract of messages to users (README U1b): request / result / status JSON, no attachments, the
/// configuration section with its validation.</summary>
public sealed class UserMessageContractTests
{
    private static UserMessageRequest Request() => new()
    {
        Id = Guid.Parse("0192f3a4-5b6c-7d8e-9f00-112233445566"),
        From = new UserMessageAddressDto { Address = "helpdesk@contoso.test", DisplayName = "Dział IT – Pomoc" },
        To = [new UserMessageAddressDto { Address = "zofia.zak@contoso.test", DisplayName = "Zofia Żak" }],
        Cc = [new UserMessageAddressDto { Address = "jan.kowalski@contoso.test" }],
        Subject = "Twoje hasło wygasa 5 listopada 2026",
        TextBody = "Dzień dobry, Zofio!\nZmień hasło przed 5 listopada.",
        HtmlBody = "<p>Dzień dobry, Zofio!</p>",
        Language = "pl",
    };

    [Fact]
    public void Request_RoundTrips_InCamelCase_AndNeverPrintsItsContent()
    {
        var json = JsonSerializer.Serialize(Request(), WatchdogJson.Options);
        var back = JsonSerializer.Deserialize<UserMessageRequest>(json, WatchdogJson.Options)!;

        Assert.Contains("\"textBody\":", json, StringComparison.Ordinal);
        Assert.Contains("\"displayName\":", json, StringComparison.Ordinal);
        Assert.Equal("Zofia Żak", back.To[0].DisplayName);
        Assert.Equal(JsonSerializer.Serialize(back, WatchdogJson.Options), json);
        Assert.Equal("zofia.zak@contoso.test", back.To.Single().Address);
        Assert.DoesNotContain("Zofi", back.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", back.To[0].ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("attachments", "[{\"name\":\"faktura.exe\",\"content\":\"TVqQ\"}]")]
    [InlineData("bcc", "[{\"address\":\"ukryty@contoso.test\"}]")]
    public void Request_WithAnUnknownMember_IsRejected(string member, string value)
    {
        var json = JsonSerializer.Serialize(Request(), WatchdogJson.Options);
        var tampered = json[..^1] + $",\"{member}\":{value}}}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UserMessageRequest>(tampered, WatchdogJson.Options));
    }

    [Fact]
    public void Request_WithMissingMembers_KeepsTheDefaults()
    {
        var back = JsonSerializer.Deserialize<UserMessageRequest>("{\"id\":\"0192f3a4-5b6c-7d8e-9f00-112233445566\"}", WatchdogJson.Options)!;

        Assert.Empty(back.To);
        Assert.Empty(back.Cc);
        Assert.Equal(string.Empty, back.Subject);
        Assert.Equal("pl", back.Language);
    }

    [Fact]
    public void ResultAndStatus_RoundTrip_WithStatesAsStrings()
    {
        var result = new UserMessageResult
        {
            Id = Request().Id,
            State = UserMessageState.Queued,
            Code = "smtp.451.4.7.1",
            SmtpStatus = 451,
            OutboxId = 42,
        };
        var status = new UserMessagingStatusDto { Enabled = true, SmtpConfigured = true, Channel = "smtp.contoso.test:587", MaxMessagesPerHour = 300 };

        var resultJson = JsonSerializer.Serialize(result, WatchdogJson.Options);
        var statusJson = JsonSerializer.Serialize(status, WatchdogJson.Options);

        Assert.Contains("\"state\":\"Queued\"", resultJson, StringComparison.Ordinal);
        Assert.Equal(result, JsonSerializer.Deserialize<UserMessageResult>(resultJson, WatchdogJson.Options)! with { RejectedCopies = result.RejectedCopies });
        var statusBack = JsonSerializer.Deserialize<UserMessagingStatusDto>(statusJson, WatchdogJson.Options)!;
        Assert.True(statusBack.Available);
        Assert.Equal((300, UserMessageLimits.MaxRecipients), (statusBack.MaxMessagesPerHour, statusBack.MaxRecipients));
        Assert.False(new UserMessagingStatusDto { Enabled = true }.Available);
    }

    [Fact]
    public void Configuration_UserMessages_AreOffByDefault_AndSurviveARoundTrip()
    {
        var configuration = new WatchdogConfiguration();
        var json = JsonSerializer.Serialize(configuration with
        {
            UserMessages = new UserMessagingSettings { Enabled = true, AllowedSenders = ["@contoso.test"] },
        }, WatchdogJson.Options);
        var back = JsonSerializer.Deserialize<WatchdogConfiguration>(json, WatchdogJson.Options)!;
        var old = JsonSerializer.Deserialize<WatchdogConfiguration>("{\"schemaVersion\":2}", WatchdogJson.Options)!;

        Assert.False(configuration.UserMessages.Enabled);
        Assert.Equal((300, 2000, 365), (configuration.UserMessages.MaxMessagesPerHour, configuration.UserMessages.MaxMessagesPerDay, configuration.Retention.UserMessageAuditDays));
        Assert.Equal(["@contoso.test"], back.UserMessages.AllowedSenders);
        Assert.False(old.UserMessages.Enabled);
        Assert.Equal(DeliveryKind.UserMessage, Enum.Parse<DeliveryKind>("UserMessage"));
    }

    [Fact]
    public void Validation_ChecksTheLimitsAndTheAllowedSenders()
    {
        var validator = new ConfigurationValidator(TimeProvider.System);
        var configuration = SampleConfiguration.Create() with
        {
            UserMessages = new UserMessagingSettings
            {
                Enabled = true,
                MaxMessagesPerHour = 0,
                MaxMessagesPerDay = 200_000,
                AllowedSenders = ["helpdesk@contoso.test", "@contoso.test", "Dział IT <it@contoso.test>", "contoso.test", " "],
            },
            Retention = new RetentionSettings { UserMessageAuditDays = 0 },
        };

        var issues = validator.Validate(configuration, new AdMng.Watchdog.Core.Abstractions.ConfigValidationContext()).Issues;

        Assert.Contains(issues, i => i.Path == "userMessages.maxMessagesPerHour" && i.Code == ConfigValidationCodes.Range);
        Assert.Contains(issues, i => i.Path == "userMessages.maxMessagesPerDay" && i.Code == ConfigValidationCodes.Range);
        Assert.Contains(issues, i => i.Path == "retention.userMessageAuditDays" && i.Code == ConfigValidationCodes.Range);
        Assert.Equal(
            ["userMessages.allowedSenders[2]", "userMessages.allowedSenders[3]", "userMessages.allowedSenders[4]"],
            issues.Where(i => i.Path.StartsWith("userMessages.allowedSenders", StringComparison.Ordinal)).Select(i => i.Path).Order());
        Assert.True(UserMessagingValidator.IsSenderPattern("@poczta.contoso.test"));
        Assert.False(UserMessagingValidator.IsSenderPattern("@"));
    }
}
