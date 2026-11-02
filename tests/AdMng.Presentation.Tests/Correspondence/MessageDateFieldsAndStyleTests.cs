using System.Globalization;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// Date fields of the message page ("Dodaj datę": a date, an editable format, the sample, the error without a date), the
/// base style of the message ("Czcionka", "Rozmiar", "Interlinia"), both kept in the last draft and templates, and the
/// preview's warnings about the setup: a past date, <c>{{podpis}}</c> without a signature, a row field in the subject of
/// grouped messages.
/// </summary>
public sealed class MessageDateFieldsAndStyleTests
{
    [Fact]
    public async Task AddDateField_NeedsADate_ShowsTheSample_InTheChosenFormat()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;

        message.AddDateFieldCommand.Execute(null);

        var field = Assert.Single(message.CustomFields);
        Assert.True(field.IsDate);
        Assert.Equal(CustomFieldKind.Date, field.Kind);
        Assert.Null(field.Date);
        Assert.Equal(CustomFieldFormats.DefaultDate, field.Format);
        Assert.Equal(string.Empty, field.Sample);
        Assert.Equal(CustomFieldFormats.DateChoices, CustomFieldViewModel.FormatChoices);
        field.Name = "Termin";
        Assert.Equal("wybierz datę", field.Error);
        Assert.Contains("Pole własne „Termin”: wybierz datę", message.ValidateForSend());

        var changes = new List<string?>();
        field.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        field.Date = new DateTime(2026, 11, 15);

        Assert.Null(field.Error);
        Assert.Equal("15 listopada 2026r.", field.Sample);
        Assert.Contains(nameof(CustomFieldViewModel.Sample), changes);
        field.Format = "dddd, d MMMM yyyy";
        Assert.Equal("niedziela, 15 listopada 2026", field.Sample);
        field.Format = "d MMMM 'r";                                         // an unclosed quote: the default
        Assert.Equal("15 listopada 2026r.", field.Sample);
        field.Format = "dd.MM.yyyy";
        Assert.Contains("{{Termin}}", message.FieldChoices);
        Assert.Equal(new CustomFieldDefinition("Termin", "2026-11-15", CustomFieldKind.Date, "dd.MM.yyyy"), Assert.Single(message.Draft().CustomFields));

