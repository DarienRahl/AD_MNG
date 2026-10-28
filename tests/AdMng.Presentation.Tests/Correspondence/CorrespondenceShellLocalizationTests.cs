using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// Every key of the Korespondencja window, the Outlook connection and the U1 Outlook transport exists in English and Polish
/// with the same placeholders; every dynamic key of <see cref="CorrespondenceText.AllDynamicKeys"/> too (the whole list,
/// so the other pages' families are checked here as well).
/// </summary>
public sealed partial class CorrespondenceShellLocalizationTests
{
    private static readonly ResxLocalizer English = new(CultureInfo.GetCultureInfo("en-US"));
    private static readonly ResxLocalizer Polish = new(CultureInfo.GetCultureInfo("pl-PL"));

    private static IEnumerable<string> ShellKeys() =>
        new[] { typeof(L.Correspondence), typeof(L.CorrespondenceOutlook), typeof(L.UserMessagesOutlook) }
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Concat(OutlookFailureCodes.All.Select(UserMessageText.CodeKey))
            .Concat([UserMessageText.TransportKey(UserMessageTransportChoice.Outlook), L.UserMessages.ViaOutlook])
            .Concat(new[] { UserMessageTransportChoiceResult.OutlookNotSupported, UserMessageTransportChoiceResult.OutlookNotInstalled }.Select(UserMessageText.ProblemKey));

    [Fact]
    public void EveryDynamicKey_ExistsInBothLanguages()
    {
        var keys = CorrespondenceText.AllDynamicKeys();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(Enum.GetValues<OutlookState>(), state => Assert.Contains(CorrespondenceText.OutlookStateKey(state), keys));
        Assert.All(OutlookFailureCodes.All, code => Assert.Contains(CorrespondenceText.OutlookCodeKey(code), keys));
        foreach (var key in keys)
        {
            Assert.True(English.Contains(key), key);
            Assert.True(Polish.Contains(key), key);
            Assert.Equal(Placeholders(English[key]), Placeholders(Polish[key]));
        }
    }

    [Fact]
    public void EveryShellKey_ExistsInBothLanguages_WithTheSamePlaceholders()
    {
        var keys = ShellKeys().ToList();
        Assert.True(keys.Count >= 70, keys.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var key in keys)
        {
            Assert.True(English.Contains(key), key);
            Assert.True(Polish.Contains(key), key);
            Assert.Equal(Placeholders(English[key]), Placeholders(Polish[key]));
        }
    }

    [Fact]
    public void TheU1Families_IncludeTheOutlookCodesAndChoice()
    {
        var dynamicKeys = UserMessageText.AllDynamicKeys().ToHashSet(StringComparer.Ordinal);

        Assert.All(OutlookFailureCodes.All, code => Assert.Contains(UserMessageText.CodeKey(code), dynamicKeys));
        Assert.Contains(UserMessageText.TransportKey(UserMessageTransportChoice.Outlook), dynamicKeys);
        Assert.Contains(UserMessageText.ProblemKey(UserMessageTransportChoiceResult.OutlookNotInstalled), dynamicKeys);
        Assert.Contains("outlook.sent", UserMessageText.KnownCodes);
    }

    [Fact]
    public void PolishTexts_UsePolishTypography()
    {
        Assert.Equal("Korespondencja (Outlook)…", Polish[L.Correspondence.Command]);
        Assert.Equal("Korespondencja – contoso.test", Polish.Format(L.Correspondence.WindowTitle, "contoso.test"));
        Assert.Equal("Outlook: połączono (2 konta)", Polish.Format(L.CorrespondenceOutlook.ConnectedFew, 2));
        Assert.Equal("Outlook: połączono (7 kont)", Polish.Format(L.CorrespondenceOutlook.ConnectedMany, 7));
        Assert.Equal("Sprawdź Outlooka", Polish[L.CorrespondenceOutlook.Check]);
        Assert.Equal("Wysyłka przez Outlooka: konto domyślne profilu", Polish.Format(L.UserMessages.ViaOutlook, Polish[L.UserMessagesOutlook.DefaultAccount]));
        Assert.Equal("Pobierz konta z Outlooka", Polish[L.UserMessagesOutlook.LoadAccounts]);
        var shellDynamic = Enum.GetValues<OutlookState>().Select(CorrespondenceText.OutlookStateKey)
            .Concat(OutlookFailureCodes.All.Select(CorrespondenceText.OutlookCodeKey));
        foreach (var key in ShellKeys().Concat(shellDynamic))
        {
            var text = Polish[key];
            Assert.DoesNotContain("\"", text, StringComparison.Ordinal);      // „…” instead
            Assert.DoesNotContain(" - ", text, StringComparison.Ordinal);     // – instead
            Assert.DoesNotContain("...", text, StringComparison.Ordinal);     // … instead
        }
    }

    [Fact]
    public void FailureText_LocalizesKnownCodes_AndNeverShowsARawCode()
    {
        var denied = FakeFailure(OutlookFailureKind.SecurityDenied, OutlookFailureCodes.SecurityDenied, unchecked((int)0x80004004));
        Assert.Equal("Outlook refused to let a program send the message (its security prompt) – allow it in Outlook or check the antivirus program.",
            CorrespondenceText.FailureText(English, denied));

        var unknown = FakeFailure(OutlookFailureKind.Unknown, OutlookFailureCodes.Unknown, unchecked((int)0x80070005));
        Assert.Equal("Nieznany błąd Outlooka. (kod 0x80070005)", CorrespondenceText.FailureText(Polish, unknown));

        var foreign = FakeFailure(OutlookFailureKind.Busy, "outlook.somethingNew", null);
        Assert.Equal("Nieznany błąd Outlooka.", CorrespondenceText.FailureText(Polish, foreign));
    }

    private static OutlookFailure FakeFailure(OutlookFailureKind kind, string code, int? hresult) => new(kind, code, "send", hresult);

    private static HashSet<string> Placeholders(string value) =>
        PlaceholderRegex().Matches(value).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{\d+[^}]*\}")]
    private static partial Regex PlaceholderRegex();
}
