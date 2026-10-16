using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Formatting;

public sealed class EmailSubjectBuilderTests
{
    [Theory]
    [InlineData("pl", @"[AD Watchdog][KRYTYCZNE] Dodano do grupy uprzywilejowanej: Jan Kowalski (jan.kowalski) → Domain Admins – CONTOSO\adm.nowak")]
    [InlineData("en", @"[AD Watchdog][CRITICAL] Privileged access granted: Jan Kowalski (jan.kowalski) → Domain Admins – CONTOSO\adm.nowak")]
    public void Single_PutsSeverityKindObjectAndActorFirst(string language, string expected)
    {
        var context = FormattingSamples.Context(language);
        var content = AlertContentBuilder.Build(FormattingSamples.PrivilegeGranted(), FormattingSamples.Rule(), context);

        Assert.Equal(expected, EmailSubjectBuilder.Single(content, context));
    }

    [Theory]
    [InlineData("pl", "[AD Watchdog][NISKI] Nowe konto: Zofia Żak (zofia.zak) – wykonawca: ustalanie…")]
    [InlineData("en", "[AD Watchdog][LOW] New account: Zofia Żak (zofia.zak) – actor: pending…")]
    public void Single_WithAPendingActor(string language, string expected)
    {
        var context = FormattingSamples.Context(language);
        var e = FormattingSamples.UserCreated(1, EventAttribution.Pending) with
        {
            Object = new ObjectRef { Dn = "CN=Zofia Żak,OU=Księgowość,DC=contoso,DC=com", DisplayName = "Zofia Żak", SamAccountName = "zofia.zak", Kind = WatchObjectKind.User },
        };

        Assert.Equal(expected, EmailSubjectBuilder.Single(AlertContentBuilder.Build(e, null, context), context));
    }

    [Theory]
    [InlineData("pl", @"[AD Watchdog][NISKI] Nowe konta: 25 (OU=Sprzedaż) – CONTOSO\adm.nowak")]
    [InlineData("en", @"[AD Watchdog][LOW] New accounts: 25 (OU=Sprzedaż) – CONTOSO\adm.nowak")]
    public void Aggregated_CountsCommonOuAndActor(string language, string expected)
    {
        var context = FormattingSamples.Context(language);
        var events = Enumerable.Range(1, 25).Select(i => FormattingSamples.UserCreated(i)).ToList();

        Assert.Equal(expected, EmailSubjectBuilder.Aggregated(events, 25, Severity.Low, null, context));
    }

    [Fact]
    public void Aggregated_OfMixedKindsAndActors_UsesTheRuleName()
    {
        var context = FormattingSamples.Context();
        IReadOnlyList<WatchEvent> events = [FormattingSamples.UserCreated(1), FormattingSamples.PrivilegeGranted(EventAttribution.Pending)];

        Assert.Equal("[AD Watchdog][KRYTYCZNE] Dostęp uprzywilejowany: 7",
            EmailSubjectBuilder.Aggregated(events, 7, Severity.Critical, FormattingSamples.Rule(), context));
    }

    [Fact]
    public void FollowUp_PrefixesRe_Once()
    {
        Assert.Equal("Re: [AD Watchdog][NISKI] Nowe konto: x", EmailSubjectBuilder.FollowUp("[AD Watchdog][NISKI] Nowe konto: x"));
        Assert.Equal("Re: temat", EmailSubjectBuilder.FollowUp("Re: temat"));
    }

    [Theory]
    [InlineData("pl", 71, 2, "[AD Watchdog] Raport dzienny contoso.com – 14.10.2026: 71 zdarzeń (2 wymagają uwagi)")]
    [InlineData("pl", 1, 1, "[AD Watchdog] Raport dzienny contoso.com – 14.10.2026: 1 zdarzenie (1 wymaga uwagi)")]
    [InlineData("pl", 3, 0, "[AD Watchdog] Raport dzienny contoso.com – 14.10.2026: 3 zdarzenia")]
    [InlineData("en", 71, 2, "[AD Watchdog] Daily report contoso.com – 2026-10-14: 71 events (2 need attention)")]
    public void Digest_UsesPluralForms(string language, int events, int attention, string expected) =>
        Assert.Equal(expected, EmailSubjectBuilder.Digest("contoso.com", new DateOnly(2026, 10, 14), events, attention, FormattingSamples.Context(language)));

    [Theory]
    [InlineData("pl", "[AD Watchdog] Wiadomość testowa")]
    [InlineData("en", "[AD Watchdog] Test message")]
    public void Test_HasAFixedSubject(string language, string expected) =>
        Assert.Equal(expected, EmailSubjectBuilder.Test(FormattingSamples.Context(language)));

    [Fact]
    public void DirectoryStrings_CannotInjectHeadersOrLinks()
    {
        var context = FormattingSamples.Context();
        var e = FormattingSamples.PrivilegeGranted() with
        {
            Object = FormattingSamples.Jan with { DisplayName = "Jan\r\nBcc: zly@example.com https://evil.example" },
        };

        var subject = EmailSubjectBuilder.Single(AlertContentBuilder.Build(e, null, context), context);

        Assert.DoesNotContain('\r', subject);
        Assert.DoesNotContain('\n', subject);
        Assert.Contains("Jan<U+000D><U+000A>Bcc: zly@example.com hxxps://evil[.]example", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void LongObjects_AreCutSoTheSubjectStaysWithin200Characters()
    {
        var context = FormattingSamples.Context();
        var e = FormattingSamples.PrivilegeGranted() with
        {
            Object = FormattingSamples.Jan with { DisplayName = string.Concat(Enumerable.Repeat("Bardzo długa nazwa wyświetlana ", 20)) },
        };

        var subject = EmailSubjectBuilder.Single(AlertContentBuilder.Build(e, null, context), context);

        Assert.Equal(EmailSubjectBuilder.MaxLength, subject.Length);
        Assert.StartsWith("[AD Watchdog][KRYTYCZNE] Dodano do grupy uprzywilejowanej: Bardzo długa", subject, StringComparison.Ordinal);
        Assert.EndsWith(@"… – CONTOSO\adm.nowak", subject, StringComparison.Ordinal);
    }
}
