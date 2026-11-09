using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Testing;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>The header synonym table and the content refinements of 2.1 (docs/dev/phase3-bulk-create.md 2.11).</summary>
public sealed class ColumnSynonymsTests
{
    /// <summary>The table of 2.1, row by row: the target and every folded header (kind-independent entries).</summary>
    public static TheoryData<string, string> AttributeEntries()
    {
        var data = new TheoryData<string, string>();
        void Add(string attribute, params string[] folded)
        {
            foreach (var header in folded)
            {
                data.Add(header, attribute);
            }
        }

        Add("givenName", "imie", "imiona", "pierwszeimie", "firstname", "givenname", "forename");
        Add("sn", "nazwisko", "lastname", "surname", "familyname", "sn");
        Add("initials", "inicjaly", "inicjal", "initials", "middleinitial");
        Add("middleName", "drugieimie", "middlename");
        Add("displayName", "nazwawyswietlana", "displayname");
        Add("cn", "nazwaobiektu", "nazwagrupy", "nazwakomputera", "komputer", "nazwahosta", "cn", "commonname", "groupname", "computername", "hostname");
        Add("sAMAccountName", "login", "nazwauzytkownika", "nazwalogowania", "konto", "loginwindows", "samaccountname", "username", "userid", "logon",
            "logonname", "account");
        Add("userPrincipalName", "upn", "loginupn", "userprincipalname");
        Add("mail", "email", "mail", "adresemail", "adresmailowy", "poczta", "pocztaemail", "adrespoczty", "emailsluzbowy", "mailsluzbowy",
            "adresemailsluzbowy", "emailaddress", "workemail");
        Add("mobile", "telefonkomorkowy", "komorkowy", "telkom", "telefonkom", "telkomorkowy", "nrkomorki", "numerkomorki", "nrtelkom",
            "nrtelefonukomorkowego", "numertelefonukomorkowego", "komorkasluzbowa", "gsm", "mobile", "mobilephone", "cellphone", "cell", "mobilenumber");
        Add("telephoneNumber", "telefon", "tel", "nrtel", "nrtelefonu", "numertelefonu", "telefonstacjonarny", "telefonsluzbowy", "telsluzbowy",
            "telefonbiurowy", "nrwewnetrzny", "wewnetrzny", "phone", "telephone", "telephonenumber", "officephone", "businessphone", "workphone", "extension");
        Add("homePhone", "telefondomowy", "homephone");
        Add("ipPhone", "telefonip", "ipphone");
        Add("facsimileTelephoneNumber", "faks", "fax", "nrfaksu", "faxnumber");
        Add("title", "stanowisko", "funkcja", "title", "jobtitle", "position");
        Add("department", "dzial", "departament", "wydzial", "komorkaorganizacyjna", "komorka", "department", "dept");
        Add("company", "firma", "spolka", "pracodawca", "company", "employer", "organization", "organisation");
        Add("physicalDeliveryOfficeName", "biuro", "pokoj", "siedziba", "miejscepracy", "lokalizacja", "office", "room", "location");
        Add("employeeID", "idpracownika", "identyfikatorpracownika", "employeeid");
        Add("employeeNumber", "numerpracownika", "nrpracownika", "nrewidencyjny", "numerewidencyjny", "nrkadrowy", "employeenumber", "personnelnumber",
            "staffnumber");
        Add("employeeType", "typpracownika", "rodzajzatrudnienia", "employeetype");
        Add("description", "opis", "description");
        Add("info", "uwagi", "notatki", "notatka", "notes", "comment", "comments", "info");
        Add("streetAddress", "ulica", "adres", "street", "streetaddress", "address");
        Add("l", "miasto", "miejscowosc", "city", "town", "locality");
        Add("postalCode", "kodpocztowy", "kod", "postalcode", "zip", "zipcode", "postcode");
        Add("st", "wojewodztwo", "region", "state", "province");
        Add("wWWHomePage", "stronawww", "www", "stronainternetowa", "homepage", "website", "webpage", "web");
        Add("homeDirectory", "folderdomowy", "katalogdomowy", "homedirectory", "homefolder");
        Add("homeDrive", "dyskdomowy", "homedrive");
        Add("scriptPath", "skryptlogowania", "scriptpath", "logonscript");
        Add("profilePath", "sciezkaprofilu", "profilepath");
        return data;
    }

