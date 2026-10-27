using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Salutations;
using AdMng.Presentation.UserMessages;
using AdMng.Testing;
using static AdMng.Presentation.Tests.UserMessages.MessageScenario;

namespace AdMng.Presentation.Tests.UserMessages;

/// <summary>
/// Salutations in the message window (U1d) over the fake DC with the salutation samples: the "Zwrot" column with its marks,
/// the derived fields, the "Zgadnięte zwroty" filter, the gender override of one message (memory only), the template's
/// "Zwroty" options and manager mode.
/// </summary>
public sealed class UserMessageSalutationComposerTests
{
    private const string PolishBody = "{$salutation},\nprosimy, aby {$honorific} {$byGender:zmienił/zmieniła} hasło.";

    private static async Task<(MessageScenario Scenario, SalutationSample Sample, UserMessageComposerViewModel Composer)> OpenAsync(string body = PolishBody)
    {
        var scenario = await CreateAsync();
        var sample = scenario.Harness.Builder.AddSalutationSamples(scenario.Company);
        var composer = await scenario.OpenAsync(sample.All, BuiltInMessageTemplates.Custom);
        composer.Subject = "Zmiana hasła";
        composer.Body = body;
        return (scenario, sample, composer);
    }

    [Fact]
    public async Task Preview_ShowsTheSalutationColumn_WithGuessAndFallbackMarks()
    {
        var (_, _, composer) = await OpenAsync();

        await composer.PlanCommand.ExecuteAsync(null);

        Assert.Null(composer.Error);
        Assert.Equal(10, composer.AllItems.Count);
        Assert.Equal("≈ Szanowny Panie Profesorze", Item(composer, "Stanisław Kowalczyk").SalutationColumnText);
        Assert.Equal("≈ Szanowna Pani Doktor", Item(composer, "dr Anna Nowak").SalutationColumnText);
        Assert.Equal("≈ Szanowny Panie", Item(composer, "Piotr Wiśniewski").SalutationColumnText);
        Assert.Equal("≈ Szanowna Pani Profesor", Item(composer, "dr hab. Ewa Zielińska, prof. UW").SalutationColumnText);
        Assert.Equal("≈ Szanowny Księże Doktorze", Item(composer, "Tomasz Wójcik").SalutationColumnText);
        Assert.Equal("≈ Szanowny Panie", Item(composer, "Kuba Mazur").SalutationColumnText);
        Assert.Equal("≈ Szanowna Pani", Item(composer, "Sasza Kamińska").SalutationColumnText);
        Assert.Equal("Szanowna Pani", Item(composer, "Nikola Lewandowski").SalutationColumnText);

        var kim = Item(composer, "Sasza Kim");
        Assert.Equal("⚠ Szanowna Pani / Szanowny Panie", kim.SalutationColumnText);
        Assert.Equal(PlannedMessageStatus.Warning, kim.Status);
        Assert.Contains("Unknown gender (Sasza Kim) – the neutral salutation is used.", kim.IssuesText, StringComparison.Ordinal);
        Assert.StartsWith("Szanowna Pani / Szanowny Panie,\nprosimy, aby Pan/Pani zmienił/zmieniła hasło.", kim.TextBody, StringComparison.Ordinal);

        var kuba = Item(composer, "Kuba Mazur");
        Assert.Equal("given name (exception list) · guessed", kuba.SalutationToolTip);
        Assert.Equal(
            [
                new DerivedValueRow("Title", "(none)", "none", string.Empty),
                new DerivedValueRow("Gender", "man (Mr)", "given name (exception list) · Kuba · guessed", "≈"),
                new DerivedValueRow("Salutation", "Szanowny Panie", "given name (exception list) · guessed", "≈"),
            ],
            kuba.DerivedRows);
        Assert.Contains("Title dr recognized in the display name.", Item(composer, "dr Anna Nowak").IssuesText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuessedFilter_ListsGuessedAndFallbackSalutations()
    {
        var (_, _, composer) = await OpenAsync();
        await composer.PlanCommand.ExecuteAsync(null);

        composer.Filter = composer.Filters.Single(f => f.Value == UserMessagePreviewFilter.Guessed);

        Assert.Equal("Guessed salutations", composer.Filter.Text);
        // Not listed: Nikola (the honorific of personalTitle is certain) and Emily (English text without salutation fields).
        Assert.Equal(8, composer.Items.Count);
        Assert.DoesNotContain(composer.Items, i => i.Name == "Nikola Lewandowski");
        Assert.Equal(string.Empty, Item(composer, "Emily Brown").SalutationColumnText);
        Assert.False(Item(composer, "Emily Brown").HasDerived);
    }

    [Fact]
    public async Task ColumnAndFilter_FollowTheFieldsTheTemplateUses()
    {
        // Only {$honorific} / {$byGender}: no salutation, but a neutral "Pan/Pani" is marked in the column.
        var (_, _, composer) = await OpenAsync("Prosimy, aby {$honorific} {$byGender:zmienił/zmieniła} hasło.");
        await composer.PlanCommand.ExecuteAsync(null);

        Assert.Equal("⚠", Item(composer, "Sasza Kim").SalutationColumnText);
        Assert.Equal("fallback (settings or template)", Item(composer, "Sasza Kim").SalutationToolTip);
        Assert.Equal("≈", Item(composer, "Kuba Mazur").SalutationColumnText);
        Assert.Equal(string.Empty, Item(composer, "Nikola Lewandowski").SalutationColumnText);

        // Only {$titledName}: nothing depends on the gender – Sasza Kim is no guessed salutation.
        composer.Body = "{$titledName},";
        await composer.PlanCommand.ExecuteAsync(null);
        composer.Filter = composer.Filters.Single(f => f.Value == UserMessagePreviewFilter.Guessed);

        Assert.DoesNotContain(composer.Items, i => i.Name == "Sasza Kim");
        Assert.Equal(string.Empty, Item(composer, "Sasza Kim").SalutationColumnText);
        Assert.Contains(composer.Items, i => i.Name == "dr Anna Nowak");
    }

    [Fact]
    public async Task GenderOverride_ReplansThatMessageOnly_AndNeverTouchesTheSettings()
    {
        var (scenario, sample, composer) = await OpenAsync();
        await composer.PlanCommand.ExecuteAsync(null);
        var saves = scenario.Harness.Settings.SaveCount;
        var others = composer.AllItems.Where(i => i.Name != "Sasza Kim").ToList();
        var kim = Item(composer, "Sasza Kim");
        composer.SelectedItem = kim;

        kim.GenderChoice = SalutationGenderChoice.Feminine;

        var replanned = Item(composer, "Sasza Kim");
        Assert.NotSame(kim, replanned);
        Assert.Same(replanned, composer.SelectedItem);
        Assert.Contains(replanned, composer.Items);
        Assert.Equal(SalutationGenderChoice.Feminine, replanned.GenderChoice);
        Assert.Equal(PlannedMessageStatus.Ready, replanned.Status);
        Assert.Equal("Szanowna Pani", replanned.SalutationColumnText);
        Assert.StartsWith("Szanowna Pani,\nprosimy, aby Pani zmieniła hasło.", replanned.TextBody, StringComparison.Ordinal);
        Assert.Contains(replanned.DerivedRows, r => r is { Label: "Gender", Value: "woman (Ms)", Source: "chosen for this send" });
        Assert.All(others, o => Assert.Contains(o, composer.AllItems));
        Assert.Equal(Gender.Feminine, composer.GenderOverrides[sample.SaszaKim]);
        Assert.Contains("With warnings: 0", composer.SummaryText, StringComparison.Ordinal);

        // Neutral on purpose: no warning; automatic again: the warning is back.
        replanned.GenderChoice = SalutationGenderChoice.Neutral;
        var neutral = Item(composer, "Sasza Kim");
        Assert.Equal(PlannedMessageStatus.Ready, neutral.Status);
        Assert.Equal("Szanowna Pani / Szanowny Panie", neutral.SalutationColumnText);
        neutral.GenderChoice = SalutationGenderChoice.Automatic;
        Assert.Equal(PlannedMessageStatus.Warning, Item(composer, "Sasza Kim").Status);
        Assert.Empty(composer.GenderOverrides);

        Assert.Equal(saves, scenario.Harness.Settings.SaveCount);
        Assert.Empty(scenario.Harness.Settings.Current.UserMessages.Salutations.GivenNames);

        // A new plan starts without overrides.
        Item(composer, "Sasza Kim").GenderChoice = SalutationGenderChoice.Masculine;
        await composer.PlanCommand.ExecuteAsync(null);
        Assert.Empty(composer.GenderOverrides);
        Assert.Equal(SalutationGenderChoice.Automatic, Item(composer, "Sasza Kim").GenderChoice);
    }

    [Fact]
    public async Task GenderOverride_KeepsAnExclusion_AndTheSendUsesTheReplannedMessage()
    {
        var (scenario, _, composer) = await OpenAsync();
        await composer.PlanCommand.ExecuteAsync(null);
        var kim = Item(composer, "Sasza Kim");
        kim.IsIncluded = false;

        kim.GenderChoice = SalutationGenderChoice.Masculine;

        var replanned = Item(composer, "Sasza Kim");
        Assert.False(replanned.IsIncluded);
        replanned.IsIncluded = true;
        scenario.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        scenario.Harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        await composer.SendCommand.ExecuteAsync(null);

        var sent = Assert.Single(scenario.Transport.Accepted, m => m.To[0].Address == "sasza.kim@contoso.test");
        Assert.StartsWith("Szanowny Panie,\nprosimy, aby Pan zmienił hasło.", sent.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZwrotyGroup_EditsTheTemplateOptions_AndSavingKeepsThem()
    {
        var (scenario, _, composer) = await OpenAsync();
        Assert.True(composer.UseTitles);
        Assert.Null(composer.TemplateUnknownGender.Value);
        Assert.Equal(["as in the settings", "neutral salutation and a warning", "as to a man (Mr)", "as to a woman (Ms)"], composer.UnknownGenderChoices.Select(c => c.Text));

        composer.UseTitles = false;
        composer.TemplateUnknownGender = composer.UnknownGenderChoices.Single(c => c.Value == UnknownGenderMode.Feminine);
        await composer.PlanCommand.ExecuteAsync(null);

        Assert.Equal("≈ Szanowny Panie", Item(composer, "Stanisław Kowalczyk").SalutationColumnText);
        var kim = Item(composer, "Sasza Kim");
        Assert.Equal("⚠ Szanowna Pani", kim.SalutationColumnText);
        Assert.Equal(PlannedMessageStatus.Warning, kim.Status);
        Assert.Contains("Unknown gender (Sasza Kim) – the default gender of the settings or the template is used.", kim.IssuesText, StringComparison.Ordinal);

        composer.TemplateName = "Hasło – zwroty";
        await composer.SaveTemplateCommand.ExecuteAsync(null);
        var stored = Assert.Single(scenario.Harness.Settings.Current.UserMessages.Templates);
        Assert.Equal(new TemplateSalutationOptions { UseTitles = false, UnknownGender = UnknownGenderMode.Feminine }, stored.Salutation);

        // Another template shows its own options; back to the stored one shows them again.
        composer.SelectedTemplate = composer.Templates.Single(t => t.Value == BuiltInMessageTemplates.PasswordExpiring);
        Assert.True(composer.UseTitles);
        Assert.Null(composer.TemplateUnknownGender.Value);
        composer.SelectedTemplate = composer.Templates.Single(t => t.Value == stored.Id);
        Assert.False(composer.UseTitles);
        Assert.Equal(UnknownGenderMode.Feminine, composer.TemplateUnknownGender.Value);
    }

    [Fact]
    public async Task ManagerMode_ShowsTheManagersSalutation()
    {
        var (_, sample, composer) = await OpenAsync("{$managerSalutation},\n{$titledName} nie {$byGender:zmienił/zmieniła/zmienił(a)} hasła.");
        composer.RecipientMode = composer.RecipientModes.Single(m => m.Value == MessageRecipientMode.Manager);
        composer.Recipients.Clear();
        composer.Recipients.Add(new UserMessageRecipientViewModel(sample.SaszaKim));

        await composer.PlanCommand.ExecuteAsync(null);

        var kim = Item(composer, "Sasza Kim");
        Assert.Contains("stanislaw.kowalczyk@contoso.test", kim.ToText, StringComparison.Ordinal);
        Assert.StartsWith("Szanowny Panie Profesorze,\nSasza Kim nie zmienił(a) hasła.", kim.TextBody, StringComparison.Ordinal);
        Assert.Equal("≈ Szanowny Panie Profesorze", kim.SalutationColumnText);
        Assert.Equal(
            ["Title", "Gender", "Manager's title", "Manager's gender", "Salutation of the manager"],
            kim.DerivedRows.Select(r => r.Label));
        Assert.True(kim.HasAccountDerived);
        Assert.True(kim.HasManagerDerived);

        // The manager's selector changes the manager's salutation; the account's {$byGender} stays.
        composer.SelectedItem = kim;
        kim.ManagerGenderChoice = SalutationGenderChoice.Feminine;

        var replanned = Item(composer, "Sasza Kim");
        Assert.NotSame(kim, replanned);
        Assert.Equal(SalutationGenderChoice.Feminine, replanned.ManagerGenderChoice);
        Assert.Equal(SalutationGenderChoice.Automatic, replanned.GenderChoice);
        Assert.Equal("Szanowna Pani Profesor", replanned.SalutationColumnText);
        Assert.StartsWith("Szanowna Pani Profesor,\nSasza Kim nie zmienił(a) hasła.", replanned.TextBody, StringComparison.Ordinal);
        Assert.Equal(Gender.Feminine, composer.ManagerGenderOverrides[sample.SaszaKim]);
        Assert.Empty(composer.GenderOverrides);

        // The account's selector changes the account's text, the manager's choice stays.
        replanned.GenderChoice = SalutationGenderChoice.Masculine;
        var both = Item(composer, "Sasza Kim");
        Assert.StartsWith("Szanowna Pani Profesor,\nSasza Kim nie zmienił hasła.", both.TextBody, StringComparison.Ordinal);
        Assert.Equal(SalutationGenderChoice.Feminine, both.ManagerGenderChoice);
    }

    [Fact]
    public async Task EnglishText_UsesEnglishForms()
    {
        var (_, _, composer) = await OpenAsync();
        composer.EditLanguage = composer.Languages.Single(l => l.Value == MessageLanguage.English);
        composer.Subject = "Password";
        composer.Body = "{$salutation},\nplease change {$byGender:his/her/your} password.";

        await composer.PlanCommand.ExecuteAsync(null);

        var emily = Item(composer, "Emily Brown");
        Assert.Equal("EN", emily.LanguageText);
        Assert.Equal("≈ Dear Ms Brown", emily.SalutationColumnText);
        Assert.StartsWith("Dear Ms Brown,\nplease change her password.", emily.TextBody, StringComparison.Ordinal);
    }
}
