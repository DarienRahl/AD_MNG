using System.Globalization;
using AdMng.Core.Messaging.Salutations;
using AdMng.Presentation.Localization;
using AdMng.Presentation.UserMessages;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>Every salutation text (U1d) exists in English and Polish; the Polish terms of the design note.</summary>
public sealed class SalutationLocalizationTests
{
    private static readonly ResxLocalizer English = new(CultureInfo.GetCultureInfo("en-US"));
    private static readonly ResxLocalizer Polish = new(CultureInfo.GetCultureInfo("pl-PL"));

    [Fact]
    public void EveryDynamicKey_ExistsInBothLanguages()
    {
        var keys = SalutationText.AllDynamicKeys().ToList();

        Assert.True(keys.Count >= 30);
        foreach (var key in keys)
        {
            Assert.True(English.Contains(key), key);
            Assert.False(English[key].StartsWith('['), key);
            Assert.False(Polish[key].StartsWith('['), key);
            Assert.NotEqual(English[key], Polish[key]);
        }
    }

    [Fact]
    public void IssueTexts_TakeTheirArgument()
    {
        foreach (var code in SalutationIssueCodes.All)
        {
            Assert.Contains("Kim", Polish.Format(UserMessageText.IssueKey(code), "Kim"), StringComparison.Ordinal);
            Assert.Contains("Kim", English.Format(UserMessageText.IssueKey(code), "Kim"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Polish_UsesTheTermsOfTheDesignNote()
    {
        Assert.Equal("Zwroty grzecznościowe", Polish[L.Salutations.Section]);
        Assert.Equal("Rozpoznawaj tytuł z nazwy wyświetlanej (np. prof. dr hab. Jan Kowalski)", Polish[L.Salutations.TitleFromDisplayName]);
        Assert.Equal("Przywróć domyślne", Polish[L.Salutations.ResetTitles]);
        Assert.Equal("Sprawdź", Polish[L.Salutations.Check]);
        Assert.Equal("Zwroty", Polish[L.Salutations.Group]);
        Assert.Equal("Używaj tytułów", Polish[L.Salutations.UseTitles]);
        Assert.Equal("Gdy płeć nieznana", Polish[L.Salutations.UnknownGender]);
        Assert.Equal("z ustawień", Polish[L.Salutations.UnknownGenderFromSettings]);
        Assert.Equal("Zwrot", Polish[L.Salutations.ColumnSalutation]);
        Assert.Equal("Pola wyliczone", Polish[L.Salutations.DerivedFields]);
        Assert.Equal("Zgadnięte zwroty", Polish[UserMessageText.FilterKey(UserMessagePreviewFilter.Guessed)]);
        Assert.Equal(
            ["automatycznie", "Pan", "Pani", "neutralnie"],
            Enum.GetValues<SalutationGenderChoice>().Select(c => Polish[SalutationText.ChoiceKey(c)]));
    }
}
