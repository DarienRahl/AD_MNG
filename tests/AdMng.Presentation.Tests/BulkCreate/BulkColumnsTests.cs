using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using AdMng.Testing.BulkCreate;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// Step 2 <em>Kolumny</em> (docs/dev/phase3-bulk-create.md 2.6, 2.11 Presentation B3): automatic mapping of Polish and English
/// headers, content-based suggestions, remembered mappings by header and by letter, LDAP and localized display names, manual
/// targets (any writable attribute of the class from the live schema), name order and case, the international phone offer,
/// blocked and password-like columns, duplicates, edits and kind changes, and the country table.
/// </summary>
public sealed class BulkColumnsTests
{
    [Fact]
    public async Task OperatorHeaders_MapWithoutAClick()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();

        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);

        Assert.Equal(
            [(ColumnTargetKind.Attribute, "mobile"), (ColumnTargetKind.Attribute, "givenName"), (ColumnTargetKind.Attribute, "sn"),
             (ColumnTargetKind.Attribute, "department"), (ColumnTargetKind.Attribute, "mail")],
            wizard.Columns.ToMappings().Select(m => (m.Kind, m.Attribute)));
        Assert.Null(wizard.Columns.MappingError);
        Assert.Equal("Telefon komórkowy [mobile]", wizard.Mapping("Telefon komórkowy").AttributeText);
        Assert.Equal(["+48 600 200 301", "+48 600 200 302", "+48 600 200 303"], wizard.Mapping("Telefon komórkowy").Samples);
    }

    [Fact]
    public async Task PolishAndEnglishHeaders_AreRecognized()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();

        await wizard.PasteAsync(ExcelSamples.PolishTsv);
        Assert.Equal("title", wizard.Mapping("Stanowisko").Attribute);
        Assert.Equal(ColumnTargetKind.Manager, wizard.Mapping("Przełożony").Target);
        Assert.Equal("mobile", wizard.Mapping("Telefon komórkowy").Attribute);

        wizard.Harness.Clipboard.Formatted = new Services.ClipboardContent(null, null, null, ExcelSamples.EnglishTsv);
        await wizard.Source.ReplaceWithPasteCommand.ExecuteAsync(null);
        Assert.Equal(
            ["givenName", "sn", "mail", "mobile", "department", "title"],
            wizard.Columns.ToMappings().Select(m => m.Attribute));
    }

    [Fact]
    public async Task Content_DecidesAmbiguousHeaders()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();

        await wizard.PasteAsync(ExcelSamples.Tsv(
            ["Imię", "Nazwisko", "Telefon", "Komórka"],
            ["Łucja", "Żak", "+48 600 100 201", "Księgowość"],
            ["Grzegorz", "Bąk", "+48 600 100 202", "Kadry"],
            ["Ewa", "Wiśniewska", "+48 512 345 678", "Sprzedaż"]));

        var phone = wizard.Mapping("Telefon");
        Assert.Equal("mobile", phone.Attribute);
        Assert.Equal(SuggestionNote.MobileNumbersDetected, phone.Note);
        Assert.Equal("rozpoznano numery komórkowe", phone.NoteText);
        Assert.Equal("department", wizard.Mapping("Komórka").Attribute);
    }

    [Fact]
    public async Task HeaderlessPaste_IsMappedByContent_AndByTheProfilesLetters()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();

        await wizard.PasteAsync(ExcelSamples.HeaderlessTsv);

        Assert.False(wizard.Source.Table.HasHeader);
        var mappings = wizard.Columns.ToMappings();
        Assert.Equal("mail", mappings[2].Attribute);
        Assert.Equal("mobile", mappings[3].Attribute);
        Assert.Equal("rozpoznano po zawartości", wizard.Mapping("C").NoteText);

        // A profile remembers a headerless table by letter (#A, #B).
        wizard.Columns.HeaderMappings =
        [
            new BulkCreateHeaderMapping("#A", ColumnTargetKind.Attribute, "givenName", NameOrder.GivenNameFirst, TextCase.Title),
            new BulkCreateHeaderMapping("#B", ColumnTargetKind.Attribute, "sn", NameOrder.GivenNameFirst, TextCase.AsIs),
            new BulkCreateHeaderMapping("#E", ColumnTargetKind.Attribute, "department", NameOrder.GivenNameFirst, TextCase.AsIs),
        ];
        wizard.Columns.AutoMapCommand.Execute(null);

        Assert.Equal(["givenName", "sn", "mail", "mobile", "department"], wizard.Columns.ToMappings().Select(m => m.Attribute));
        Assert.Equal(TextCase.Title, wizard.Mapping("A").Case);
        Assert.Equal("jak zapamiętano w profilu", wizard.Mapping("A").NoteText);
        Assert.Equal(["#A", "#B", "#C", "#D", "#E"], wizard.Columns.ToHeaderMappings().Select(m => m.Header));
    }

    [Fact]
    public async Task LdapAndDisplayNames_AreMapped()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();

        await wizard.PasteAsync(ExcelSamples.Tsv(
            ["Imię", "Nazwisko", "extensionAttribute5", "Numer identyfikatora [contosoBadgeNumber]", "Nazwa wyświetlana"],
            ["Łucja", "Żak", "A-1", "B-17", "Łucja Żak (Kadry)"]));

        Assert.Equal("extensionAttribute5", wizard.Mapping("extensionAttribute5").Attribute);
        Assert.Equal(FakeSchema.CustomBadgeAttribute, wizard.Mapping("Numer identyfikatora [contosoBadgeNumber]").Attribute);
        Assert.Equal("displayName", wizard.Mapping("Nazwa wyświetlana").Attribute);
    }

    [Fact]
    public async Task ManualTarget_AnyWritableAttributeOfTheClass_ButNoBlockedOne()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.Tsv(["Imię", "Nazwisko", "Numer szafki"], ["Łucja", "Żak", "S-17"]));
        var options = wizard.Columns.AttributeOptions.Select(o => o.LdapName).ToList();

        Assert.Contains("extensionAttribute5", options);
        Assert.Contains("extensionAttribute15", options);
        Assert.Contains(FakeSchema.CustomBadgeAttribute, options);
        Assert.Contains("mobile", options);
        Assert.Contains("cn", options);
        Assert.DoesNotContain("unicodePwd", options);
        Assert.DoesNotContain("memberOf", options);
        Assert.DoesNotContain("manager", options);
        Assert.DoesNotContain("objectSid", options);
        Assert.DoesNotContain("userAccountControl", options);

        var code = wizard.Mapping("Numer szafki");
        Assert.Equal(ColumnTargetKind.Ignore, code.Target);
        code.Target = ColumnTargetKind.Attribute;
        Assert.Equal("Kolumna „Numer szafki”: wybierz atrybut.", wizard.Columns.MappingError);
        Assert.False(wizard.Wizard.CanGoNext && wizard.Wizard.IsColumnsStep);

        code.AttributeText = "extensionAttribute5";
        Assert.Equal("extensionAttribute5", code.Attribute);
        Assert.Null(wizard.Columns.MappingError);

        code.AttributeText = "Telefon komórkowy [mobile]";
        Assert.Equal("mobile", code.Attribute);
        Assert.True(code.IsPhone);

        code.AttributeText = "memberOf";
        Assert.Equal("Kolumna „Numer szafki”: atrybutu memberOf nie można ustawić przy zakładaniu obiektów.", wizard.Columns.MappingError);
    }

    [Fact]
    public async Task FullName_OrderAndCase_GoToTheMapping()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.Tsv(["Pracownik", "E-mail"], ["ŻAK ŁUCJA", "lucja.zak@contoso.com"]));
        var person = wizard.Mapping("Pracownik");

        person.Target = ColumnTargetKind.FullName;
        person.Order = NameOrder.SurnameFirst;
        person.Case = TextCase.Title;

        var mapping = wizard.Columns.ToMappings()[0];
        Assert.Equal(new ColumnMapping(0, ColumnTargetKind.FullName, null, NameOrder.SurnameFirst, TextCase.Title), mapping);
        Assert.True(person.IsFullNameTarget);
        Assert.True(person.CanChooseCase);
        Assert.Null(wizard.Columns.MappingError);
    }

    [Fact]
    public async Task PhonesWithoutPlus_OfferTheInternationalFormat()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.Tsv(
            ["Imię", "Nazwisko", "Telefon komórkowy"],
            ["Łucja", "Żak", "48600100201"],
            ["Grzegorz", "Bąk", "48600100202"]));
        var phone = wizard.Mapping("Telefon komórkowy");

        Assert.Equal(SuggestionNote.PhonesWithoutPlus, phone.Note);
        Assert.True(phone.ShowsInternationalOffer);

        wizard.Columns.UseInternationalPhonesCommand.Execute(null);

        Assert.True(wizard.Columns.InternationalPhones);
        Assert.False(phone.ShowsInternationalOffer);
        Assert.Equal(PhoneFormat.International, wizard.Rules.Phones);
        Assert.Equal(PhoneFormat.International, wizard.Rules.ToRules().Phones);

        wizard.Rules.Phones = PhoneFormat.AsIs;
        Assert.False(wizard.Columns.InternationalPhones);
        Assert.True(phone.ShowsInternationalOffer);
    }

    [Fact]
    public async Task PasswordLikeAndIdentifierColumns_AreNotMapped()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.SensitiveColumnsTsv);
        var password = wizard.Mapping("Hasło startowe");

        Assert.True(password.IsPasswordLike);
        Assert.False(password.CanMap);
        Assert.Equal(ColumnTargetKind.Ignore, password.Target);
        Assert.Equal([BulkSourceViewModel.Mask, BulkSourceViewModel.Mask], password.Samples);
        Assert.Equal("wygląda na hasła – nigdy nie jest używana", password.NoteText);
        password.Target = ColumnTargetKind.Attribute;
        Assert.Equal(ColumnTargetKind.Ignore, password.Target);
        Assert.Equal(ColumnTargetKind.Ignore, wizard.Columns.ToMappings()[2].Kind);
        Assert.DoesNotContain(wizard.Columns.ToHeaderMappings(), m => m.Header.Contains("haslo", StringComparison.Ordinal));

        var pesel = wizard.Mapping("PESEL");
        Assert.Equal(ColumnTargetKind.Ignore, pesel.Target);
        Assert.StartsWith("identyfikator osobowy", pesel.NoteText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecondColumnForTheSameTarget_IsSkipped_AndAManualDuplicateIsAnError()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.Tsv(
            ["Imię", "Nazwisko", "E-mail", "Adres e-mail"],
            ["Łucja", "Żak", "lucja.zak@contoso.com", "l.zak@firma.pl"]));
        var second = wizard.Mapping("Adres e-mail");

        Assert.Equal("mail", wizard.Mapping("E-mail").Attribute);
        Assert.Equal(ColumnTargetKind.Ignore, second.Target);
        Assert.Equal("inna kolumna ma już to przeznaczenie – pominięto", second.NoteText);

        second.Target = ColumnTargetKind.Attribute;
        second.AttributeText = "mail";
        Assert.Equal("Kolumny „E-mail” i „Adres e-mail” mają to samo przeznaczenie – pomiń jedną z nich.", wizard.Columns.MappingError);
    }

    [Fact]
    public async Task EditsAndAppends_KeepTheOperatorsChoices_ANewTableIsMappedAgain()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        var department = wizard.Mapping("Dział");
        department.Target = ColumnTargetKind.PatternOnly;

        wizard.Source.Rows[0][3] = "Kadry";
        await wizard.PasteAsync(ExcelSamples.Tsv(
            ["Telefon komórkowy", "Imię", "Nazwisko", "Dział", "E-mail"],
            ["+48 600 200 306", "Jędrzej", "Łęcki", "Kadry", "jedrzej.lecki@firma.pl"]));

        Assert.Same(department, wizard.Mapping("Dział"));
        Assert.Equal(ColumnTargetKind.PatternOnly, wizard.Mapping("Dział").Target);

        wizard.Source.HeaderMode = HeaderMode.FirstRowIsHeader;
        await wizard.Source.PendingWork;
        Assert.Equal("department", wizard.Mapping("Dział").Attribute);
    }

    [Fact]
    public async Task KindChange_OffersTheKindsTargets()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.Tsv(["Nazwa", "Opis", "E-mail"], ["GG_Projekt_Ślęża", "Projekt Ślęża", "projekt.sleza@firma.pl"]));
        Assert.Contains(wizard.Columns.AvailableTargets, t => t.Value == ColumnTargetKind.FullName);

        wizard.Wizard.Kind = wizard.Wizard.Kinds.Single(k => k.Value == ObjectKind.Group);

        Assert.Equal(ObjectKind.Group, wizard.Columns.Kind);
        Assert.DoesNotContain(wizard.Columns.AvailableTargets, t => t.Value is ColumnTargetKind.FullName or ColumnTargetKind.Country or ColumnTargetKind.ExpiresAt);
        Assert.DoesNotContain(wizard.Columns.AttributeOptions, o => o.LdapName == "givenName");
        Assert.Equal("cn", wizard.Mapping("Nazwa").Attribute);
        Assert.Equal("description", wizard.Mapping("Opis").Attribute);
    }

    [Fact]
    public async Task CountryTable_KnowsCodesAndNamesInBothLanguages()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        var countries = wizard.Columns.CountryTable;

        Assert.Equal("PL", countries["polska"].Code);
        Assert.Equal("Polska", countries["polska"].Name);
        Assert.Equal(616, countries["pl"].NumericCode);
        Assert.Equal("PL", countries["poland"].Code);
        Assert.Equal("PL", countries["616"].Code);
        Assert.Equal("DE", countries[ColumnSynonyms.Fold("Niemcy")].Code);
        Assert.Equal("DE", countries[ColumnSynonyms.Fold("Germany")].Code);
        Assert.Same(countries, wizard.Wizard.BuildInput()?.Countries ?? countries);
    }
}
