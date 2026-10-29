using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using static AdMng.Presentation.Tests.Correspondence.PageTests;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// "1 · Odbiorcy" from the directory (design note 6.4, decision D2): the window's request, the selected objects again, the
/// object picker, group members (nested), every AD field as a U1 expression with the salutations of the U1 settings,
/// objects without an address as invalid rows, the field definitions and their validation, the end of the session.
/// </summary>
public sealed class RecipientsDirectoryTests
{
    [Fact]
    public async Task Request_AddsTheObjectsAsAdRows_WithTheirFields()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var company = scenario.Company;

        using var window = await scenario.OpenAsync(new CorrespondenceRequest([company.JanKowalski, company.Computer1, company.BartlomiejKozlowski]));
        var page = window.Recipients;

        Assert.Equal(["jan.kowalski@contoso.test", "bartlomiej.kozlowski@contoso.test"], page.Items.Select(r => r.Email));
        var jan = page.Items[0];
        Assert.Equal("Jan Kowalski", jan.Name);
        Assert.Equal("AD", jan.Source);
        Assert.Equal(RecipientOrigin.Directory, jan.Origin);
        Assert.True(Core.Ldap.DistinguishedName.AreEqual(company.JanKowalski, jan.DistinguishedName));
        Assert.Equal("Jan", jan["Imię"]);
        Assert.Equal("Kowalski", jan["Nazwisko"]);
        Assert.Equal("Administrator systemów", jan["Stanowisko"]);
        Assert.Equal("IT", jan["Dział"]);
        Assert.Equal("jan.kowalski", jan["Login"]);
        Assert.Equal("Pana", jan["Pana/Panią"]);
        Assert.Equal("Szanowny", jan["Szanowny/a"]);
        Assert.Equal([.. DirectoryFieldDefinition.Defaults.Select(d => d.Name)], page.Columns);
        Assert.Equal("Z katalogu: 2 nowe, pominięte obiekty innego typu: 1", page.StatusText);
        Assert.Equal(page.StatusText, window.StatusText);
        Assert.Null(window.Error);

