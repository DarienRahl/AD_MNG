using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Presentation.Localization;
using AdMng.Presentation.UserMessages;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>Every U1c key, problem kind, issue code, transport code and hint exists in English and Polish with the same
/// placeholders.</summary>
public sealed partial class RichHtmlLocalizationTests
{
    private static readonly ResxLocalizer English = new(CultureInfo.GetCultureInfo("en-US"));
    private static readonly ResxLocalizer Polish = new(CultureInfo.GetCultureInfo("pl-PL"));

    public static IEnumerable<string> Keys() =>
        typeof(L.UserMessagesRich).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!)
            .Concat(Enum.GetValues<HtmlImportProblemKind>().Select(L.UserMessagesRich.ProblemKey))
            .Concat(HtmlIssueCodes.All.Select(UserMessageText.IssueKey))
            .Concat(new[] { WatchdogUserMessageTransport.ImagesNotSupportedCode, WatchdogUserMessageTransport.ImageStorageCode, "smtp.invalidImage" }.Select(UserMessageText.CodeKey))
            .Append("Watchdog.Hint." + DeliveryHints.MessageTooLarge);

    [Fact]
    public void EveryKey_ExistsInBothLanguages_WithTheSamePlaceholders()
    {
        var keys = Keys().ToList();
        Assert.True(keys.Count > 70, keys.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var key in keys)
        {
            var en = English[key];
            var pl = Polish[key];
            Assert.False(en.StartsWith('['), key);
            Assert.False(pl.StartsWith('['), key);
            Assert.Equal(Placeholders(en), Placeholders(pl));
        }
    }

    [Fact]
    public void TheDynamicFamilies_IncludeTheU1cCodes()
    {
        var dynamicKeys = UserMessageText.AllDynamicKeys().ToHashSet(StringComparer.Ordinal);
        Assert.All(HtmlIssueCodes.All, code => Assert.Contains(UserMessageText.IssueKey(code), dynamicKeys));
        Assert.Contains(UserMessageText.CodeKey(WatchdogUserMessageTransport.ImagesNotSupportedCode), dynamicKeys);
        Assert.Contains(DeliveryHints.MessageTooLarge, WatchdogDisplay.Hints);
    }

    [Fact]
    public void PolishTexts_ReadNaturally()
    {
        Assert.Equal("Wklej sformatowaną treść", Polish[L.UserMessagesRich.PasteFormatted]);
        Assert.Equal("Zezwól na domenę contoso.pl", Polish.Format(L.UserMessagesRich.AllowDomain, "contoso.pl"));
        Assert.Equal("Wyślij bez obrazów", Polish[L.UserMessagesRich.SendWithoutImages]);
        Assert.Equal("Usunięto obraz z sieci (tracker.example) – treść z internetu nigdy nie jest pobierana.",
            Polish.Format(L.UserMessagesRich.ProblemKey(HtmlImportProblemKind.RemoteImageRemoved), "tracker.example"));
        Assert.Equal(
            "Wysyłka bez obrazów – każdy obraz zastąpiono jego opisem.",
            UserMessageText.Issue(Polish, new MessageIssue(HtmlIssueCodes.ImagesOmitted, MessageIssueSeverity.Info)));
    }

    private static IReadOnlyList<string> Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
