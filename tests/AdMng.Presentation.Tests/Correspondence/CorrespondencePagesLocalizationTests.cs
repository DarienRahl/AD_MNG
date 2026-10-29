using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using AdMng.Core.Correspondence.Composition;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// Every key of the message, preview, send and responses pages – the constants of their <c>L</c> classes and the dynamic
/// families (send modes, widths, compatibility findings, signature problems, response kinds) – exists in English and Polish
/// with the same placeholders; Polish texts use Polish typography.
/// </summary>
public sealed partial class CorrespondencePagesLocalizationTests
{
    private static readonly ResxLocalizer English = new(CultureInfo.GetCultureInfo("en-US"));
    private static readonly ResxLocalizer Polish = new(CultureInfo.GetCultureInfo("pl-PL"));

    public static TheoryData<string> PageKeys() =>
    [
        .. new[] { typeof(L.CorrespondenceMessage), typeof(L.CorrespondencePreview), typeof(L.CorrespondenceSend), typeof(L.CorrespondenceResponses) }
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!),
    ];

    [Theory]
    [MemberData(nameof(PageKeys))]
    public void EveryPageKey_ExistsInBothLanguages_WithTheSamePlaceholders(string key)
    {
        Assert.True(English.Contains(key), key);
        Assert.True(Polish.Contains(key), key);
        Assert.Equal(Placeholders(English[key]), Placeholders(Polish[key]));
    }

    [Fact]
    public void TheDynamicFamiliesOfThePages_AreListed_AndExistInBothLanguages()
    {
        var keys = CorrespondenceText.AllDynamicKeys().ToHashSet(StringComparer.Ordinal);
        string[] families =
        [
            .. Enum.GetValues<CorrespondenceSendMode>().Select(CorrespondenceText.SendModeKey),
            .. Enum.GetValues<CorrespondencePreviewWidth>().Select(CorrespondenceText.WidthKey),
            .. Enum.GetValues<MailLintKind>().Select(CorrespondenceText.LintKey),
            .. SignatureProblemCodes.All.Select(CorrespondenceText.SignatureProblemKey),
            .. Enum.GetValues<ResponseKind>().Select(CorrespondenceText.ResponseKindKey),
        ];

        Assert.Equal(2 + 3 + 9 + 7 + 4, families.Length);
        foreach (var key in families)
        {
            Assert.Contains(key, keys);
            Assert.True(English.Contains(key), key);
            Assert.True(Polish.Contains(key), key);
            Assert.Equal(Placeholders(English[key]), Placeholders(Polish[key]));
        }
    }

    [Fact]
    public void PolishTexts_UsePolishTypography()
    {
        Assert.Equal("Nie znaleziono podpisu „Sekretariat”.", Polish.Format(L.CorrespondenceMessage.SignatureNotFound, "Sekretariat"));
        Assert.Equal("Pole {{Miejscowość}} jest puste u 12 z 95 odbiorców – u nich zostanie pusta luka.",
            Polish.Format(L.CorrespondencePreview.EmptyField, "{{Miejscowość}}", 12, 95));
        Assert.Equal("Wysyłam 12 z 95 · zostało ok. 4 min",
            Polish.Format(L.CorrespondenceSend.ProgressSendEta, 12, 95, Polish.Format(L.CorrespondenceSend.EtaMinutes, 4)));
        Assert.Equal("Odpowiedzi: 40 z 95 · Tak: 30 · Nie: 10", Polish.Format(L.CorrespondenceResponses.Summary, 40, 95, 30, 10));
        Assert.Equal("28.10.2026 · Temat · 95 wiad.", Polish.Format(L.CorrespondenceResponses.Campaign, "28.10.2026", "Temat", 95));
        Assert.Equal("(bez podpisu)", Polish[L.CorrespondenceMessage.NoSignature]);
        Assert.Equal("Osobna wiadomość do każdego", Polish[CorrespondenceText.SendModeKey(CorrespondenceSendMode.Individual)]);
        Assert.Equal("Komputer (700 px)", Polish[CorrespondenceText.WidthKey(CorrespondencePreviewWidth.Desktop)]);
        foreach (var key in PageKeys().Select(row => (string)row.Data))
        {
            var text = Polish[key];
            Assert.DoesNotContain("\"", text, StringComparison.Ordinal);    // „…” in Polish
            Assert.DoesNotContain(" - ", text, StringComparison.Ordinal);   // – in Polish
            Assert.DoesNotContain("...", text, StringComparison.Ordinal);   // …
        }
    }

    [Fact]
    public void Lint_AndSignatureProblems_AreLocalizedWithTheirDetails()
    {
        Assert.Equal("Element szerszy niż 700 px (900 px) – na telefonie wymusi przewijanie w bok.",
            CorrespondenceText.Lint(Polish, new MailLintFinding(MailLintKind.TooWide, "900")));
        Assert.Equal("Wiadomość waży 21,0 MB z obrazami i załącznikami – wiele serwerów odrzuca wiadomości powyżej 20 MB.",
            CorrespondenceText.Lint(Polish, new MailLintFinding(MailLintKind.LargeMessage, (21L * 1024 * 1024).ToString(CultureInfo.InvariantCulture))));
        Assert.Equal("The text of a link names another address than the link itself (bank.example) – recipients may take it for phishing.",
            CorrespondenceText.Lint(English, new MailLintFinding(MailLintKind.LinkTextMismatch, "bank.example")));
        Assert.Equal($"odrzucono obraz (większy niż {CorrespondenceText.Size(Polish, CorrespondenceLimits.MaxImageBytes)})",
            CorrespondenceText.SignatureProblem(Polish, SignatureProblemCodes.Format(SignatureProblemCodes.ImageRejected, "TooLarge")));
        Assert.Equal("10,0 MB", CorrespondenceText.Size(Polish, CorrespondenceLimits.MaxImageBytes));
        Assert.Equal("nie udało się odczytać obrazu Podpis_files/image001.png",
            CorrespondenceText.SignatureProblem(Polish, SignatureProblemCodes.Format(SignatureProblemCodes.ImageUnreadable, "Podpis_files/image001.png")));
        Assert.Equal("signature.future: x", CorrespondenceText.SignatureProblem(Polish, "signature.future: x"));
    }

    private static string[] Placeholders(string text) => [.. PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"(?<!\{)\{\d+[^}]*\}(?!\})")]
    private static partial Regex PlaceholderPattern();
}
