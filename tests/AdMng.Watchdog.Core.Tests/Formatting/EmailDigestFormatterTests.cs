using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting;
using AdMng.Watchdog.Core.Notifications.Digest;

namespace AdMng.Watchdog.Core.Tests.Formatting;

public sealed class EmailDigestFormatterTests
{
    [Fact]
    public void Digest_HasTheDailySubjectAndEverySection()
    {
        var email = EmailDigestFormatter.Format(DigestSamples.Full(), FormattingSamples.Context());

        Assert.Equal("[AD Watchdog] Raport dzienny contoso.com – 14.10.2026: 7 zdarzeń (2 wymagają uwagi)", email.Subject);
        foreach (var title in new[]
        {
            "Raport dzienny – contoso.com", "Wymaga uwagi (2)", "Nowe konta (3)", "Zmiany parametrów kont (1)", "Blokady (1)", "Nadchodzące terminy (1)",
            "Nieaktywne konta (15)", "Higiena", "Aktywne wyjątki (1)",
        })
        {
            Assert.Contains(title, email.HtmlBody, StringComparison.Ordinal);
            Assert.Contains(title, email.TextBody, StringComparison.Ordinal);
        }

        Assert.Contains("Okres: 13.10.2026 07:30:00 (UTC+02:00) – 14.10.2026 07:30:00 (UTC+02:00) · z sekcjami tygodniowymi", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("Kontroler domeny DC01.contoso.com (OK) · ostatni odczyt 14.10.2026 07:29:45 (UTC+02:00) · pokrycie atrybucji 97", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("kanały bez dostarczania: Teams – dział IT", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Wymagają uwagi", email.TextBody, StringComparison.Ordinal);
        EmailAlertFormatterTests.AssertNoMissingKeys(email.Subject + email.HtmlBody + email.TextBody);
    }

    [Fact]
    public void Rows_ShowTheirOwnColumns()
    {
        var email = EmailDigestFormatter.Format(DigestSamples.Full(), FormattingSamples.Context());
        var text = email.TextBody;

        Assert.Contains("niepotwierdzone", text, StringComparison.Ordinal);
        Assert.Contains(@"potwierdzone przez CONTOSO\adm.wójcik", text, StringComparison.Ordinal);
        Assert.Contains("Zażółć Gęślą Jaźń 001 (zgj001)", text, StringComparison.Ordinal);
        Assert.Contains("zablokowane 1× dziś, ostatnio 05:30; odblokowane 1×", text, StringComparison.Ordinal);
        Assert.Contains("10.0.0.12 (WS-ŻÓŁW-01) × 5", text, StringComparison.Ordinal);
        Assert.Contains("Dział: Sprzedaż → Księgowość", text, StringComparison.Ordinal);
        Assert.Contains("konto wygasa 19.10.2026", text, StringComparison.Ordinal);
        Assert.Contains("Stary Użytkownik (s.uzytkownik) – uprzywilejowane", text, StringComparison.Ordinal);
        Assert.Contains("nigdy nie logowano", text, StringComparison.Ordinal);
        Assert.Contains("Firma › Księgowość", text, StringComparison.Ordinal);
        Assert.Contains("Synchronizacja kadr (HR) · wykonawca S-1-5-21-1004336348-1177238915-682003330-3105 · atrybut department", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("brak danych (katalog niedostępny)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_SaysWhenTheSeverityWasRaisedAfterTheAlert()
    {
        // The acknowledged grant (High now) was alerted as Medium, then escalated (13.2 #7).
        var model = DigestSamples.Full() with { SeverityChanges = new Dictionary<long, Severity> { [18350] = Severity.Medium } };

        var polish = EmailDigestFormatter.Format(model, FormattingSamples.Context());
        var english = EmailDigestFormatter.Format(model, FormattingSamples.Context("en"));

        Assert.Contains("(priorytet: Średni → Wysoki)", polish.TextBody, StringComparison.Ordinal);
        Assert.Contains("(priorytet: Średni → Wysoki)", polish.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(1, polish.TextBody.Split("priorytet:").Length - 1);
        Assert.Contains("(severity: Medium → High)", english.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void HostileDirectoryValues_AreEncodedAndDefanged()
    {
        var hostile = FormattingSamples.UserCreated(99);
        hostile = hostile with { Object = hostile.Object with { DisplayName = FormattingSamples.HostileDescription } };
        var model = DigestSamples.Full() with
        {
            Sections = [new DigestSection { Kind = DigestSectionKind.NewAccounts, Total = 1, Rows = [new DigestEventRow(hostile)] }],
            Domain = "contoso.com" + FormattingSamples.Rlo,
        };

        var email = EmailDigestFormatter.Format(model, FormattingSamples.Context());

        Assert.DoesNotContain("<script>", email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(FormattingSamples.Rlo, email.HtmlBody + email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("https://evil.example", email.HtmlBody + email.TextBody, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("hxxps://evil[.]example/x", email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(FormattingSamples.Rlo, email.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreRowsThanShown_AreAnnounced()
    {
        var model = DigestSamples.Full(newAccounts: 60, maxRows: 50);

        var email = EmailDigestFormatter.Format(model, FormattingSamples.Context());

        Assert.Contains("… i 10 kolejnych – pełna lista w zakładce Watchdog", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("Nowe konta (60)", email.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPeriod_IsAHeartbeat()
    {
        var email = EmailDigestFormatter.Format(DigestSamples.Empty(), FormattingSamples.Context());

        Assert.Equal("[AD Watchdog] Raport dzienny contoso.com – 14.10.2026: 0 zdarzeń", email.Subject);
        Assert.Contains("Brak zmian w okresie.", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Brak zmian w okresie.", email.TextBody, StringComparison.Ordinal);
        Assert.Equal("Info", email.Headers["X-AdMng-Severity"]);
    }

    [Fact]
    public void MinimalDetail_HidesWorkstationsAndPersonalValues()
    {
        var email = EmailDigestFormatter.Format(DigestSamples.Full(), FormattingSamples.Context(level: DetailLevel.Minimal));

        Assert.DoesNotContain("WS-ŻÓŁW-01", email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Sprzedaż → Księgowość", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("Dział", email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Firma › Księgowość", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void English_UsesTheEnglishTexts()
    {
        var email = EmailDigestFormatter.Format(DigestSamples.Full(), FormattingSamples.Context("en"));

        Assert.Equal("[AD Watchdog] Daily report contoso.com – 2026-10-14: 7 events (2 need attention)", email.Subject);
        Assert.Contains("Needs attention (2)", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("New accounts (3)", email.TextBody, StringComparison.Ordinal);
        EmailAlertFormatterTests.AssertNoMissingKeys(email.HtmlBody + email.TextBody);
    }

    [Fact]
    public void Headers_CarryTheEventIdsAndTheHighestAttentionSeverity()
    {
        var email = EmailDigestFormatter.Format(DigestSamples.Full(), FormattingSamples.Context());

        Assert.Equal("Critical", email.Headers["X-AdMng-Severity"]);
        Assert.Equal("auto-generated", email.Headers["Auto-Submitted"]);
        Assert.False(email.Headers.ContainsKey("X-AdMng-Rule"));
        Assert.Equal(4, email.Headers["X-AdMng-Event-Id"].Split(", ").Length);
    }

    [Fact]
    public void TextAlternative_KeepsLinesShort()
    {
        var text = EmailDigestFormatter.Format(DigestSamples.Full(newAccounts: 20), FormattingSamples.Context()).TextBody;

        Assert.All(text.Split('\n'), line => Assert.True(line.Length <= 100, line));
    }

    [Fact]
    public void SectionTitles_ExistForEverySection()
    {
        var context = FormattingSamples.Context();
        foreach (var kind in Enum.GetValues<DigestSectionKind>())
        {
            EmailAlertFormatterTests.AssertNoMissingKeys(DigestText.SectionTitle(new DigestSection { Kind = kind, Total = 1 }, context));
            Assert.All(DigestRowFormatter.Headers(kind, context), h => EmailAlertFormatterTests.AssertNoMissingKeys(h));
        }
    }
}
