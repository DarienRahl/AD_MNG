using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Testing.BulkCreate;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// Step 3 <em>Nazwy i opcje</em> (docs/dev/phase3-bulk-create.md 2.6, 2.11 Presentation B3): the default rules, the
/// <em>Numeracja</em> panel rewriting every <c>{$nr}</c>, the first free number, live samples (three rows and the longest,
/// marked when changed or shortened), localized pattern errors with position and suggestion, the literal quick fix,
/// <em>Wstaw pole</em>, the column precedence, UPN suffixes and the mail domain, account options and their validation, the
/// shared password boxes, groups, the kind and the session's end.
/// </summary>
public sealed class BulkRulesTests
{
    [Fact]
    public async Task Standard_IsTheDefault_WithNameBasedRulesAndSamples()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);

        await wizard.GoToRulesAsync();

        var rules = wizard.Rules;
        Assert.Equal("Standardowy", rules.Profile?.Text);
        Assert.True(rules.IsBuiltInProfile);
        Assert.False(rules.IsModified);
        Assert.Null(rules.Error);
        Assert.Equal([Attr.Cn, Attr.DisplayName, Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.Description], rules.Rules.Select(r => r.Attribute));
        Assert.All(rules.Rules, r => Assert.True(r.IsCore));
        Assert.Equal("{givenName|sam|lower}.{sn|sam|lower}", wizard.Rule(Attr.SamAccountName).Pattern);
        Assert.Equal("lukasz.grzedzinski  ·  malgorzata.zrebiec  ·  bozena.kakol", wizard.Rule(Attr.SamAccountName).Sample);
        Assert.StartsWith("Łukasz Grzędziński  ·  Małgorzata Źrebiec", wizard.Rule(Attr.Cn).Sample, StringComparison.Ordinal);
        Assert.StartsWith("lukasz.grzedzinski@contoso.test", wizard.Rule(Attr.UserPrincipalName).Sample, StringComparison.Ordinal);
        Assert.Equal("bez reguły – wartość z kolumny „E-mail”", wizard.Rule(Attr.Mail).EmptyHint);
        Assert.StartsWith("lukasz.grzedzinski@firma.pl", wizard.Rule(Attr.Mail).Sample, StringComparison.Ordinal);
        Assert.False(rules.HasCounter);
        Assert.Null(rules.FirstFreeText);
        Assert.Equal(["contoso.test", "contoso.com", "firma.pl"], rules.UpnSuffixes);
        Assert.Equal("contoso.test", rules.UpnSuffix);
        Assert.Equal("contoso.test", rules.MailDomain);
        Assert.Equal(InitialPasswordMode.Generate, rules.PasswordMode);
        Assert.Equal(PasswordGenerator.DefaultLength, rules.GeneratedLength);
        Assert.True(rules.MustChangeAtLogon);
        Assert.True(rules.Enabled);
        Assert.Equal(7, rules.DomainMinimumLength);
        Assert.Equal("minimum domeny: 7", rules.DomainMinimumText);
        Assert.False(rules.AddPrimarySmtp);
        Assert.Equal("Najniższy wolny numer (uzupełnia luki) · bez numerów z Kosza AD · zajęta nazwa: dopisz numer", rules.AdvancedSummary);
        Assert.Equal(
            "Foldery macierzyste nie są tworzone – można ustawić tylko atrybuty homeDirectory i homeDrive.",
            rules.HomeFoldersNote);
    }

    [Fact]
    public async Task NumberedProfile_ShowsTheFirstFreeNumber_AndNumberedSamples()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;

        rules.Profile = rules.Profiles.Single(p => p.Value == BulkCreateProfileStore.NumberedId);
        await wizard.SettleAsync();

        Assert.Equal("Konta numerowane (kd201…)", rules.Profile?.Text);
        Assert.Equal(BulkCreateProfileStore.NumberedLoginPattern, wizard.Rule(Attr.SamAccountName).Pattern);
        Assert.True(rules.HasCounter);
        Assert.True(rules.CanEditPrefix);
        Assert.Equal("kd", rules.CounterPrefix);
        Assert.Equal(201, rules.CounterStart);
        Assert.Equal(0, rules.CounterWidth);
        Assert.Equal(1, rules.CounterStep);
        Assert.Equal(CounterMode.AfterHighest, rules.CounterMode);
        Assert.True(rules.AvoidRecycleBinNumbers);
        Assert.Equal("Pierwszy wolny numer: kd208 (najwyższy istniejący: kd207)", rules.FirstFreeText);
        Assert.True(wizard.Rule(Attr.SamAccountName).IsCounter);
        Assert.Equal("kd208  ·  kd209  ·  kd210", wizard.Rule(Attr.SamAccountName).Sample);
        Assert.StartsWith("kd208@contoso.test", wizard.Rule(Attr.UserPrincipalName).Sample, StringComparison.Ordinal);
        Assert.StartsWith("Łukasz Grzędziński", wizard.Rule(Attr.Cn).Sample, StringComparison.Ordinal);
        Assert.Equal("Kolumna „E-mail” ma pierwszeństwo – reguła działa tylko dla pustych komórek", wizard.Rule(Attr.Mail).ColumnNote);
        Assert.StartsWith("lukasz.grzedzinski@firma.pl", wizard.Rule(Attr.Mail).Sample, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NumberingPanel_RewritesEveryCounter()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;
        rules.Profile = rules.Profiles.Single(p => p.Value == BulkCreateProfileStore.NumberedId);
        await wizard.SettleAsync();
        wizard.Rule(Attr.Description).Pattern = "Konto {$nr:201|upper} – {department}";

        rules.CounterStart = 500;
        rules.CounterWidth = 4;
        rules.CounterStep = 5;
        await wizard.SettleAsync();

        Assert.Equal("kd{$nr:500:4:5}", wizard.Rule(Attr.SamAccountName).Pattern);
        Assert.Equal("Konto {$nr:500:4:5|upper} – {department}", wizard.Rule(Attr.Description).Pattern);
        Assert.Null(rules.Error);
        Assert.Equal("kd0500  ·  kd0505  ·  kd0510", wizard.Rule(Attr.SamAccountName).Sample);
        Assert.StartsWith("Konto 0500 – Księgowość", wizard.Rule(Attr.Description).Sample, StringComparison.Ordinal);
        Assert.True(rules.IsModified);
        Assert.Equal("(zmieniony)", rules.ModifiedText);

        rules.CounterPrefix = "pr";
        await wizard.SettleAsync();
        Assert.Equal("pr{$nr:500:4:5}", wizard.Rule(Attr.SamAccountName).Pattern);
        Assert.StartsWith("Pierwszy wolny numer: pr0500", rules.FirstFreeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiteralEndingInDigits_OffersTheCounter()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var login = wizard.Rule(Attr.SamAccountName);

        login.Pattern = "kd201";

        Assert.Equal("kd{$nr:201}", login.CounterFix);
        Assert.True(login.HasCounterFix);
        Assert.Equal("Numerować kolejno od 201? → kd{$nr:201}", login.Suggestion);

        wizard.Rules.ApplyCounterSuggestionCommand.Execute(login);
        await wizard.SettleAsync();

        Assert.Equal("kd{$nr:201}", login.Pattern);
        Assert.Null(login.CounterFix);
        Assert.True(wizard.Rules.HasCounter);
        Assert.StartsWith("Pierwszy wolny numer: kd2", wizard.Rules.FirstFreeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatternErrors_AreLocalized_WithPositionTokenAndSuggestion()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var display = wizard.Rule(Attr.DisplayName);

        display.Pattern = "{imie} {sn}";
        Assert.Equal("Pozycja 1: nieznany atrybut {imie} – pole pozostanie puste.", display.Error);
        Assert.False(display.IsError);
        Assert.Equal("Czy chodziło o {givenName}?", display.Suggestion);
        Assert.Null(wizard.Rules.Error);

        display.Pattern = "{givenName} {sn";
        Assert.True(display.IsError);
        Assert.StartsWith("Pozycja 13: błąd we wzorcu", display.Error, StringComparison.Ordinal);
        Assert.StartsWith("Nazwa wyświetlana [displayName]: Pozycja 13:", wizard.Rules.Error, StringComparison.Ordinal);
        Assert.False(wizard.Wizard.CanGoNext);
        Assert.Null(wizard.Wizard.BuildInput());

        display.Pattern = "{givenName} {$counter}";
        Assert.StartsWith("Pozycja 13: pola {$counter} nie można użyć", display.Error, StringComparison.Ordinal);

        display.Pattern = "{givenName} {sn}";
        Assert.Null(display.Error);
        Assert.Null(wizard.Rules.Error);
        Assert.True(wizard.Wizard.CanGoNext);
    }

    [Fact]
    public async Task NameWithoutSource_StopsTheStep()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();

        wizard.Rule(Attr.SamAccountName).Pattern = string.Empty;

        Assert.Equal(
            wizard.Rule(Attr.SamAccountName).AttributeText + ": brak reguły i kolumny – dodaj wzorzec albo przypisz kolumnę.",
            wizard.Rules.Error);
        Assert.EndsWith("[sAMAccountName]", wizard.Rule(Attr.SamAccountName).AttributeText, StringComparison.Ordinal);
        Assert.Equal("bez reguły – pole pozostanie puste", wizard.Rule(Attr.SamAccountName).EmptyHint);
    }

    [Fact]
    public async Task InsertField_ByPolishLabels_AtTheCaret()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.SensitiveColumnsTsv);
        wizard.Mapping("PESEL").Target = ColumnTargetKind.PatternOnly;
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;
        var fields = rules.InsertableFields;

        Assert.Contains(fields, f => f.Text == "Imię" && f.Token == "{givenName}" && f.MenuText == "Imię  →  {givenName}");
        Assert.Contains(fields, f => f.Text == "Nazwisko" && f.Token == "{sn}");
        Assert.Contains(fields, f => f.Text == "Adres e-mail" && f.Token == "{mail}");
        Assert.Contains(fields, f => f.Token == "{$column:PESEL}" && f.Text == "Kolumna „PESEL”");
        Assert.DoesNotContain(fields, f => f.Token.Contains("Hasło", StringComparison.Ordinal) || f.Token.Contains("$column:C", StringComparison.Ordinal));
        Assert.Contains(fields, f => f.Token == "{$nr:1}" && f.Text == "Kolejny numer");
        Assert.Contains(fields, f => f.Token == "{$suffix}");
        Assert.Contains(fields, f => f.Token == "{$mailDomain}");
        // The date entries describe the format, not a fixed day (the label would show a stale date tomorrow).
        Assert.Contains(fields, f => f.Token == "{$date:yyyy-MM-dd}" && f.Text == "Dzisiejsza data (RRRR-MM-DD)");
        Assert.Contains(fields, f => f.Token == "{$date:dd.MM.yyyy}" && f.Text == "Dzisiejsza data (DD.MM.RRRR)");
        var lower = Assert.Single(fields, f => f.Token == "|lower");
        Assert.True(lower.IsFunction);
        Assert.Equal("małe litery", lower.Description);
        Assert.Equal("|first:1  –  pierwsze N znaków", fields.Single(f => f.Token == "|first:1").MenuText);

        var description = wizard.Rule(Attr.Description);
        rules.ActiveRule = description;
        rules.InsertTokenCommand.Execute("{givenName}");
        rules.InsertTokenCommand.Execute(" ");
        rules.InsertTokenCommand.Execute("{sn}");
        Assert.Equal("{givenName} {sn}", description.Pattern);

        description.CaretIndex = 4;
        rules.InsertTokenCommand.Execute("|upper");
        Assert.Equal("{givenName|upper} {sn}", description.Pattern);
        Assert.Equal(17, description.CaretIndex);

        description.CaretIndex = description.Pattern.Length;
        rules.InsertTokenCommand.Execute("|first:1");
        Assert.Equal("{givenName|upper} {sn|first:1}", description.Pattern);
    }

    [Fact]
    public async Task ColumnPrecedence_AndTheRuleThatReplacesTheColumn()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var mail = wizard.Rule(Attr.Mail);

        mail.Pattern = "{givenName|sam|lower}.{sn|sam|lower}@{$mailDomain}";
        await wizard.SettleAsync();
        Assert.Equal("Kolumna „E-mail” ma pierwszeństwo – reguła działa tylko dla pustych komórek", mail.ColumnNote);
        Assert.StartsWith("lukasz.grzedzinski@firma.pl", mail.Sample, StringComparison.Ordinal);

        mail.OverridesColumn = true;
        await wizard.SettleAsync();
        Assert.Equal("Reguła zastępuje kolumnę „E-mail”", mail.ColumnNote);
        Assert.StartsWith("lukasz.grzedzinski@contoso.test", mail.Sample, StringComparison.Ordinal);
        Assert.Contains(wizard.Rules.ToRules().Attributes, r => r.Attribute == Attr.Mail && r.OverridesColumn);
    }

    [Fact]
    public async Task UpnSuffix_MailDomain_AndUpnFromMail()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;

        rules.UpnSuffix = "contoso.com";
        await wizard.SettleAsync();
        Assert.Equal("contoso.com", rules.MailDomain);
        Assert.StartsWith("lukasz.grzedzinski@contoso.com", wizard.Rule(Attr.UserPrincipalName).Sample, StringComparison.Ordinal);
        Assert.Equal("contoso.com", rules.ToRules().UpnSuffix);
        Assert.True(rules.IsModified);

        rules.MailDomain = "firma.local";
        Assert.Equal("Domena firma.local nie odbiera poczty z internetu – sprawdź domenę adresów e-mail.", rules.MailDomainWarning);
        Assert.Equal("firma.local", rules.ToRules().MailDomain);
        rules.UpnSuffix = "firma.pl";
        Assert.Equal("firma.local", rules.MailDomain);

        rules.UseMailAsUpnCommand.Execute(null);
        await wizard.SettleAsync();
        Assert.Equal("{mail}", wizard.Rule(Attr.UserPrincipalName).Pattern);
        Assert.True(rules.IsUpnFromPattern);
        Assert.StartsWith("lukasz.grzedzinski@firma.pl", wizard.Rule(Attr.UserPrincipalName).Sample, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Samples_MarkChangedAndShortenedValues()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var login = wizard.Rule(Attr.SamAccountName);

        login.Pattern = "{givenName}.{sn}";
        await wizard.SettleAsync();
        Assert.StartsWith("Lukasz.Grzedzinski (zmieniono znaki)", login.Sample, StringComparison.Ordinal);

        login.Pattern = "{givenName|sam|lower}.{sn|sam|lower}.{department|sam|lower}";
        await wizard.SettleAsync();
        Assert.Contains("(skrócono)", login.Sample, StringComparison.Ordinal);
        Assert.All(login.Sample.Split("  ·  "), s => Assert.True(s.Split(' ')[^1].Length > 0));
    }

    [Fact]
    public async Task AccountOptions_AreValidated_AndGoToTheOptions()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;

        rules.GeneratedLength = 3;
        Assert.NotNull(rules.Error);
        rules.GeneratedLength = 20;
        Assert.Null(rules.Error);

        rules.HasExpiry = true;
        Assert.NotNull(rules.ExpiryDate);
        var expiry = rules.ExpiryDate!.Value;
        var options = rules.ToOptions();
        Assert.NotNull(options.ExpiresAt);
        Assert.Equal(expiry.Date.AddDays(1), options.ExpiresAt!.Value.DateTime);
        rules.ExpiryDate = new DateTime(2020, 1, 31);
        Assert.Equal("Data wygaśnięcia jest w przeszłości.", rules.Error);
        rules.HasExpiry = false;
        Assert.Null(rules.Error);
        Assert.Null(rules.ToOptions().ExpiresAt);

        rules.PasswordMode = InitialPasswordMode.None;
        Assert.False(rules.Enabled);
        Assert.False(rules.CanEnable);
        Assert.False(rules.ToOptions().Enabled);
        Assert.Equal(InitialPasswordMode.None, rules.ToOptions().PasswordMode);

        // Back to a password mode: the accounts are enabled again as before (not silently created disabled).
        rules.PasswordMode = InitialPasswordMode.Generate;
        Assert.True(rules.Enabled);
        rules.Enabled = false;
        rules.PasswordMode = InitialPasswordMode.None;
        rules.PasswordMode = InitialPasswordMode.SameForAll;
        Assert.False(rules.Enabled);
        rules.PasswordMode = InitialPasswordMode.Generate;
        rules.Enabled = true;
        rules.MustChangeAtLogon = false;
        rules.MatchExisting = false;
        options = rules.ToOptions();
        Assert.Equal(20, options.GeneratedLength);
        Assert.True(options.Enabled);
        Assert.False(options.MustChangeAtLogon);
        Assert.Equal([], options.MatchAttributes);
        Assert.Contains("bez szukania istniejących kont", rules.AdvancedSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedPassword_IsReadOnceAndCleared_MismatchIsAnError()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;
        var password = new FakePasswordInput("Start!2026Łąka");
        var confirm = new FakePasswordInput("Start!2026Łąka");
        rules.PasswordInput = password;
        rules.ConfirmPasswordInput = confirm;

        Assert.Null(rules.ReadSharedPassword());
        rules.PasswordMode = InitialPasswordMode.SameForAll;
        Assert.True(rules.IsSharedPassword);
        Assert.Equal("Wszyscy będą znać hasło każdego konta do pierwszej zmiany hasła.", rules.SharedPasswordNote);
        Assert.False(rules.NeedsSharedPassword);

        Assert.Equal("Start!2026Łąka", rules.ReadSharedPassword());
        Assert.True(password.Cleared);
        Assert.True(confirm.Cleared);
        Assert.DoesNotContain("Start!2026", rules.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Start!2026", rules.Capture().ToString(), StringComparison.Ordinal);

        password.Password = string.Empty;
        Assert.True(rules.NeedsSharedPassword);
        Assert.Null(rules.ReadSharedPassword());

        password.Password = "Start!2026Łąka";
        confirm.Password = "Start!2026Łaka";
        Assert.Null(rules.ReadSharedPassword());
        Assert.NotNull(rules.PasswordError);
        Assert.Equal(rules.PasswordError, rules.Error);
        rules.NotifyPasswordChanged();
        Assert.Null(rules.PasswordError);
        Assert.Null(rules.Error);
    }

    [Fact]
    public async Task Groups_ArePickedAndRemoved()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;
        wizard.Harness.Dialogs.PickedObjects.Enqueue([wizard.Sample.GroupNew, wizard.Sample.GroupDomainLocal]);

        await rules.AddGroupsCommand.ExecuteAsync(null);

        Assert.Equal(["GG_Nowi", "DL_Drukarki"], rules.Groups.Select(g => g.Name));
        Assert.All(rules.Groups, g => Assert.True(g.IsResolved));
        Assert.Equal([wizard.Sample.GroupNew, wizard.Sample.GroupDomainLocal], rules.ToOptions().GroupDns);

        rules.RemoveGroupCommand.Execute(rules.Groups[1]);
        Assert.Equal([wizard.Sample.GroupNew], rules.ToOptions().GroupDns);
        Assert.True(rules.IsModified);
    }

    [Fact]
    public async Task KindChange_GivesTheKindsProfile_AndAsksWhenRulesWereChanged()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        var rules = wizard.Rules;

        wizard.Wizard.Kind = wizard.Wizard.Kinds.Single(k => k.Value == ObjectKind.Group);
        Assert.Equal(ObjectKind.Group, rules.Kind);
        Assert.True(rules.IsGroupKind);
        Assert.False(rules.IsUserKind);
        Assert.Equal([Attr.Cn, Attr.SamAccountName, Attr.Mail, Attr.Description], rules.Rules.Select(r => r.Attribute));
        Assert.Equal("{cn}", wizard.Rule(Attr.SamAccountName).Pattern);
        Assert.Equal(InitialPasswordMode.None, rules.ToOptions().PasswordMode);
        Assert.Equal(ObjectKind.Group, rules.ToRules().Kind);

        wizard.Rule(Attr.Description).Pattern = "Grupa {cn}";
        Assert.True(rules.IsModified);
        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(false);
        wizard.Wizard.Kind = wizard.Wizard.Kinds.Single(k => k.Value == ObjectKind.Computer);
        await Task.Yield();
        Assert.Equal(ObjectKind.Group, wizard.Wizard.ObjectKind);
        Assert.Equal("Grupa {cn}", wizard.Rule(Attr.Description).Pattern);

        wizard.Harness.Dialogs.ConfirmAnswers.Enqueue(true);
        wizard.Wizard.Kind = wizard.Wizard.Kinds.Single(k => k.Value == ObjectKind.Computer);
        await Task.Yield();
        Assert.Equal(ObjectKind.Computer, rules.Kind);
        Assert.True(rules.IsComputerKind);
        Assert.False(rules.CanChooseCollisions);
        Assert.Equal(CollisionStrategy.MarkError, rules.ToRules().Collisions);
        Assert.Equal(string.Empty, wizard.Rule(Attr.Description).Pattern);
    }

    [Fact]
    public async Task SessionEnded_NumbersAreNotChecked()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        wizard.Rules.Profile = wizard.Rules.Profiles.Single(p => p.Value == BulkCreateProfileStore.NumberedId);
        await wizard.SettleAsync();

        wizard.Wizard.OnSessionEnded();

        Assert.Equal("Pierwszy numer: kd201 (bez sprawdzenia w AD)", wizard.Rules.FirstFreeText);
    }

    [Fact]
    public async Task AddedRule_ForAnyAttribute_AndRemoved()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.OperatorScenarioTsv);
        await wizard.GoToRulesAsync();
        var rules = wizard.Rules;

        rules.AddRuleCommand.Execute(null);
        var added = rules.Rules[^1];
        Assert.False(added.IsCore);
        Assert.True(added.CanEditAttribute);
        added.Pattern = "Nowy pracownik";
        Assert.Equal("Wybierz atrybut reguły.", rules.Error);

        added.AttributeText = "extensionAttribute5";
        Assert.Equal("extensionAttribute5", added.Attribute);
        Assert.Null(rules.Error);
        Assert.Contains(rules.ToRules().Attributes, r => r.Attribute == "extensionAttribute5" && r.Pattern == "Nowy pracownik");

        added.AttributeText = "unicodePwd";
        Assert.Equal("Tego atrybutu nie można ustawić regułą.", added.Error);
        Assert.NotNull(rules.Error);

        rules.RemoveRuleCommand.Execute(added);
        rules.RemoveRuleCommand.Execute(wizard.Rule(Attr.Cn));
        Assert.Null(rules.Error);
        Assert.Contains(rules.Rules, r => r.Attribute == Attr.Cn);
        Assert.DoesNotContain(rules.Rules, r => r.Attribute == "unicodePwd");
    }

    [Fact]
    public async Task FiveThousandRows_SamplesAreRenderedOffTheUiThread()
    {
        using var wizard = await BulkWizardHarness.CreateAsync();
        await wizard.PasteAsync(ExcelSamples.Rows(5000));
        var started = DateTime.UtcNow;

        await wizard.GoToRulesAsync();
        wizard.Rule(Attr.DisplayName).Pattern = "{sn} {givenName} ({department})";
        await wizard.SettleAsync();

        Assert.StartsWith("Żak Łucja (Księgowość)", wizard.Rule(Attr.DisplayName).Sample, StringComparison.Ordinal);
        Assert.Contains("najdłuższy:", wizard.Rule(Attr.DisplayName).Sample, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
    }
}