        // "Zaznaczone obiekty" adds the window's selection again.
        Assert.Equal(3, page.SelectedObjects.Count);
        Assert.True(page.AddFromSelectionCommand.CanExecute(null));
        await page.AddFromSelectionCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("Z katalogu: 0 nowych, 2 powtórzone, pominięte obiekty innego typu: 1", page.StatusText);
    }

    [Fact]
    public async Task AdRows_UseTheU1Salutations_ForEveryPersonField()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var salutations = scenario.Harness.Builder.AddSalutationSamples(scenario.Company);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        scenario.Harness.Dialogs.PickedObjects.Enqueue([salutations.Professor, salutations.Doctor, salutations.Associate]);

        await page.PickUsersCommand.ExecuteAsync(null);

        var professor = Recipient(page, "stanislaw.kowalczyk@contoso.test");
        Assert.Equal("Szanowny Panie Profesorze", professor["Zwrot osobowy"]);
        Assert.Equal("Pana", professor["Pana/Panią"]);
        Assert.Equal("Panu", professor["Panu/Pani"]);
        Assert.Equal("Szanowny", professor["Szanowny/a"]);
        var doctor = Recipient(page, "anna.nowak@contoso.test");
        Assert.Equal("dr Anna Nowak", doctor.Name);
        Assert.Equal("Szanowna Pani Doktor", doctor["Zwrot osobowy"]);
        Assert.Equal("Panią", doctor["Pana/Panią"]);
        Assert.Equal("Pani", doctor["Pan/Pani"]);
        Assert.Equal("Szanowna", doctor["Szanowny/a"]);
        Assert.Equal("dr hab. Ewa Zielińska, prof. UW", Recipient(page, "ewa.zielinska@contoso.test").Name);
        Assert.Equal("Z katalogu: 3 nowe", page.StatusText);
    }

    [Fact]
    public async Task GroupMembers_AreReadInChain_UsersAndContactsOnly()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var builder = scenario.Harness.Builder;
        var contact = builder.AddContact("Grażyna", "Żak", scenario.Company.SalesOu,
            new Dictionary<string, object?> { [Attr.Mail] = "grazyna.zak@partner.pl" });
        builder.Session.SetValues(scenario.Company.GroupSales, Attr.Member,
            [.. builder.Session.GetValues(scenario.Company.GroupSales, Attr.Member), contact, scenario.Company.Computer1]);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        scenario.Harness.Dialogs.PickedObjects.Enqueue([scenario.Company.GroupAll]);

        await page.AddGroupMembersCommand.ExecuteAsync(null);

        Assert.Equal(9, page.Items.Count);   // 8 users of the nested groups + the contact
        Assert.Contains(page.Items, r => r.Email == "grazyna.zak@partner.pl" && r.Name == "Grażyna Żak");
        Assert.All(page.Items, r => Assert.Equal("grupa DL_Wszyscy", r.Source));
        Assert.Equal("Z katalogu: 9 nowych", page.StatusText);
    }

    [Fact]
    public async Task ObjectsWithoutAnAddress_AreInvalidRows_AndProxyAddressesAreUsed()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var builder = scenario.Harness.Builder;
        var noMail = builder.AddUser("Wiesław", "Pękala", scenario.Company.SalesOu);
        var proxy = builder.AddUser("Agata", "Źródło", scenario.Company.SalesOu, new Dictionary<string, object?>
        {
            [Attr.ProxyAddresses] = new[] { "smtp:a.zrodlo@stara.contoso.test", "SMTP:Agata.Zrodlo@contoso.test" },
        });
        using var window = await scenario.OpenAsync(new CorrespondenceRequest([noMail, proxy, noMail]));
        var page = window.Recipients;

        var invalid = Assert.Single(page.Items, r => !r.IsValid);
        Assert.Equal(string.Empty, invalid.Email);
        Assert.Equal("Wiesław Pękala", invalid.Name);
        Assert.Equal(RecipientStatusKind.Skipped, invalid.StatusKind);
        Assert.Equal("brak adresu e-mail", invalid.Status);
        Assert.Equal("agata.zrodlo@contoso.test", Assert.Single(page.Items, r => r.IsValid).Email);
        Assert.Equal("Odbiorcy: 2 • do wysłania: 1 • błędne: 1", page.CountsText);
        Assert.Equal("Z katalogu: 1 nowy, 1 bez adresu e-mail", page.StatusText);
        Assert.DoesNotContain(invalid, page.SendList());

        // The same object again is not added twice.
        await page.AddFromSelectionCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task DirectoryFields_AreValidated_AndOnlyValidOnesEvaluated()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        var fields = page.DirectoryFields;
        page.ResetDirectoryFieldsCommand.Execute(null);
        Assert.All(fields, f => Assert.Null(f.Error));

        page.AddDirectoryFieldCommand.Execute(null);
        page.AddDirectoryFieldCommand.Execute(null);
        Assert.Equal(["Nowe pole", "Nowe pole 2"], fields.Skip(DirectoryFieldDefinition.Defaults.Count).Select(f => f.Name));
        var custom = fields[^2];
        custom.Expression = "{unicodePwd}";
        Assert.Equal("Atrybutu unicodePwd nie można użyć – zawiera dane poufne.", custom.Error);
        custom.Expression = "{$manager:mail}";
        Assert.Equal("Pola {$manager} nie można tu użyć.", custom.Error);
        custom.Expression = "{givenName";
        Assert.StartsWith("Nieprawidłowe wyrażenie: ", custom.Error, StringComparison.Ordinal);
        custom.Expression = "  ";
        Assert.Equal("Podaj wyrażenie, np. {givenName}.", custom.Error);
        custom.Expression = "{l}";
        custom.Name = "Imię";
        Assert.Equal("Jest już pole o tej nazwie.", custom.Error);
        custom.Name = "Miasto {x}";
        Assert.Equal("Nazwa pola może mieć najwyżej 80 znaków i nie może zawierać nawiasów klamrowych ani znaku „|”.", custom.Error);
        custom.Name = " ";
        Assert.Equal("Podaj nazwę pola.", custom.Error);
        custom.Name = "Miasto";
        Assert.Null(custom.Error);
        fields[^1].Expression = "{$input:termin}";
        page.RemoveDirectoryFieldCommand.Execute(fields[0]);    // "Imię"
        Assert.DoesNotContain(fields, f => f.Name == "Imię");

        await window.Recipients.AddFromDirectoryAsync([scenario.Company.ZanetaSzymanska]);

        var zaneta = Recipient(page, "zaneta.szymanska@contoso.test");
        Assert.Equal("Wrocław", zaneta["Miasto"]);
        Assert.False(zaneta.Fields.ContainsKey("Nowe pole 2"));   // invalid: not evaluated
        Assert.False(zaneta.Fields.ContainsKey("Imię"));
        Assert.Contains("Miasto", page.Columns);
    }

    [Fact]
    public async Task AfterTheSessionEnded_TheDirectoryActionsAreOff()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync(new CorrespondenceRequest([scenario.Company.JanKowalski]));
        var page = window.Recipients;
        Assert.True(page.PickUsersCommand.CanExecute(null));

        await scenario.Harness.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => !page.CanUseDirectory);

        Assert.False(page.PickUsersCommand.CanExecute(null));
        Assert.False(page.AddGroupMembersCommand.CanExecute(null));
        Assert.False(page.AddFromSelectionCommand.CanExecute(null));
        Assert.Single(page.Items);   // the rows stay
    }

    [Fact]
    public async Task DirectoryErrors_AreShown()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();
        var page = window.Recipients;
        scenario.Harness.Dialogs.PickedObjects.Enqueue([scenario.Company.JanKowalski]);
        scenario.Harness.Session.FailNext(FakeOperationKind.Search, null, DirectoryErrorKind.Busy);

        await page.PickUsersCommand.ExecuteAsync(null);

        Assert.Empty(page.Items);
        var (title, _) = Assert.Single(scenario.Harness.Dialogs.Errors);
        Assert.Equal("Odbiorcy z katalogu", title);
    }

    [Fact]
    public void Builder_ReadsWhatTheFieldsNeed_AndNeverASecret()
    {
        var fields = new Core.Messaging.UserMessageFields();
        var context = new DirectoryRowContext(DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc, Core.Messaging.Salutations.SalutationOptions.Default, "contoso.test", FakeSchema.Create());
        var builder = new DirectoryRowBuilder(fields,
        [
            new("Miasto", "{l}"),
            new("Telefon", "{telephoneNumber|default:brak}"),
            new("Hasło", "{unicodePwd}"),
            new("Skrzynka", "{description}", DirectoryFieldTarget.Mailbox),
            new("Zwrot", "{$salutation}"),
            new("miasto", "{st}"),
        ], context);

        var person = builder.AttributesFor(DirectoryFieldTarget.Person);
        Assert.Contains("l", person);
        Assert.Contains("telephoneNumber", person);
        Assert.Contains("mail", person);
        Assert.Contains("proxyAddresses", person);
        Assert.Contains("objectClass", person);
        Assert.Contains(Core.Messaging.Salutations.SalutationOptions.Default.UsableTitleAttribute!, person);
        Assert.DoesNotContain("unicodePwd", person, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("st", person);   // the second "Miasto" is a duplicate name
        Assert.DoesNotContain("description", person);
        Assert.Contains("description", builder.AttributesFor(DirectoryFieldTarget.Mailbox));
        Assert.Equal(["Miasto", "Telefon", "Zwrot"], builder.FieldNames(DirectoryFieldTarget.Person));
        Assert.Equal(nameof(DirectoryRowBuilder) + " (4)", builder.ToString());
    }

    [Fact]
    public void Builder_AddressAndName_FallBack()
    {
        var withProxy = new DirectoryObject("CN=Agata Źródło,OU=Sprzedaż,DC=contoso,DC=test",
        [
            new AttributeValues(Attr.Mail, ["nie-adres"]),
            new AttributeValues(Attr.ProxyAddresses, ["smtp:stary@contoso.test", "SMTP:Agata.Zrodlo@Contoso.test"]),
        ]);
        var bare = new DirectoryObject("CN=Bez Nazwy\\, Sp. z o.o.,OU=Sprzedaż,DC=contoso,DC=test", []);

        Assert.Equal("agata.zrodlo@contoso.test", DirectoryRowBuilder.AddressOf(withProxy));
        Assert.Equal("Agata Źródło", DirectoryRowBuilder.NameOf(withProxy));
        Assert.Null(DirectoryRowBuilder.AddressOf(bare));
        Assert.Equal("Bez Nazwy, Sp. z o.o.", DirectoryRowBuilder.NameOf(bare));
    }
}