        message.Subject = "Odpowiedź do {{Termin}}";
        var rendered = CorrespondenceRenderer.Render(message.Draft(), new CorrespondenceUnit("anna.nowak@contoso.test", "Anna Nowak", [new Dictionary<string, string>()]), []);
        Assert.Equal("Odpowiedź do 15.11.2026", rendered.Subject);
    }

    [Fact]
    public async Task DateFieldsAndTheBaseStyle_AreKeptInTheLastDraft()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using (var window = await scenario.OpenAsync())
        {
            var message = window.Message;
            message.Subject = "Spotkanie";
            message.BodyHtml = "<p>Do {{Termin}}.</p>";
            message.AddDateFieldCommand.Execute(null);
            message.CustomFields[0].Name = "Termin";
            message.CustomFields[0].Date = new DateTime(2026, 12, 4);
            message.CustomFields[0].Format = "d MMMM yyyy";
            message.CustomFields.Add(new CustomFieldViewModel("Sala", "Aula Główna"));
            message.FontFamily = "Georgia,serif";
            message.FontSize = 10.5;
            message.LineHeight = "1.15";

            Assert.True(await window.CanCloseAsync());
        }

        var draft = scenario.Templates.LoadLastDraft()!;
        Assert.Equal([new CustomFieldDefinition("Termin", "2026-12-04", CustomFieldKind.Date, "d MMMM yyyy"), new CustomFieldDefinition("Sala", "Aula Główna")],
            draft.CustomFields);
        Assert.Equal(("Georgia,serif", 10.5, "1.15"), (draft.FontFamily, draft.FontSize!.Value, draft.LineHeight));

        using var reopened = await scenario.OpenAsync();
        var restored = reopened.Message;
        Assert.Equal(("Georgia,serif", 10.5, "1.15"), (restored.FontFamily, restored.FontSize, restored.LineHeight));
        var termin = restored.CustomFields[0];
        Assert.Equal((true, new DateTime(2026, 12, 4), "d MMMM yyyy", "4 grudnia 2026"), (termin.IsDate, termin.Date, termin.Format, termin.Sample));
        Assert.Equal(("Sala", "Aula Główna", false), (restored.CustomFields[1].Name, restored.CustomFields[1].Value, restored.CustomFields[1].IsDate));
    }

    [Fact]
    public async Task ATemplateWithADateField_LoadsIt_AndSavingKeepsIt()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        Assert.True(await message.LoadTemplateAsync(CorrespondenceTemplateLibrary.MailboxStillNeededId));
        message.CustomFields[0].Date = new DateTime(2026, 11, 30);
        message.TemplateName = "Weryfikacja – Wydział Łączności";

        await message.SaveTemplateCommand.ExecuteAsync(null);

        var saved = Assert.Single(scenario.Templates.Saved);
        Assert.Equal([new CustomFieldDefinition("Termin", "2026-11-30", CustomFieldKind.Date, CustomFieldFormats.DefaultDate)], saved.CustomFields);
        Assert.Equal(new MessageBaseStyle("Aptos,sans-serif", 12, "normal"), CorrespondenceTemplateLibrary.StyleOf(saved));
        Assert.Equal((false, false), (saved.VotingEnabled, saved.GroupByAddress));
    }

    [Fact]
    public async Task ATemplateWithoutFontValues_KeepsTheWindowsStyle()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        var older = await scenario.Templates.SaveAsync(new CorrespondenceTemplate
        {
            Name = "Zaproszenie na szkolenie – Łódź",
            Subject = "Zaproszenie na szkolenie",
            BodyHtml = "<p>{{zwrot}},</p><p>zapraszamy na szkolenie.</p>",
        });
        Assert.Null(CorrespondenceTemplateLibrary.StyleOf(older));                // saved before the fonts existed
        using var window = await scenario.OpenAsync();
        var message = window.Message;
        var chosen = new MessageBaseStyle("Georgia,serif", 14, "1.5");
        message.ApplyBaseStyle(chosen);

        Assert.True(await message.LoadTemplateAsync(older.Id));

        Assert.Equal("<p>{{zwrot}},</p><p>zapraszamy na szkolenie.</p>", message.BodyHtml);
        Assert.Equal(chosen, message.BaseStyle);                                   // the window's values, not the default
        Assert.Equal(chosen, message.Draft().BaseStyle);
        scenario.Ui.ConfirmAnswers.Enqueue(true);                                  // replace the body loaded above
        Assert.True(await message.LoadTemplateAsync(CorrespondenceTemplateLibrary.MailboxStillNeededId));
        Assert.Equal(CorrespondenceTemplateLibrary.MailboxStyle, message.BaseStyle); // a template's own values win
    }

    [Fact]
    public async Task BaseStyle_DefaultsToTheEarlierLook_TheChoicesHoldEveryValue()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var message = window.Message;

        Assert.Equal(MessageBaseStyle.Default, message.BaseStyle);
        Assert.Equal(MessageBaseStyle.Default, message.Draft().BaseStyle);
        Assert.Equal(["Calibri", "Aptos", "Arial", "Segoe UI", "Verdana", "Tahoma", "Times New Roman", "Georgia"], message.FontFamilyChoices.Select(c => c.Text));
        Assert.Contains(message.FontFamilyChoices, c => c.Value == message.FontFamily);
        Assert.Equal(["9 pkt", "10 pkt", "10,5 pkt", "11 pkt", "12 pkt", "14 pkt", "16 pkt"], message.FontSizeChoices.Select(c => c.Text));
        Assert.Equal([("normal", "Pojedyncza"), ("1.15", "1,15"), ("1.4", "1,4"), ("1.5", "1,5"), ("2", "Podwójna")],
            message.LineHeightChoices.Select(c => (c.Value, c.Text)));

        // A template's own values join the choices, so the combo boxes find them.
        message.ApplyBaseStyle(new MessageBaseStyle("\"Book Antiqua\", serif", 13, "1.3"));
        Assert.Equal(("'Book Antiqua',serif", 13d, "1.3"), (message.FontFamily, message.FontSize, message.LineHeight));
        Assert.Equal(("'Book Antiqua',serif", "Book Antiqua"), (message.FontFamilyChoices[^1].Value, message.FontFamilyChoices[^1].Text));
        Assert.Contains(message.FontSizeChoices, c => c.Value == 13 && c.Text == "13 pkt");
        Assert.Equal(("1.3", "1,3"), (message.LineHeightChoices[^1].Value, message.LineHeightChoices[^1].Text));

        // A value the message cannot use (a combo box without a match, a typed one) goes back to the previous one.
        message.FontFamily = null!;
        message.FontSize = 0;
        message.LineHeight = "wysoka";
        Assert.Equal(("'Book Antiqua',serif", 13d, "1.3"), (message.FontFamily, message.FontSize, message.LineHeight));
    }

    [Fact]
    public async Task Choices_FollowTheWindowsLanguage()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        using var window = await scenario.OpenAsync();

        Assert.Equal("10.5 pt", window.Message.FontSizeChoices.Single(c => c.Value == 10.5).Text);
        Assert.Equal(["Single", "1.15", "1.4", "1.5", "Double"], window.Message.LineHeightChoices.Select(c => c.Text));
        window.Message.AddDateFieldCommand.Execute(null);
        window.Message.CustomFields[0].Name = "Termin";
        Assert.Equal("choose a date", window.Message.CustomFields[0].Error);
    }

    [Fact]
    public async Task Preview_WarnsOfAPastDate_ASignatureFieldWithoutASignature_AndARowFieldInAGroupedSubject()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        CorrespondencePagesScenario.AddRows(window,
            CorrespondencePagesScenario.Row("jan.kowalski@contoso.test", "Jan Kowalski", ("Skrzynka", "Sekretariat"), ("Wydział", "Łączności")),
            CorrespondencePagesScenario.Row("jan.kowalski@contoso.test", "Jan Kowalski", ("Skrzynka", "Rekrutacja"), ("Wydział", "Łączności")));
        var message = window.Message;
        message.Subject = "Weryfikacja skrzynki pocztowej: {{Skrzynka}} ({{zwrot}}, {{Termin}}, {{liczba}})";
        message.BodyHtml = "<p>Prosimy o odpowiedź do {{Termin}}.</p><p>{{podpis}}</p>";
        message.AddDateFieldCommand.Execute(null);
        message.CustomFields[0].Name = "Termin";
        message.CustomFields[0].Date = new DateTime(2026, 10, 1);          // the test clock says 13 October 2026
        message.AddDateFieldCommand.Execute(null);
        message.CustomFields[1].Name = "Nieużywany";
        message.CustomFields[1].Date = new DateTime(2026, 1, 1);           // not in the message: no warning
        message.GroupByAddress = true;

        await window.Preview.RefreshCommand.ExecuteAsync(null);

        var warnings = window.Preview.Warnings;
        Assert.Contains("Pole {{Termin}} ma datę z przeszłości: 1 października 2026r. – odbiorcy zobaczą termin, który już minął.", warnings);
        Assert.DoesNotContain(warnings, w => w.Contains("{{Nieużywany}}", StringComparison.Ordinal));
        Assert.Contains("Treść ma pole {{podpis}}, ale nie wybrano podpisu – w tym miejscu nic się nie pojawi.", warnings);
        Assert.Contains("Wiadomości są grupowane po adresie, a temat używa pola {{Skrzynka}} – w temacie będzie wartość pierwszego wiersza grupy.", warnings);
        Assert.DoesNotContain(warnings, w => w.Contains("{{podpis}} jest puste", StringComparison.Ordinal));

        // A date from today, a chosen signature and no grouping: none of the three.
        message.CustomFields[0].Date = new DateTime(2026, 10, 13);
        message.SelectedSignature = message.Signatures.Single(s => s.Name == CorrespondencePagesScenario.ParsingSignatures.Secretariat);
        await (message.SignatureLoading ?? Task.CompletedTask);
        message.GroupByAddress = false;
        await window.Preview.RefreshCommand.ExecuteAsync(null);

        Assert.DoesNotContain(window.Preview.Warnings, w => w.Contains("przeszłości", StringComparison.Ordinal)
            || w.Contains("nie wybrano podpisu", StringComparison.Ordinal) || w.Contains("grupowane", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_GroupedSubject_BuiltInsAndCustomFieldsAreNoRowFields_NorIsBcc()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        using var window = await scenario.OpenAsync();
        CorrespondencePagesScenario.AddRows(window,
            CorrespondencePagesScenario.Row("jan.kowalski@contoso.test", "Jan Kowalski", ("Skrzynka", "Sekretariat"), ("Termin", "jutro")));
        var message = window.Message;
        message.Subject = "{{zwrot}} {{nazwa}} {{email}} {{liczba}} {{Termin}}";
        message.BodyHtml = "<p>Treść</p>";
        message.CustomFields.Add(new CustomFieldViewModel("Termin", "15 listopada"));   // a custom field wins over the column
        message.GroupByAddress = true;

        await window.Preview.RefreshCommand.ExecuteAsync(null);
        Assert.DoesNotContain(window.Preview.Warnings, w => w.Contains("grouped", StringComparison.Ordinal));

        message.Subject = "{{Skrzynka}}";
        message.SendMode = message.SendModeChoices.Single(m => m.Value == CorrespondenceSendMode.Bcc);
        await window.Preview.RefreshCommand.ExecuteAsync(null);
        Assert.DoesNotContain(window.Preview.Warnings, w => w.Contains("grouped", StringComparison.Ordinal));

        message.SendMode = message.SendModeChoices.Single(m => m.Value == CorrespondenceSendMode.Individual);
        await window.Preview.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("The messages are grouped by address and the subject uses {{Skrzynka}} – the subject will have the value of the group's first row.",
            window.Preview.Warnings);
    }
}
