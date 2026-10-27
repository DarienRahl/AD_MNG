using System.Globalization;
using AdMng.Core.Messaging.Salutations;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using static AdMng.Presentation.Tests.UserMessages.MessageScenario;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>The "Zwroty grzecznościowe" section of the settings (U1d): fields, grids, validation, saving, the check box.</summary>
public sealed class SalutationSettingsViewModelTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static SalutationSettingsViewModel Create(SalutationSettings? settings = null, CultureInfo? culture = null) =>
        new(new ResxLocalizer(culture ?? Polish), settings ?? new SalutationSettings());

    [Fact]
    public void Opens_WithTheSettings_AndTheDefaultTable()
    {
        var section = Create();

        Assert.Equal("personalTitle", section.TitleAttribute);
        Assert.True(section.TitleFromDisplayName);
        Assert.Equal("M, male, mężczyzna, Pan, Mr", section.MasculineValuesText);
        Assert.Equal("Szanowna Pani / Szanowny Panie", section.NeutralPolish);
        Assert.Equal("Dear {name}", section.NeutralEnglish);
        Assert.Equal(11, section.Titles.Count);
        Assert.Equal(("prof.", "prof, profesor, prof. zw., prof. nadzw., prof. uczelni, prof. ucz.", 100), (section.Titles[3].Title, section.Titles[3].Variants, section.Titles[3].Rank));
        Assert.Equal(["zwrot neutralny i ostrzeżenie", "jak do mężczyzny (Pan)", "jak do kobiety (Pani)"], section.UnknownGenderModes.Select(m => m.Text));
        Assert.Equal("Używany, gdy płeć jest nieznana; {name} = imię i nazwisko.", section.NeutralHint);
        Assert.Null(section.Error);
    }

    [Fact]
    public void CheckBox_ShowsTitleGenderAndBothSalutations_ForTypedValues()
    {
        var section = Create();

        section.CheckDisplayName = "dr hab. Ewa Zielińska, prof. UW";
        section.CheckGivenName = "Ewa";
        section.CheckSurname = "Zielińska";

        Assert.Equal("Tytuł: dr hab., prof. UW (nazwa wyświetlana)", section.CheckTitleResult);
        Assert.Equal("Płeć: kobieta (Pani) (imię (reguła) · Ewa · zgadnięte)", section.CheckGenderResult);
        Assert.Equal("Szanowna Pani Profesor", section.CheckPolish);
        Assert.Equal("Dear Professor Zielińska", section.CheckEnglish);

        section.CheckDisplayName = string.Empty;
        section.CheckTitle = "ks. dr";
        section.CheckGivenName = "Tomasz";
        section.CheckSurname = "Wójcik";
        Assert.Equal("Szanowny Księże Doktorze", section.CheckPolish);
        Assert.Equal("Dear Father Wójcik", section.CheckEnglish);

        // The Polish name rule applies to Polish messages only.
        section.CheckTitle = string.Empty;
        section.CheckGivenName = "John";
        section.CheckSurname = "Smith";
        Assert.Equal("Szanowny Panie", section.CheckPolish);
        Assert.Equal("Dear John Smith", section.CheckEnglish);
    }

    [Fact]
    public void CheckBox_FollowsTheUnsavedGridsAndOptions()
    {
        var section = Create();
        section.CheckGivenName = "Sasza";
        section.CheckSurname = "Kim";
        Assert.Equal("Szanowna Pani / Szanowny Panie", section.CheckPolish);
        Assert.Equal("Płeć: nieznana (brak)", section.CheckGenderResult);

        section.AddGivenNameCommand.Execute(null);
        section.GivenNames[0].Name = "Sasza";
        section.GivenNames[0].Gender = Gender.Masculine;
        Assert.Equal("Szanowny Panie", section.CheckPolish);
        Assert.Equal("Płeć: mężczyzna (Pan) (imiona w ustawieniach · Sasza)", section.CheckGenderResult);

        section.RemoveGivenNameCommand.Execute(null);
        section.UnknownGender = section.UnknownGenderModes.Single(m => m.Value == UnknownGenderMode.Feminine);
        Assert.Equal("Szanowna Pani", section.CheckPolish);
        Assert.Equal("Płeć: kobieta (Pani) (zastępczo (ustawienia lub szablon) · zgadnięte)", section.CheckGenderResult);

        section.UnknownGender = section.UnknownGenderModes[0];
        section.NeutralPolish = "Szanowni Państwo";
        Assert.Equal("Szanowni Państwo", section.CheckPolish);

        // A new title row is used at once.
        section.CheckTitle = "płk";
        section.AddTitleCommand.Execute(null);
        var row = Assert.IsType<TitleRowViewModel>(section.SelectedTitle);
        row.Title = "płk";
        row.Variants = "pułkownik";
        row.Rank = 75;
        row.PolishMasculine = "Panie Pułkowniku";
        row.English = "Colonel";
        Assert.Equal("Tytuł: płk (atrybut tytułu)", section.CheckTitleResult);
        Assert.Equal("Dear Colonel Kim", section.CheckEnglish);
    }

    [Fact]
    public void Validation_AttributesAndRanks()
    {
        var section = Create(culture: CultureInfo.GetCultureInfo("en-US"));

        section.TitleAttribute = "title";
        Assert.Equal("Title attribute: not a valid attribute name, or an attribute not allowed in messages.", section.Error);
        section.TitleAttribute = string.Empty;
        section.GenderAttribute = "user Password";
        Assert.Equal("Gender attribute: not a valid attribute name, or an attribute not allowed in messages.", section.Error);
        section.GenderAttribute = "extensionAttribute5";
        Assert.Null(section.Error);
        section.Titles[0].Rank = 1001;
        Assert.Equal("Title table: the rank must be between 0 and 1000.", section.Error);
        section.Titles[0].Rank = 120;
        Assert.Null(section.Error);
    }

    [Fact]
    public void ToSettings_KeepsDefaultsAsNull_AndResetRestoresThem()
    {
        var section = Create();
        Assert.Null(section.ToSettings().Titles);
        Assert.Null(section.ToSettings().NeutralPolish);

        section.SelectedTitle = section.Titles[^1];
        section.RemoveTitleCommand.Execute(null);
        section.MasculineValuesText = "M; Pan\nmężczyzna";
        var changed = section.ToSettings();
        Assert.Equal(10, changed.Titles!.Count);
        Assert.Equal(["M", "Pan", "mężczyzna"], changed.MasculineValues);

        section.ResetTitlesCommand.Execute(null);
        Assert.Equal(11, section.Titles.Count);
        Assert.Null(section.ToSettings().Titles);
    }

    [Fact]
    public async Task SettingsWindow_SavesTheSection_AndAnErrorBlocksSaving()
    {
        var scenario = await CreateAsync();
        var settings = scenario.Factory.CreateSettings();
        var tab = Assert.IsType<UserMessageSettingsViewModel>(settings.Messages);
        var section = tab.Salutations;

        section.GenderAttribute = "unicodePwd";
        Assert.Equal("Gender attribute: not a valid attribute name, or an attribute not allowed in messages.", settings.Error);
        Assert.False(settings.SaveCommand.CanExecute(null));

        section.GenderAttribute = "extensionAttribute5";
        section.TitleFromDisplayName = false;
        section.AddGivenNameCommand.Execute(null);
        section.GivenNames[0].Name = "Sasza";
        section.GivenNames[0].Gender = Gender.Masculine;
        section.UnknownGender = section.UnknownGenderModes.Single(m => m.Value == UnknownGenderMode.Masculine);
        Assert.Null(settings.Error);

        await settings.SaveCommand.ExecuteAsync(null);

        var saved = scenario.Harness.Settings.Current.UserMessages.Salutations;
        Assert.Equal("extensionAttribute5", saved.GenderAttribute);
        Assert.False(saved.TitleFromDisplayName);
        Assert.Equal([new GivenNameGender("Sasza", Gender.Masculine)], saved.GivenNames);
        Assert.Equal(UnknownGenderMode.Masculine, saved.UnknownGender);
        Assert.Null(saved.Titles);
        Assert.Equal("personalTitle", saved.TitleAttribute);
    }
}
