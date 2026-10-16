using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Formatting;
using AdMng.Watchdog.Core.Tests.Notifications.Fakes;

namespace AdMng.Watchdog.Core.Tests.Formatting;

public sealed class EventHtmlRendererTests
{
    [Fact]
    public void Page_IsACompleteDocument_WithTheAlertRelatedEventsEvidenceAndDeliveries()
    {
        var configuration = FormattingSamples.Configuration() with { Rules = [FormattingSamples.Rule()] };
        var renderer = new EventHtmlRenderer(new FakeConfigProvider(configuration));
        var details = new EventDetails
        {
            Event = FormattingSamples.PrivilegeGranted() with { AcknowledgedAt = FormattingSamples.Now, AcknowledgedBy = @"CONTOSO\łucja.nowak", AcknowledgeComment = "Sprawdzone" },
            RelatedEvents = [FormattingSamples.PrivilegeGranted(), FormattingSamples.UserCreated(1) with { ChangeSetId = Guid.NewGuid() }],
            Deliveries =
            [
                new EventDeliveryDto
                {
                    OutboxId = 7, ChannelType = DeliveryChannelType.Teams, ChannelId = "teams-it", ChannelName = "Teams – IT", Kind = DeliveryKind.Alert,
                    Status = OutboxStatus.Sent, CreatedAt = FormattingSamples.Now, SentAt = FormattingSamples.Now,
                },
            ],
        };

        var html = renderer.Render(details);

        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("<meta charset=\"utf-8\">", html, StringComparison.Ordinal);
        Assert.Contains("KRYTYCZNE · Dostęp uprzywilejowany", html, StringComparison.Ordinal);
        Assert.Contains("Powiązane zdarzenia", html, StringComparison.Ordinal);
        Assert.Contains("Zażółć Gęślą Jaźń 001", html, StringComparison.Ordinal);
        Assert.Contains("Dowody (dzienniki zabezpieczeń)", html, StringComparison.Ordinal);
        Assert.Contains("918273", html, StringComparison.Ordinal);
        Assert.Contains("Teams – IT", html, StringComparison.Ordinal);
        Assert.Contains("Wysłano", html, StringComparison.Ordinal);
        Assert.Contains("Potwierdzono 14.10.2026 14:32:05 (UTC+02:00) przez CONTOSO\\łucja.nowak", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Otwórz w przeglądarce", html, StringComparison.Ordinal);
        EmailAlertFormatterTests.AssertNoMissingKeys(html);
    }

    [Fact]
    public void ViewerDetailLevel_HidesPersonalValues()
    {
        var renderer = new EventHtmlRenderer(new FakeConfigProvider(FormattingSamples.Configuration("en")));
        var details = new EventDetails { Event = FormattingSamples.AttributesChanged() };

        var full = renderer.Render(details);
        var standard = renderer.Render(details, DetailLevel.Standard);

        Assert.Contains("+48 600 100 201", full, StringComparison.Ordinal);
        Assert.DoesNotContain("+48 600 100 201", standard, StringComparison.Ordinal);
        Assert.Contains("Department", standard, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", full, StringComparison.Ordinal);
    }
}
