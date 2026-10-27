using System.Text.Json;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>The salutation settings (U1d): defaults, normalization of hand-edited files, JSON, the title table's "defaults".</summary>
public sealed class SalutationSettingsTests
{
    [Fact]
    public void Defaults_MatchTheDesignNote()
    {
        var settings = new AppSettings().UserMessages.Salutations;

        Assert.Equal("personalTitle", settings.TitleAttribute);
        Assert.True(settings.TitleFromDisplayName);
        Assert.Null(settings.GenderAttribute);
        Assert.Equal(["M", "male", "mężczyzna", "Pan", "Mr"], settings.MasculineValues);
        Assert.Equal(["K", "F", "W", "female", "kobieta", "Pani", "Ms", "Mrs"], settings.FeminineValues);
        Assert.Empty(settings.GivenNames);
        Assert.Null(settings.Titles);
        Assert.Same(SalutationData.DefaultTitles, settings.EffectiveTitles);
        Assert.Equal(UnknownGenderMode.Neutral, settings.UnknownGender);

        var options = settings.ToOptions();
        Assert.Same(TitleTable.Default, options.Titles);
        Assert.Equal("Szanowna Pani / Szanowny Panie", options.Neutral(MessageLanguage.Polish));
        Assert.Equal("Dear {name}", options.Neutral(MessageLanguage.English));
    }

    [Fact]
    public void Normalize_RepairsAHandEditedFile()
    {
        var settings = new AppSettings();
        var salutations = settings.UserMessages.Salutations;
        salutations.TitleAttribute = "title";
        salutations.GenderAttribute = "unicodePwd";
        salutations.MasculineValues = [" M ", "m", "", "Pan\r\n"];
        salutations.FeminineValues = null!;
        salutations.GivenNames =
        [
            new("Sasza", Gender.Masculine), new(" SASZA ", Gender.Feminine), new("Józefa", Gender.Masculine), new("Kim", Gender.Unknown), new("  ", Gender.Feminine),
        ];
        salutations.NeutralPolish = "  ";
        salutations.NeutralEnglish = "Hello\n{name}";
        salutations.UnknownGender = (UnknownGenderMode)7;

        settings.Normalize();

        Assert.Equal(string.Empty, salutations.TitleAttribute);
        Assert.Null(salutations.GenderAttribute);
        Assert.Equal(["M", "Pan"], salutations.MasculineValues);
        Assert.Empty(salutations.FeminineValues);
        Assert.Equal([new GivenNameGender("Sasza", Gender.Masculine), new GivenNameGender("Józefa", Gender.Masculine)], salutations.GivenNames);
        Assert.Null(salutations.NeutralPolish);
        Assert.Equal("Hello {name}", salutations.NeutralEnglish);
        Assert.Equal(UnknownGenderMode.Neutral, salutations.UnknownGender);
        Assert.Null(salutations.ToOptions().TitleAttribute);
    }

    [Fact]
    public void Normalize_AMissingSection_GetsTheDefaults()
    {
        var settings = new AppSettings();
        settings.UserMessages.Salutations = null!;

        settings.Normalize();

        Assert.Equal("personalTitle", settings.UserMessages.Salutations.TitleAttribute);
    }

    [Fact]
    public void TitleTable_EqualToTheDefaults_IsStoredAsNull_OtherwiseCleaned()
    {
        Assert.Null(SalutationSettings.NormalizeTitles([.. SalutationData.DefaultTitles]));
        Assert.Null(SalutationSettings.NormalizeTitles(null));

        var rows = SalutationSettings.NormalizeTitles(
        [
            .. SalutationData.DefaultTitles,
            new TitleRule { Title = " płk ", Variants = ["pułkownik", "płk", " ", "pułkownik"], Rank = 5000, PolishMasculine = "Panie Pułkowniku", PolishFeminine = " ", English = "Colonel" },
            new TitleRule { Title = "  ", Rank = 1 },
            new TitleRule { Title = "PŁK", Rank = 1 },
            new TitleRule { Title = new string('x', 300), Rank = -4 },
        ])!;

        Assert.Equal(SalutationData.DefaultTitles.Count + 2, rows.Count);
        var colonel = rows[^2];
        Assert.Equal(("płk", 1000, "Panie Pułkowniku", null, "Colonel"), (colonel.Title, colonel.Rank, colonel.PolishMasculine, colonel.PolishFeminine, colonel.English));
        Assert.Equal(["pułkownik"], colonel.Variants);
        Assert.Equal((TitleTable.MaxTextLength, 0), (rows[^1].Title.Length, rows[^1].Rank));
    }

    [Fact]
    public void Template_Salutation_IsNormalizedOnLoad()
    {
        var settings = new AppSettings();
        settings.UserMessages.Templates.Add(new MessageTemplate { Id = "a", Name = "Zwroty domyślne", Salutation = new TemplateSalutationOptions() });
        settings.UserMessages.Templates.Add(new MessageTemplate
        {
            Id = "b",
            Name = "Bez tytułów",
            Salutation = new TemplateSalutationOptions { UseTitles = false, NeutralPolish = "Szanowni Państwo\r\n" },
        });

        settings.Normalize();

        Assert.Null(settings.UserMessages.Templates[0].Salutation);
        Assert.Equal(new TemplateSalutationOptions { UseTitles = false, NeutralPolish = "Szanowni Państwo" }, settings.UserMessages.Templates[1].Salutation);
    }

    [Fact]
    public void Json_RoundTrip_KeepsEveryValue_AndAnEmptyTitleAttributeMeansNone()
    {
        var settings = new AppSettings();
        var salutations = settings.UserMessages.Salutations;
        salutations.TitleAttribute = string.Empty;
        salutations.GenderAttribute = "extensionAttribute5";
        salutations.GivenNames = [new("Sasza", Gender.Masculine)];
        salutations.Titles = [new TitleRule { Title = "płk", Variants = ["pułkownik"], Rank = 75, PolishMasculine = "Panie Pułkowniku", English = "Colonel" }];
        salutations.UnknownGender = UnknownGenderMode.Feminine;
        settings.UserMessages.Templates.Add(new MessageTemplate
        {
            Id = "t",
            Name = "Zwroty",
            Salutation = new TemplateSalutationOptions { UseTitles = false, UnknownGender = UnknownGenderMode.Masculine },
        });
        settings.Normalize();

        var json = JsonSerializer.Serialize(settings, JsonSettingsStore.SerializerOptions);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonSettingsStore.SerializerOptions)!;
        loaded.Normalize();

        var copy = loaded.UserMessages.Salutations;
        Assert.Contains("\"unknownGender\": \"Feminine\"", json, StringComparison.Ordinal);
        Assert.Equal(string.Empty, copy.TitleAttribute);
        Assert.Null(copy.ToOptions().TitleAttribute);
        Assert.Equal("extensionAttribute5", copy.GenderAttribute);
        Assert.Equal([new GivenNameGender("Sasza", Gender.Masculine)], copy.GivenNames);
        Assert.True(Assert.Single(copy.Titles!).SameAs(salutations.Titles[0]));
        Assert.Equal(new TemplateSalutationOptions { UseTitles = false, UnknownGender = UnknownGenderMode.Masculine }, loaded.UserMessages.Templates[0].Salutation);
    }
}
