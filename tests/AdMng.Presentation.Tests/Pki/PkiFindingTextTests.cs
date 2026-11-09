using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Pki;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// The texts of the AD CS security review (docs/dev/p-certificate-services.md 2.8, the contract resx block): every code of
/// <see cref="PkiFindingCodes"/> has a title, a message, an explanation and a recommendation in English and Polish, the
/// message takes exactly the code's arguments (at most 8), and every key of <see cref="PkiTextKeys"/> exists in both languages.
/// </summary>
public sealed partial class PkiFindingTextTests
{
    private static readonly ResourceManager Resources = new(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);

    [Fact]
    public void EveryCode_HasItsFourTexts_InBothLanguages()
    {
        var missing = new List<string>();
        foreach (var key in PkiFindingText.AllKeys())
        {
            foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("pl") })
            {
                var value = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString(key);
                if (string.IsNullOrWhiteSpace(value))
                {
                    missing.Add($"{key} ({(culture.Name.Length == 0 ? "en" : culture.Name)})");
                }
            }
        }

        Assert.Equal([], missing);
        Assert.Equal(11 * 4, PkiFindingText.AllKeys().Count());
        Assert.Equal(11, PkiFindingCodes.All.Count);
    }

    [Fact]
    public void EveryMessage_FormatsWithTheArgumentsOfItsCode()
    {
        Assert.Equal(PkiFindingCodes.All.Select(d => d.Code).Order(StringComparer.Ordinal), PkiFindingText.ArgumentCounts.Keys.Order(StringComparer.Ordinal));
        Assert.All(PkiFindingText.ArgumentCounts.Values, count => Assert.InRange(count, 0, 8));
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("pl-PL") })
        {
            var localizer = new ResxLocalizer(culture);
            foreach (var (code, count) in PkiFindingText.ArgumentCounts)
            {
                var message = localizer[PkiFindingText.MessageKey(code)];
                var indexes = Placeholder().Matches(message).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order().ToList();

                Assert.True(Enumerable.Range(0, count).SequenceEqual(indexes), $"{code} ({culture.Name}): {message}");
                var finding = new PkiFinding(code, PkiFindingSeverity.High, PkiFindingArea.Template, null, [.. Enumerable.Range(0, count).Select(i => "arg" + i)]);
                var text = PkiFindingText.Message(localizer, finding);
                Assert.All(finding.Arguments, a => Assert.Contains(a, text, StringComparison.Ordinal));
            }

            foreach (var code in PkiFindingCodes.All.Select(d => d.Code))
            {
                foreach (var key in new[] { PkiFindingText.TitleKey(code), PkiFindingText.ExplanationKey(code), PkiFindingText.RecommendationKey(code) })
                {
                    Assert.Empty(Placeholder().Matches(localizer[key]));
                }
            }
        }
    }

    [Fact]
    public void EverySharedName_ExistsInBothLanguages()
    {
        var en = Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var pl = Resources.GetResourceSet(CultureInfo.GetCultureInfo("pl"), true, false)!;
        var keys = PkiTextKeys.AllKeys().ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([], keys.Where(k => en.GetString(k) is null));
        Assert.Equal([], keys.Where(k => pl.GetString(k) is null));
        Assert.Equal(7, PkiTextKeys.NoteFlags.Count);
        Assert.DoesNotContain(PkiFindingNotes.None, PkiTextKeys.NoteFlags);
        Assert.Throws<ArgumentOutOfRangeException>(() => PkiTextKeys.NoteKey(PkiFindingNotes.NotPublished | PkiFindingNotes.AssumesCaEnroll));
    }

    [Fact]
    public void Polish_ReadsAsTheConsoles_WithDiacritics()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("Rejestruj", l[PkiTextKeys.RightKey(TemplateRight.Enroll)]);
        Assert.Equal("Autorejestracja", l[PkiTextKeys.RightKey(TemplateRight.AutoEnroll)]);
        Assert.Equal("Pełna kontrola", l[PkiTextKeys.RightKey(TemplateRight.FullControl)]);
        Assert.Equal("Wygasa", l[PkiTextKeys.HealthKey(PkiHealthStatus.Expiring)]);
        Assert.Equal("Wygasła", l[PkiTextKeys.HealthKey(PkiHealthStatus.Expired)]);
        Assert.Equal("Niedostępna", l[PkiTextKeys.HealthKey(PkiHealthStatus.Unavailable)]);
        Assert.Equal("Krytyczna", PkiFindingText.Severity(l, PkiFindingSeverity.Critical));
        Assert.Equal("Krytyczne: 3", l.Format(PkiTextKeys.ChipKey(PkiFindingSeverity.Critical), 3));
        Assert.Equal("Szablon certyfikatu", PkiFindingText.Area(l, PkiFindingArea.Template));
        Assert.Equal("Słabe", l[PkiTextKeys.MappingKey(MappingStrength.Weak)]);
        Assert.Equal("Konto lub grupa spoza Tier 0", l[PkiTextKeys.ClassKey(PrincipalClass.Other)]);
        Assert.StartsWith("DC bez certyfikatu LDAPS", l[PkiTextKeys.TlsKey(TlsProbeOutcome.HandshakeFailed)], StringComparison.Ordinal);
        Assert.Contains("opublikowanych w AD", l[PkiTextKeys.KdcKey(KdcReadinessState.Ready)], StringComparison.Ordinal);
        Assert.Equal("Szablon nie jest wystawiany (do wykorzystania dopiero po opublikowaniu)", PkiTextKeys.Notes(l, PkiFindingNotes.NotPublished).Single());
        Assert.Equal("Wnioskujący podaje podmiot certyfikatu uwierzytelniania klienta", l[PkiFindingText.TitleKey(PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth)]);
        Assert.Equal(
            "Szablon Użytkownik-VPN-Podatny: podmiot dostarcza wnioskujący, a certyfikat pozwala na uwierzytelnianie klienta; rejestrować się może: Użytkownicy domeny (kont: 12).",
            PkiFindingText.Message(l, new PkiFinding(PkiFindingCodes.EnrolleeSuppliesSubjectClientAuth, PkiFindingSeverity.Critical, PkiFindingArea.Template, null,
                ["Użytkownik-VPN-Podatny", "Użytkownicy domeny", "12"])));
    }

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();
}
