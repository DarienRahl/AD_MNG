using System.Text;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting.AdaptiveCards;
using AdMng.Watchdog.Core.Notifications.Digest;

namespace AdMng.Watchdog.Core.Tests.Formatting.AdaptiveCards;

public sealed class TeamsDigestCardFormatterTests
{
    private const string Marker = "ZŁY_MARKER";

    [Fact]
    public void DigestCard_IsAValidAdaptiveCard14_WithAnEmphasisHeaderAndCounters()
    {
        var message = TeamsDigestCardFormatter.Format(DigestSamples.Full(), FormattingSamples.Context());

        var card = TeamsCardFormatterTests.AssertValidMessage(message);

        Assert.Equal("emphasis", card["body"]![0]!["style"]!.GetValue<string>());
        Assert.Equal("FactSet", card["body"]![1]!["type"]!.GetValue<string>());
        var text = TeamsCardFormatterTests.AllText(card);
        Assert.Contains("Raport dzienny", text, StringComparison.Ordinal);
        Assert.Contains("Wymaga uwagi (2)", text, StringComparison.Ordinal);
        Assert.Contains("Nowe konta (3)", text, StringComparison.Ordinal);
        Assert.Contains("Pokazano", text, StringComparison.Ordinal);
        Assert.StartsWith("AD Watchdog: [AD Watchdog] Raport dzienny contoso.com – 14.10.2026", card["fallbackText"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("https://admng-wd.contoso.com:8443/", Assert.Single(card["actions"]!.AsArray())!["url"]!.GetValue<string>());
        EmailAlertFormatterTests.AssertNoMissingKeys(Encoding.UTF8.GetString(message.Body));
    }

    [Fact]
    public void Attention_SaysWhenTheSeverityWasRaisedAfterTheAlert()
    {
        var model = DigestSamples.Full() with { SeverityChanges = new Dictionary<long, Severity> { [18350] = Severity.Medium } };

        var text = TeamsCardFormatterTests.AllText(TeamsCardFormatterTests.AssertValidMessage(TeamsDigestCardFormatter.Format(model, FormattingSamples.Context())));

        Assert.Contains("(priorytet: Średni → Wysoki)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectoryStrings_AreOnlyInTextRuns()
    {
        var created = FormattingSamples.UserCreated(1);
        created = created with { Object = created.Object with { DisplayName = Marker + " [Kliknij](https://evil.example/a)" } };
        var model = DigestSamples.Full() with
        {
            Domain = Marker + ".contoso.com",
            Health = DigestSamples.Full().Health with { DcHostName = Marker + "-dc01.contoso.com" },
            Sections = [new DigestSection { Kind = DigestSectionKind.NewAccounts, Total = 1, Rows = [new DigestEventRow(created)] }],
        };

        var card = TeamsCardFormatterTests.AssertValidMessage(TeamsDigestCardFormatter.Format(model, FormattingSamples.Context()));

        Assert.DoesNotContain(Marker, string.Join("\n", TeamsCardFormatterTests.MarkdownTexts(card)), StringComparison.Ordinal);
        Assert.Contains(Marker + " [Kliknij](hxxps://evil[.]example/a)", TeamsCardFormatterTests.AllText(card), StringComparison.Ordinal);
    }

    [Fact]
    public void LongDigest_StaysWithin24KiB_AndSaysHowMuchIsShown()
    {
        var model = DigestSamples.Full(newAccounts: 500, maxRows: 500);

        var message = TeamsDigestCardFormatter.Format(model, FormattingSamples.Context());

        Assert.True(message.Body.Length <= CardBudget.MaxBytes, message.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var text = TeamsCardFormatterTests.AllText(TeamsCardFormatterTests.AssertValidMessage(message));
        Assert.Contains("… i 490 kolejnych – pełna lista w zakładce Watchdog", text, StringComparison.Ordinal);
        Assert.Contains("Pokazano", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyDigest_IsTheHeartbeatCard()
    {
        var card = TeamsCardFormatterTests.AssertValidMessage(TeamsDigestCardFormatter.Format(DigestSamples.Empty(), FormattingSamples.Context()));

        var text = TeamsCardFormatterTests.AllText(card);
        Assert.Contains("Brak zmian w okresie.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Pokazano", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DetailLevel.Minimal)]
    [InlineData(DetailLevel.Standard)]
    [InlineData(DetailLevel.Full)]
    public void EveryDetailLevel_ProducesAValidCard(DetailLevel level) =>
        TeamsCardFormatterTests.AssertValidMessage(TeamsDigestCardFormatter.Format(DigestSamples.Full(), FormattingSamples.Context(level: level)));

    [Fact]
    public void WithoutPublicBaseUrl_ThereIsNoAction()
    {
        var card = TeamsCardFormatterTests.AssertValidMessage(TeamsDigestCardFormatter.Format(DigestSamples.Full(), FormattingSamples.Context(publicBaseUrl: null)));

        Assert.False(card.ContainsKey("actions") && card["actions"]!.AsArray().Count > 0);
    }
}