    /// <summary>The non-attribute targets of the table (users).</summary>
    public static TheoryData<string, ColumnTargetKind, NameOrder> TargetEntries()
    {
        var data = new TheoryData<string, ColumnTargetKind, NameOrder>();
        foreach (var header in new[] { "imieinazwisko", "imienazwisko", "pracownik", "osoba", "nazwa", "fullname", "employeename", "name" })
        {
            data.Add(header, ColumnTargetKind.FullName, NameOrder.GivenNameFirst);
        }

        data.Add("nazwiskoiimie", ColumnTargetKind.FullName, NameOrder.SurnameFirst);
        data.Add("nazwiskoimie", ColumnTargetKind.FullName, NameOrder.SurnameFirst);
        foreach (var header in new[] { "emailprywatny", "mailprywatny", "prywatnyemail", "adresprywatny", "emaildomowy", "privateemail", "personalemail", "homeemail" })
        {
            data.Add(header, ColumnTargetKind.PatternOnly, NameOrder.GivenNameFirst);
        }

        foreach (var header in new[] { "menedzer", "przelozony", "bezposredniprzelozony", "kierownik", "szef", "manager", "supervisor", "reportsto" })
        {
            data.Add(header, ColumnTargetKind.Manager, NameOrder.GivenNameFirst);
        }

        foreach (var header in new[] { "umowado", "datakoncaumowy", "datazakonczenia", "kontowygasa", "wygasa", "datawygasniecia", "enddate", "contractend", "expires", "expirationdate" })
        {
            data.Add(header, ColumnTargetKind.ExpiresAt, NameOrder.GivenNameFirst);
        }

        foreach (var header in new[] { "kraj", "panstwo", "krajregion", "country", "countryregion" })
        {
            data.Add(header, ColumnTargetKind.Country, NameOrder.GivenNameFirst);
        }

        foreach (var header in new[] { "grupy", "grupa", "czlonkostwo", "grupyad", "groups", "group", "memberof" })
        {
            data.Add(header, ColumnTargetKind.Groups, NameOrder.GivenNameFirst);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AttributeEntries))]
    public void EveryAttributeEntry_IsSuggestedForUsers(string folded, string attribute)
    {
        var suggestion = ColumnSynonyms.Suggest(folded, ObjectKind.User);

        Assert.NotNull(suggestion);
        Assert.Equal(ColumnTargetKind.Attribute, suggestion.Kind);
        Assert.Equal(attribute, suggestion.Attribute);
        Assert.Contains(ColumnSynonyms.Entries, e => e.Folded == folded);
    }

    [Theory]
    [MemberData(nameof(TargetEntries))]
    public void EveryTargetEntry_IsSuggestedForUsers(string folded, ColumnTargetKind kind, NameOrder order)
    {
        var suggestion = ColumnSynonyms.Suggest(folded, ObjectKind.User);

        Assert.NotNull(suggestion);
        Assert.Equal(kind, suggestion.Kind);
        Assert.Null(suggestion.Attribute);
        Assert.Equal(order, suggestion.Order);
    }

    [Theory]
    [InlineData("Tel. kom.", "telkom")]
    [InlineData("Adres e-mail", "adresemail")]
    [InlineData("Telefon komórkowy", "telefonkomorkowy")]
    [InlineData("  IMIĘ  ", "imie")]
    [InlineData("Przełożony (bezpośredni)", "przelozonybezposredni")]
    [InlineData("Nr. telefonu #1", "nrtelefonu1")]
    [InlineData("Żółć-Źdźbło", "zolczdzblo")]
    [InlineData("---", "")]
    public void Fold_LowersRemovesDiacriticsAndPunctuation(string header, string folded) => Assert.Equal(folded, ColumnSynonyms.Fold(header));

    [Theory]
    [InlineData("Telefon komórkowy", "mobile")]
    [InlineData("Tel. kom.", "mobile")]
    [InlineData("Imię", "givenName")]
    [InlineData("Nazwisko", "sn")]
    [InlineData("E-mail", "mail")]
    [InlineData("Adres e-mail", "mail")]
    [InlineData("Stanowisko", "title")]
    [InlineData("Dział", "department")]
    [InlineData("Numer pracownika", "employeeNumber")]
    [InlineData("First Name", "givenName")]
    [InlineData("Last Name", "sn")]
    [InlineData("Email", "mail")]
    [InlineData("Mobile Phone", "mobile")]
    [InlineData("Job Title", "title")]
    [InlineData("Department", "department")]
    [InlineData("Kod pocztowy", "postalCode")]
    [InlineData("Województwo", "st")]
    [InlineData("Strona WWW", "wWWHomePage")]
    public void RealHeaders_AreSuggested(string header, string attribute) => Assert.Equal(attribute, ColumnSynonyms.Suggest(header, ObjectKind.User)?.Attribute);

    [Fact]
    public void RealHeaders_ToTargets()
    {
        Assert.Equal(ColumnTargetKind.Manager, ColumnSynonyms.Suggest("Przełożony", ObjectKind.User)?.Kind);
        Assert.Equal(ColumnTargetKind.FullName, ColumnSynonyms.Suggest("Imię i nazwisko", ObjectKind.User)?.Kind);
        Assert.Equal(NameOrder.SurnameFirst, ColumnSynonyms.Suggest("Nazwisko i imię", ObjectKind.User)?.Order);
        Assert.Equal(ColumnTargetKind.PatternOnly, ColumnSynonyms.Suggest("E-mail prywatny", ObjectKind.User)?.Kind);
        Assert.Equal(ColumnTargetKind.Groups, ColumnSynonyms.Suggest("Grupy AD", ObjectKind.User)?.Kind);
        Assert.Equal(ColumnTargetKind.Country, ColumnSynonyms.Suggest("Kraj/region", ObjectKind.User)?.Kind);
        Assert.Equal(ColumnTargetKind.ExpiresAt, ColumnSynonyms.Suggest("Umowa do", ObjectKind.User)?.Kind);
    }

    [Theory]
    [InlineData("Komórka organizacyjna")]
    [InlineData("Telefon komórkowy służbowy (prywatny)")]
    [InlineData("Imię ojca")]
    [InlineData("Nazwisko panieńskie")]
    [InlineData("Uwagi kadrowe")]
    [InlineData("Data")]
    [InlineData("")]
    public void OnlyExactMatches_AreSuggested(string header)
    {
        var suggestion = ColumnSynonyms.Suggest(header, ObjectKind.User);

        // "Komórka organizacyjna" is an exact entry itself (department); the others have no exact entry.
        if (ColumnSynonyms.Fold(header) == "komorkaorganizacyjna")
        {
            Assert.Equal("department", suggestion?.Attribute);
        }
        else
        {
            Assert.Null(suggestion);
        }
    }

    [Fact]
    public void Komorka_IsDepartment_UnlessItsSamplesArePhones()
    {
        Assert.Equal("department", ColumnSynonyms.Suggest("Komórka", ObjectKind.User)?.Attribute);
        Assert.Equal("department", ColumnSynonyms.Suggest("Komórka", ObjectKind.User, samples: ["Księgowość", "Kadry", "Sprzedaż", "Logistyka"])?.Attribute);
        var phones = ColumnSynonyms.Suggest("Komórka", ObjectKind.User, samples: ["+48 600 100 201", "600 100 202", "48600100203", "+48 22 555 01 01", "Kadry"]);

        Assert.Equal("mobile", phones?.Attribute);
        Assert.Equal(SuggestionNote.MobileNumbersDetected, phones?.Note);
    }

    [Fact]
    public void Telefon_WithMobileNumbers_IsMobile()
    {
        var mobile = ColumnSynonyms.Suggest("Telefon", ObjectKind.User, samples: ["+48 600 100 201", "+48 512 345 678", "", "+48 698 765 432", "600 100 202"]);
        var fixedLine = ColumnSynonyms.Suggest("Telefon", ObjectKind.User, samples: ["+48 22 555 01 01", "+48 22 555 01 02", "+48 600 100 201"]);
        var noSamples = ColumnSynonyms.Suggest("Nr tel.", ObjectKind.User);

        Assert.Equal(new ColumnSuggestion(ColumnTargetKind.Attribute, "mobile", Note: SuggestionNote.MobileNumbersDetected), mobile);
        Assert.Equal("telephoneNumber", fixedLine?.Attribute);
        Assert.Equal(SuggestionNote.None, fixedLine?.Note);
        Assert.Equal("telephoneNumber", noSamples?.Attribute);
    }

    [Fact]
    public void PhonesWithoutPlus_AreNoted()
    {
        var mobile = ColumnSynonyms.Suggest("Telefon komórkowy", ObjectKind.User, samples: ["+48 600 100 201", "600 100 202", "48600100203"]);
        var office = ColumnSynonyms.Suggest("Telefon służbowy", ObjectKind.User, samples: ["48225550101"]);
        var plain = ColumnSynonyms.Suggest("Telefon komórkowy", ObjectKind.User, samples: ["+48 600 100 201"]);

        Assert.Equal(SuggestionNote.PhonesWithoutPlus, mobile?.Note);
        Assert.Equal(SuggestionNote.PhonesWithoutPlus, office?.Note);
        Assert.Equal(SuggestionNote.None, plain?.Note);
    }

    [Fact]
    public void KindDependentEntries()
    {
        Assert.Equal(ColumnTargetKind.FullName, ColumnSynonyms.Suggest("Nazwa", ObjectKind.User)?.Kind);
        Assert.Equal(ColumnTargetKind.FullName, ColumnSynonyms.Suggest("Name", ObjectKind.Contact)?.Kind);
        Assert.Equal("cn", ColumnSynonyms.Suggest("Nazwa", ObjectKind.Group)?.Attribute);
        Assert.Equal("cn", ColumnSynonyms.Suggest("Name", ObjectKind.Computer)?.Attribute);
        Assert.Equal("physicalDeliveryOfficeName", ColumnSynonyms.Suggest("Lokalizacja", ObjectKind.User)?.Attribute);
        Assert.Equal("physicalDeliveryOfficeName", ColumnSynonyms.Suggest("Location", ObjectKind.Contact)?.Attribute);
        Assert.Equal("location", ColumnSynonyms.Suggest("Lokalizacja", ObjectKind.Computer)?.Attribute);
        Assert.Null(ColumnSynonyms.Suggest("Lokalizacja", ObjectKind.Group));
        Assert.Equal(ColumnTargetKind.ExpiresAt, ColumnSynonyms.Suggest("Wygasa", ObjectKind.InetOrgPerson)?.Kind);
        Assert.Null(ColumnSynonyms.Suggest("Wygasa", ObjectKind.Contact));
        Assert.Null(ColumnSynonyms.Suggest("Login", ObjectKind.Contact));
        Assert.Null(ColumnSynonyms.Suggest("Imię", ObjectKind.Group));
        Assert.Null(ColumnSynonyms.Suggest("Telefon komórkowy", ObjectKind.Computer));
        Assert.Equal("description", ColumnSynonyms.Suggest("Opis", ObjectKind.Group)?.Attribute);
        Assert.Equal(ColumnTargetKind.Manager, ColumnSynonyms.Suggest("Kierownik", ObjectKind.Group)?.Kind);
        Assert.Equal(ColumnTargetKind.Groups, ColumnSynonyms.Suggest("Grupy", ObjectKind.Computer)?.Kind);
        Assert.Contains(ColumnSynonyms.Entries, e => e.Folded == "nazwa" && e.OnlyFor == ObjectKind.Group);
    }

    [Theory]
    [InlineData("Hasło")]
    [InlineData("Hasło startowe")]
    [InlineData("nowe hasło")]
    [InlineData("Hasła tymczasowe")]
    [InlineData("Password")]
    [InlineData("TempPassword")]
    [InlineData("passwd")]
    [InlineData("PWD")]
    [InlineData("Kennwort")]
    [InlineData("PIN")]
    [InlineData("Kod PIN")]
    [InlineData("pass")]
    public void PasswordLikeHeaders_AreDetected_AndNeverSuggested(string header)
    {
        Assert.True(ColumnSynonyms.IsPasswordLike(header));
        Assert.Null(ColumnSynonyms.Suggest(header, ObjectKind.User, ["userPassword", "unicodePwd"], ["Start!2026a"]));
    }

    [Theory]
    [InlineData("Pinezka")]
    [InlineData("Passat")]
    [InlineData("Login")]
    [InlineData("Imię")]
    public void OrdinaryHeaders_AreNotPasswordLike(string header) => Assert.False(ColumnSynonyms.IsPasswordLike(header));

    [Theory]
    [InlineData("PESEL")]
    [InlineData("Nr PESEL")]
    [InlineData("NIP")]
    [InlineData("Dowód")]
    [InlineData("Nr dowodu")]
    [InlineData("Data urodzenia")]
    [InlineData("Birthdate")]
    [InlineData("SSN")]
    [InlineData("IBAN")]
    public void PersonalIdentifiers_AreDetected_AndNotSuggested(string header)
    {
        Assert.True(ColumnSynonyms.IsPersonalIdentifier(header));
        Assert.Null(ColumnSynonyms.Suggest(header, ObjectKind.User, ["employeeID"]));
    }

    [Fact]
    public void LdapNamesOfTheCatalogue_AreSuggested_WhenNoSynonymMatches()
    {
        string[] catalogue = ["extensionAttribute5", FakeSchema.CustomBadgeAttribute, "mobile", "givenName", "unicodePwd", "msExchHideFromAddressLists"];

        Assert.Equal("extensionAttribute5", ColumnSynonyms.Suggest("extensionAttribute5", ObjectKind.User, catalogue)?.Attribute);
        Assert.Equal("extensionAttribute5", ColumnSynonyms.Suggest("Extension Attribute 5", ObjectKind.User, catalogue)?.Attribute);
        Assert.Equal(FakeSchema.CustomBadgeAttribute, ColumnSynonyms.Suggest("contoso-badge-number", ObjectKind.User, catalogue)?.Attribute);
        Assert.Null(ColumnSynonyms.Suggest("unicodePwd", ObjectKind.User, catalogue));
        Assert.Null(ColumnSynonyms.Suggest("msExchHideFromAddressLists", ObjectKind.User, catalogue));
        Assert.Null(ColumnSynonyms.Suggest("extensionAttribute5", ObjectKind.User));
    }

    [Fact]
    public void TheCatalogue_FiltersSynonymTargets()
    {
        Assert.Equal("mobile", ColumnSynonyms.Suggest("Telefon komórkowy", ObjectKind.User, ["mobile", "givenName"])?.Attribute);
        Assert.Null(ColumnSynonyms.Suggest("Telefon komórkowy", ObjectKind.User, ["givenName"]));
        Assert.Equal(ColumnTargetKind.Manager, ColumnSynonyms.Suggest("Przełożony", ObjectKind.User, ["givenName"])?.Kind);
    }

    [Fact]
    public void NoBlockedAttribute_IsEverSuggested()
    {
        var schema = FakeSchema.Create(bulkCreate: true);
        foreach (var entry in ColumnSynonyms.Entries.Where(e => e.Target.Attribute is not null))
        {
            Assert.False(CreationValues.IsNamedBlockedAttribute(entry.Target.Attribute!), entry.Target.Attribute);
            var kind = entry.OnlyFor ?? ObjectKind.User;
            if (entry.Target.Attribute is not ("location" or "ipPhone" or "middleName") || kind == ObjectKind.Computer)
            {
                Assert.False(CreationValues.IsBlockedAttribute(entry.Target.Attribute!, kind, schema), $"{entry.Target.Attribute} ({kind})");
            }
        }

        Assert.All(ColumnSynonyms.Entries, e => Assert.Equal(e.Folded, ColumnSynonyms.Fold(e.Folded)));
        Assert.Equal(ColumnSynonyms.Entries.Count, ColumnSynonyms.Entries.Select(e => (e.Folded, e.OnlyFor)).Distinct().Count());
    }

    [Fact]
    public void SuggestFromContent_ForAHeaderlessTable()
    {
        var mail = ColumnSynonyms.SuggestFromContent(["lucja.zak@contoso.com", "grzegorz.bak@contoso.com", "ewa.wisniewska@contoso.test"], ObjectKind.User);
        var mobile = ColumnSynonyms.SuggestFromContent(["+48 600 100 201", "+48 600 100 202", "+48 512 345 678", "+48 698 765 432", "+48 22 555 01 01"], ObjectKind.User);
        var phone = ColumnSynonyms.SuggestFromContent(["+48 22 555 01 01", "+48 22 555 01 02", "+49 30 1234567"], ObjectKind.User);

        Assert.Equal(new ColumnSuggestion(ColumnTargetKind.Attribute, "mail", Note: SuggestionNote.ContentBased), mail);
        Assert.Equal(new ColumnSuggestion(ColumnTargetKind.Attribute, "mobile", Note: SuggestionNote.ContentBased), mobile);
        Assert.Equal(new ColumnSuggestion(ColumnTargetKind.Attribute, "telephoneNumber", Note: SuggestionNote.ContentBased), phone);
        Assert.Null(ColumnSynonyms.SuggestFromContent(["Łucja", "Grzegorz", "Ewa"], ObjectKind.User));
        Assert.Null(ColumnSynonyms.SuggestFromContent(["lucja.zak@contoso.com", "Grzegorz", "Ewa"], ObjectKind.User));
        Assert.Null(ColumnSynonyms.SuggestFromContent(["", " "], ObjectKind.User));
        Assert.Null(ColumnSynonyms.SuggestFromContent(["+48 600 100 201"], ObjectKind.Computer));
        Assert.Equal("mail", ColumnSynonyms.SuggestFromContent(["gg_nowi@contoso.com"], ObjectKind.Group)?.Attribute);
    }

    [Fact]
    public void ToString_NamesTheTargetOnly()
    {
        var entry = ColumnSynonyms.Entries.First(e => e.Folded == "telefonkomorkowy");

        Assert.DoesNotContain("telefonkomorkowy", entry.ToString(), StringComparison.Ordinal);
        Assert.Contains("mobile", entry.ToString(), StringComparison.Ordinal);
    }
}
