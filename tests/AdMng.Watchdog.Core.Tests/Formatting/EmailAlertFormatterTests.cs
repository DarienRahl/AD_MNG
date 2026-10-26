using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Formatting;

public sealed class EmailAlertFormatterTests
{
    [Fact]
    public void SingleAlert_HasTheBannerSentenceFactsLinksAndFooter_InPolish()
    {
        var email = EmailAlertFormatter.Alert(FormattingSamples.PrivilegeGranted(), FormattingSamples.Rule(), FormattingSamples.Context());

        Assert.Contains("#b00020", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("KRYTYCZNE · Dostęp uprzywilejowany", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Konto Jan Kowalski (jan.kowalski) uzyskało dostęp do grupy Domain Admins.", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("(członkostwo pośrednie: GG_Admins_Serwery → Domain Admins)", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Dodano do grupy uprzywilejowanej (reguła: Dostęp uprzywilejowany)", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Jan Kowalski (jan.kowalski) · Firma › Pracownicy › IT", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(@"CONTOSO\adm.nowak (Anna Nowak) · pewność: wysoka", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("10.0.12.34 · WS-ADM-01 (DNS) · logowanie sieciowe (4624) · pewność: niska", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("14.10.2026 14:32:05 (UTC+02:00) · 12:32:05 UTC", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("DC01.contoso.com (zdarzenie 4728, rekord 918273)", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("href=\"https://admng-wd.contoso.com:8443/events/0192f3a4-5b6c-7d8e-9f00-112233445566\"", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("W AD_MNG: Narzędzia → Watchdog → zdarzenie nr 18342", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Reguła tier0-privilege · zdarzenie 18342 · usługa admng-wd.contoso.com · wiadomość automatyczna, nie odpowiadaj", email.HtmlBody, StringComparison.Ordinal);
        Assert.StartsWith("<!DOCTYPE html>", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<meta charset=\"utf-8\">", email.HtmlBody, StringComparison.Ordinal);
        AssertNoMissingKeys(email.HtmlBody + email.TextBody + email.Subject);
    }

    [Fact]
    public void SingleAlert_InEnglish_UsesTheEnglishLabels()
    {
        var email = EmailAlertFormatter.Alert(FormattingSamples.PrivilegeGranted(), FormattingSamples.Rule(), FormattingSamples.Context("en"));

        Assert.Contains("CRITICAL · Privileged access", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("The account Jan Kowalski (jan.kowalski) was granted membership of Domain Admins.", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("(nested: GG_Admins_Serwery → Domain Admins)", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(">What<", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(">Who<", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("automated message, do not reply", email.HtmlBody, StringComparison.Ordinal);
        AssertNoMissingKeys(email.HtmlBody + email.TextBody);
    }

    [Fact]
    public void HostileDirectoryValues_AreEncodedAndDefanged()
    {
        var e = FormattingSamples.PrivilegeGranted() with
        {
            Object = FormattingSamples.Jan with { DisplayName = "<script>alert(1)</script>" },
            Via = [new ObjectRef { Dn = "CN=x,DC=contoso,DC=com", Name = "[Kliknij](https://evil.example/grupa)" + FormattingSamples.Rlo }, FormattingSamples.DomainAdmins],
        };

        var email = EmailAlertFormatter.Alert(e, null, FormattingSamples.Context());
        var changed = EmailAlertFormatter.Alert(FormattingSamples.AttributesChanged(), null, FormattingSamples.Context());

        foreach (var body in new[] { email.HtmlBody, changed.HtmlBody })
        {
            Assert.DoesNotContain("<script>", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://evil", body, StringComparison.Ordinal);
            Assert.DoesNotContain(FormattingSamples.Rlo, body, StringComparison.Ordinal);
        }

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("[Kliknij](hxxps://evil[.]example/grupa)&lt;U+202E&gt;", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("hxxps://evil[.]example/x", changed.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("www[.]zly.example", changed.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<U+202E>gpj.exe", changed.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Changes_AreMaskedPerDetailLevel()
    {
        var full = EmailAlertFormatter.Alert(FormattingSamples.AttributesChanged(), null, FormattingSamples.Context(level: DetailLevel.Full));
        var standard = EmailAlertFormatter.Alert(FormattingSamples.AttributesChanged(), null, FormattingSamples.Context(level: DetailLevel.Standard));
        var minimal = EmailAlertFormatter.Alert(FormattingSamples.AttributesChanged(), null, FormattingSamples.Context(level: DetailLevel.Minimal));

        Assert.Contains("Księgowość", full.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("+48 600 100 201", full.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("514 (+Konto wyłączone)", full.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("zmieniono (wartość ukryta)", full.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Księgowość", standard.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("600 100", standard.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Księgowość", minimal.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Dział", minimal.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("WS-ADM-01", minimal.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.12.34", minimal.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedAndPendingActors_ShowReasonRemedyOrFollowUp()
    {
        var unknown = EmailAlertFormatter.Alert(
            FormattingSamples.PrivilegeGranted(new EventAttribution { Status = AttributionStatus.Unknown, Reason = AttributionReasons.NoMatch }), null,
            FormattingSamples.Context());
        var pending = EmailAlertFormatter.Alert(FormattingSamples.PrivilegeGranted(EventAttribution.Pending), null, FormattingSamples.Context(), followUpExpected: true);

        Assert.Contains("nieustalony – Nie znaleziono pasującego zdarzenia w oknie czasowym (NO_MATCH)", unknown.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("ustalanie… (uzupełnienie zostanie wysłane)", pending.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("wykonawca: ustalanie…", pending.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Lockouts_ShowTheBadPasswordSources_AndNoWhoRow()
    {
        var email = EmailAlertFormatter.Alert(FormattingSamples.LockedOut(), FormattingSamples.Rule("lockouts", "Blokady kont"), FormattingSamples.Context());

        Assert.Contains("Źródło błędnych haseł", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("10.0.0.12 (WS-ŻÓŁW-01) × 5 · LAPTOP-ŁĄCKA × 2", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("zablokowane 3× dziś, ostatnio 14:32", email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(">Kto<", email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("wykonawca", email.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregated_ListsItemsUpToTheCap_AndAnnouncesTheRest()
    {
        var events = Enumerable.Range(1, 3).Select(i => FormattingSamples.UserCreated(i)).ToList();

        var email = EmailAlertFormatter.Aggregated(events, 22, FormattingSamples.Rule("new-accounts", "Nowe konta"), FormattingSamples.Context());

        Assert.Equal(@"[AD Watchdog][NISKI] Nowe konta: 25 (OU=Sprzedaż) – CONTOSO\adm.nowak", email.Subject);
        Assert.Contains("Nowe konta: 25 (OU=Sprzedaż) · wykonawca CONTOSO\\adm.nowak", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(">Czas<", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Zażółć Gęślą Jaźń 003 (zgj003)", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("(+22 kolejnych)", email.HtmlBody, StringComparison.Ordinal);
        Assert.Equal("Low", email.Headers["X-AdMng-Severity"]);
        Assert.Equal("new-accounts", email.Headers["X-AdMng-Rule"]);
        Assert.Equal(3, email.Headers["X-AdMng-Event-Id"].Split(", ").Length);
    }

    [Fact]
    public void FollowUp_RepliesInTheSameThread()
    {
        var email = EmailAlertFormatter.FollowUp([FormattingSamples.PrivilegeGranted()], "[AD Watchdog][KRYTYCZNE] Dodano do grupy uprzywilejowanej: x",
            "0192f3a45b6c7d8e9f00112233445566", FormattingSamples.Rule(), FormattingSamples.Context());

        Assert.Equal("Re: [AD Watchdog][KRYTYCZNE] Dodano do grupy uprzywilejowanej: x", email.Subject);
        Assert.Equal("0192f3a45b6c7d8e9f00112233445566", email.InReplyTo);
        Assert.Contains("Uzupełnienie: wykonawca ustalony", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(@"CONTOSO\adm.nowak", email.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEscalation_SaysTheSeverityWasRaised_AndShowsTheChange()
    {
        // The alert showed the grant as Medium with the actor pending; the follow-up brings the actor and Critical.
        var e = FormattingSamples.PrivilegeGranted();
        var email = EmailAlertFormatter.FollowUp([e], "[AD Watchdog][ŚREDNI] Dodano do grupy uprzywilejowanej: x", "0192f3a45b6c7d8e9f00112233445566",
            FormattingSamples.Rule(), FormattingSamples.Context(), [new FollowUpChange(e.Id, Severity.Medium, ActorWasPending: true)]);

        Assert.Equal("Re: [AD Watchdog][ŚREDNI] Dodano do grupy uprzywilejowanej: x", email.Subject);
        Assert.Equal("Critical", email.Headers["X-AdMng-Severity"]);
        Assert.StartsWith("Uzupełnienie: wykonawca ustalony, priorytet podniesiony · KRYTYCZNE ·", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("Priorytet:", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("Średni → Krytyczny", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("Średni → Krytyczny", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(@"CONTOSO\adm.nowak", email.TextBody, StringComparison.Ordinal);

        var english = EmailAlertFormatter.FollowUp([e], "x", "0192f3a45b6c7d8e9f00112233445566", FormattingSamples.Rule(), FormattingSamples.Context("en"),
            [new FollowUpChange(e.Id, Severity.Medium, ActorWasPending: false)]);
        Assert.StartsWith("Follow-up: severity raised · CRITICAL ·", english.TextBody, StringComparison.Ordinal);
        Assert.Contains("Medium → Critical", english.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void AFollowUpWithoutAChange_LooksAsBefore_AndALoweredSeverityIsShownWithoutARaise()
    {
        var e = FormattingSamples.PrivilegeGranted();
        var unchanged = EmailAlertFormatter.FollowUp([e], "x", "0192f3a45b6c7d8e9f00112233445566", FormattingSamples.Rule(), FormattingSamples.Context(),
            [new FollowUpChange(e.Id, Severity.Critical, ActorWasPending: true)]);
        var lowered = EmailAlertFormatter.FollowUp([e with { Severity = Severity.Info }], "x", "0192f3a45b6c7d8e9f00112233445566", FormattingSamples.Rule(),
            FormattingSamples.Context(), [new FollowUpChange(e.Id, Severity.High, ActorWasPending: true)]);

        Assert.StartsWith("Uzupełnienie: wykonawca ustalony · KRYTYCZNE ·", unchanged.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Priorytet", unchanged.TextBody, StringComparison.Ordinal);
        Assert.StartsWith("Uzupełnienie: wykonawca ustalony · INFO ·", lowered.TextBody, StringComparison.Ordinal);
        Assert.Contains("Wysoki → Info", lowered.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAggregatedFollowUp_ShowsEachEventWithItsOwnSeverity()
    {
        var granted = FormattingSamples.PrivilegeGranted();
        var changed = FormattingSamples.AttributesChanged() with { Severity = Severity.High };
        var email = EmailAlertFormatter.FollowUp([granted, changed], "x", "0192f3a45b6c7d8e9f00112233445566", FormattingSamples.Rule(), FormattingSamples.Context("en"),
            [new FollowUpChange(granted.Id, Severity.Critical, true), new FollowUpChange(changed.Id, Severity.Low, true)]);

        Assert.StartsWith("Follow-up: actor determined, severity raised · CRITICAL ·", email.TextBody, StringComparison.Ordinal);
        Assert.Contains(">Severity<", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Severity: Critical", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("Severity: Low → High", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Headers_MarkAutomatedMessages_AndCapTheEventIds()
    {
        var headers = EmailAlertFormatter.Headers(Severity.High, "lockouts", Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()));

        Assert.Equal("auto-generated", headers["Auto-Submitted"]);
        Assert.Equal("All", headers["X-Auto-Response-Suppress"]);
        Assert.Equal(EmailAlertFormatter.MaxEventIdsInHeader, headers["X-AdMng-Event-Id"].Split(", ").Length);
    }

    [Fact]
    public void TextAlternative_IsAlignedAndAtMost100CharactersPerLine()
    {
        var e = FormattingSamples.AttributesChanged() with
        {
            Changes = [new AttributeChange { Attribute = "description", NewValues = [string.Concat(Enumerable.Repeat("bardzo długi opis ", 15))], NewCount = 1 }],
        };

        var email = EmailAlertFormatter.Alert(e, null, FormattingSamples.Context(level: DetailLevel.Full));

        Assert.All(email.TextBody.Split('\n'), line => Assert.True(line.Length <= PlainTextWriter.LineWidth, line));
        Assert.Contains("Kto:", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("Obiekt:", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("Otwórz w przeglądarce: https://admng-wd.contoso.com:8443/events/", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutPublicBaseUrl_ThereIsNoLink()
    {
        var email = EmailAlertFormatter.Alert(FormattingSamples.PrivilegeGranted(), null, FormattingSamples.Context(publicBaseUrl: null));

        Assert.DoesNotContain("href=", email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Otwórz w przeglądarce", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("usługa AD Watchdog", email.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TestMessage_IsSimple()
    {
        var email = EmailAlertFormatter.Test(FormattingSamples.Context(), FormattingSamples.Now);

        Assert.Equal("[AD Watchdog] Wiadomość testowa", email.Subject);
        Assert.Contains("To jest wiadomość testowa usługi AD Watchdog. Wysłano: 14.10.2026 14:32:05 (UTC+02:00).", email.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKind_RendersWithoutMissingTexts()
    {
        foreach (var language in new[] { "pl", "en" })
        {
            var context = FormattingSamples.Context(language);
            foreach (var kind in Enum.GetValues<WatchEventKind>())
            {
                var e = FormattingSamples.PrivilegeGranted() with { Kind = kind };
                var email = EmailAlertFormatter.Alert(e, null, context);
                AssertNoMissingKeys(email.Subject + email.HtmlBody + email.TextBody);
            }
        }
    }

    internal static void AssertNoMissingKeys(string text) =>
        Assert.DoesNotMatch(@"\[(Kind|Severity|Label|Value|Email|Card|Subject|Source|Confidence|Reason|Page|Digest|Day|Rule|DeliveryKind|OutboxStatus)\.[A-Za-z.]+\]", text);
}
