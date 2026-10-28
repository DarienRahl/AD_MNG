using AdMng.Core.Correspondence.Import;

namespace AdMng.Core.Tests.Correspondence.Import;

public class ContactExtractorTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Rows(params string[][] rows) => rows;

    // ---- free text (MailerWPiA cases) ----------------------------------------------------------------------------------

    [Fact]
    public void FromText_NamedPairsAndBareAddresses_InTheOrderTheyAppear()
    {
        var result = ContactExtractor.FromText(
            "luzem@gmina.pl\nJan Kowalski <jan.kowalski@urzad.pl>\nAnna Nowak (ANNA@starostwo.pl)\n\"Zakład Wodny\" <biuro@wody.pl>");
        Assert.Equal(ContactSourceFormat.Text, result.Format);
        Assert.Empty(result.Columns);
        Assert.Equal(["luzem@gmina.pl", "jan.kowalski@urzad.pl", "anna@starostwo.pl", "biuro@wody.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal(["", "Jan Kowalski", "Anna Nowak", "Zakład Wodny"], result.Contacts.Select(c => c.Name));
        Assert.All(result.Contacts, c => Assert.Empty(c.Fields));
    }

    [Fact]
    public void FromText_KontaktPrefixIsDropped() =>
        Assert.Equal("Łukasz Wiśniewski", Assert.Single(ContactExtractor.FromText("Kontakt: Łukasz Wiśniewski <lw@urzad.pl>").Contacts).Name);

    [Fact]
    public void FromText_LaterNameFillsAnEarlierBareAddress_AndCountsTheDuplicate()
    {
        var result = ContactExtractor.FromText("Pisz na jan@urzad.pl. Podpisano: Jan Kowalski <JAN@urzad.pl>");
        var contact = Assert.Single(result.Contacts);
        Assert.Equal("Jan Kowalski", contact.Name);
        Assert.Equal(1, result.Duplicates);
    }

    [Fact]
    public void FromText_AnAddressInsideTheNamePartOfAPair_IsKept()
    {
        var result = ContactExtractor.FromText("rada@gmina.pl i Biuro Rady <biuro@gmina.pl>");
        Assert.Equal(["rada@gmina.pl", "biuro@gmina.pl"], result.Contacts.Select(c => c.Email));
        Assert.All(result.Contacts, c => Assert.Equal(string.Empty, c.Name));   // MailerWPiA: a name holding an address is no name
    }

    [Fact]
    public void FromText_ARowOfAddressesIsNoHeader()
    {
        // "anna@mail.pl" contains "mail" but is an address: no table, every address is taken.
        var result = ContactExtractor.FromText("jan@urzad.pl, anna@mail.pl\nbeata@urzad.pl, celina@urzad.pl");
        Assert.Equal(ContactSourceFormat.Text, result.Format);
        Assert.Equal(4, result.Contacts.Count);
    }

    [Fact]
    public void FromText_JunkAndInvalidAreSkippedWithoutCounting()
    {
        var result = ContactExtractor.FromText("a@b, kontakt@example.com, ok@gmina.pl, x@sentry.io");
        Assert.Equal("ok@gmina.pl", Assert.Single(result.Contacts).Email);
        Assert.Equal(0, result.InvalidAddresses);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void FromText_Empty(string? text) => Assert.Same(ContactImportResult.Empty, ContactExtractor.FromText(text));

    // ---- delimited tables ----------------------------------------------------------------------------------------------

    [Fact]
    public void FromText_SemicolonTable_EveryColumnIsAField()
    {
        var result = ContactExtractor.FromText(
            "Nazwa;E-mail;Miejscowość;Kod pocztowy\r\nUrząd Gminy Łęczyca;ug@leczyca.pl;Łęczyca;99-100\r\nStarostwo Żagań; SP@ZAGAN.PL ;Żagań;68-100\r\n");
        Assert.Equal(ContactSourceFormat.Delimited, result.Format);
        Assert.Equal(["Nazwa", "E-mail", "Miejscowość", "Kod pocztowy"], result.Columns);
        Assert.Equal(["ug@leczyca.pl", "sp@zagan.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal(["Urząd Gminy Łęczyca", "Starostwo Żagań"], result.Contacts.Select(c => c.Name));
        var fields = result.Contacts[1].Fields;
        Assert.Equal(["Nazwa", "E-mail", "Miejscowość", "Kod pocztowy"], fields.Keys);
        Assert.Equal("SP@ZAGAN.PL", fields["E-mail"]);
        Assert.Equal("Żagań", fields["Miejscowość"]);
    }

    [Fact]
    public void FromText_TabTable() =>
        Assert.Equal("Gmina Ząbki", Assert.Single(ContactExtractor.FromText("Odbiorca\tAdres e-mail\nGmina Ząbki\tgmina@zabki.pl").Contacts).Name);

    [Fact]
    public void FromText_CommaTable_WithQuotedCells()
    {
        var result = ContactExtractor.FromText("Firma,Email,Uwagi\n\"Kowalski, Nowak i S-ka\",biuro@kn.pl,\"powiedział \"\"tak\"\"\"\n");
        var contact = Assert.Single(result.Contacts);
        Assert.Equal("Kowalski, Nowak i S-ka", contact.Name);
        Assert.Equal("powiedział \"tak\"", contact.Fields["Uwagi"]);
    }

    [Fact]
    public void FromText_QuotedCellWithLineBreak()
    {
        var contact = Assert.Single(ContactExtractor.FromText("Nazwa;E-mail\n\"Urząd Miasta\nwydział kultury\";kultura@miasto.pl\n").Contacts);
        Assert.Equal("Urząd Miasta wydział kultury", contact.Name);
        Assert.Equal("Urząd Miasta\nwydział kultury", contact.Fields["Nazwa"]);
    }

    [Fact]
    public void FromText_DelimiterOrder_SemicolonBeforeComma()
    {
        // With ',' the name "Kowalski, Jan" would be split; ';' is tried first and gives two cells.
        var contact = Assert.Single(ContactExtractor.FromText("Nazwa;E-mail\nKowalski, Jan;jan@urzad.pl").Contacts);
        Assert.Equal("Kowalski, Jan", contact.Name);
    }

    [Fact]
    public void FromText_NextDelimiterWhenTheFirstTableHasNoContacts()
    {
        // The ';' table has an e-mail column without a single address; the ',' table has one.
        var result = ContactExtractor.FromText("Nazwa,Kontakt;E-mail\nJan,jan@urzad.pl;-");
        Assert.Equal(ContactSourceFormat.Delimited, result.Format);
        Assert.Equal("Jan", Assert.Single(result.Contacts).Name);
    }

    [Fact]
    public void FromText_HeaderWithinTheFirstEightRows_PreambleIsFreeText()
    {
        var text = "Lista adresowa 2026\n\nPytania: Biuro Rady <rada@gmina.pl>\nstan na 28.10.2026\n\nNazwa;E-mail\nSołectwo Ździechów;soltys@zdziechow.pl\n";
        var result = ContactExtractor.FromText(text);
        Assert.Equal(["rada@gmina.pl", "soltys@zdziechow.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal("Biuro Rady", result.Contacts[0].Name);
        Assert.Empty(result.Contacts[0].Fields);
        Assert.Equal("Sołectwo Ździechów", result.Contacts[1].Fields["Nazwa"]);
    }

    [Fact]
    public void FromText_HeaderAfterTheEighthRow_IsNotATable()
    {
        var preamble = string.Concat(Enumerable.Range(1, 8).Select(i => $"wiersz {i}\n"));
        var result = ContactExtractor.FromText(preamble + "Nazwa;E-mail\nGmina;gmina@urzad.pl\n");
        Assert.Equal(ContactSourceFormat.Text, result.Format);
        Assert.Empty(result.Columns);
        Assert.Equal("gmina@urzad.pl", Assert.Single(result.Contacts).Email);
    }

    [Fact]
    public void FromText_HeaderNeedsTwoCells()
    {
        var result = ContactExtractor.FromText("E-mail\njan@urzad.pl\n");
        Assert.Equal(ContactSourceFormat.Text, result.Format);
        Assert.Equal("jan@urzad.pl", Assert.Single(result.Contacts).Email);
    }

    [Fact]
    public void FromText_EmailBeatsInneEmaile_WhateverTheColumnOrder()
    {
        var result = ContactExtractor.FromText("Inne e-maile;Nazwa;E-mail\ninny@urzad.pl;Gmina Łódź;glowny@urzad.pl\n");
        var contact = Assert.Single(result.Contacts);
        Assert.Equal("glowny@urzad.pl", contact.Email);
        Assert.Equal("inny@urzad.pl", contact.Fields["Inne e-maile"]);
    }

    [Fact]
    public void FromText_ContainsMail_WhenNoExactHeader()
    {
        var contact = Assert.Single(ContactExtractor.FromText("Instytucja;Służbowy e-mail\nSąd Rejonowy;sad@sr.gov.pl").Contacts);
        Assert.Equal(("sad@sr.gov.pl", "Sąd Rejonowy"), (contact.Email, contact.Name));
    }

    [Fact]
    public void FromText_EmailBeatsPoczta()
    {
        var contact = Assert.Single(ContactExtractor.FromText("Nazwa;Poczta;E-mail\nGmina Zębowice;Zębowice;gmina@zebowice.pl").Contacts);
        Assert.Equal("gmina@zebowice.pl", contact.Email);
        Assert.Equal("Zębowice", contact.Fields["Poczta"]);
    }

    [Fact]
    public void FromText_EmptyEmailColumn_FallsBackToAnotherEmailColumnWithAddresses()
    {
        var contact = Assert.Single(ContactExtractor.FromText("Nazwa;E-mail;Inne e-maile\nGmina;;zastepczy@gmina.pl").Contacts);
        Assert.Equal("zastepczy@gmina.pl", contact.Email);
    }

    [Theory]
    [InlineData("Imię;Nazwisko;E-mail\nJan;Kowalski;jan@x.pl", "Jan Kowalski")]
    [InlineData("Nazwisko;Imię;E-mail\nKowalska;Żaneta;z@x.pl", "Żaneta Kowalska")]
    [InlineData("First name;Last name;Email\nAnna;Nowak;a@x.pl", "Anna Nowak")]
    [InlineData("Imie;Nazwisko;E-mail\n;Kowalski;k@x.pl", "Kowalski")]
    [InlineData("Imię;Nazwisko;Nazwa;E-mail\nJan;Kowalski;Biuro Jana;j@x.pl", "Biuro Jana")]
    [InlineData("Imię;E-mail\nJan;j@x.pl", "")]
    [InlineData("Login;E-mail\njkowalski;j@x.pl", "")]
    public void FromText_NameColumnRules(string text, string expected) =>
        Assert.Equal(expected, Assert.Single(ContactExtractor.FromText(text).Contacts).Name);

    [Theory]
    [InlineData("Pełna nazwa;E-mail\nUrząd Skarbowy w Łodzi;us@x.pl", "Urząd Skarbowy w Łodzi")]
    [InlineData("Imię i nazwisko;E-mail\nJan Kowalski;j@x.pl", "Jan Kowalski")]
    [InlineData("Lp;Urząd;E-mail\n1;Urząd Gminy;u@x.pl", "Urząd Gminy")]
    [InlineData("Adresat;E-mail\nPani Dyrektor;d@x.pl", "Pani Dyrektor")]
    [InlineData("Display name;Email\nJan K.;j@x.pl", "Jan K.")]
    [InlineData("Odbiorca;E-mail\n  •  Kontakt: Ewa Zielińska ;e@x.pl", "Ewa Zielińska")]
    [InlineData("Kontakt;E-mail\nEwa Zielińska;e@x.pl", "")]
    public void FromText_NameHeaders(string text, string expected) =>
        Assert.Equal(expected, Assert.Single(ContactExtractor.FromText(text).Contacts).Name);

    [Fact]
    public void FromText_RepeatedAndEmptyHeaders()
    {
        var result = ContactExtractor.FromText("Nazwa;E-mail;Telefon;Telefon;;kod-pocztowy;Kod pocztowy;\nGmina;g@x.pl;1;2;uwaga;00-001;00-002;\n");
        Assert.Equal(["Nazwa", "E-mail", "Telefon", "Telefon (2)", "Kolumna 5", "kod-pocztowy", "Kod pocztowy (2)"], result.Columns);
        var fields = Assert.Single(result.Contacts).Fields;
        Assert.Equal("2", fields["Telefon (2)"]);
        Assert.Equal("uwaga", fields["Kolumna 5"]);
        Assert.Equal("00-002", fields["Kod pocztowy (2)"]);
    }

    [Fact]
    public void FromText_AtMost256Columns()
    {
        var header = "E-mail;" + string.Join(";", Enumerable.Range(1, 400).Select(i => $"K{i}"));
        var row = "a@x.pl;" + string.Join(";", Enumerable.Range(1, 400).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var result = ContactExtractor.FromText(header + "\n" + row);
        Assert.Equal(256, result.Columns.Count);
        Assert.Equal(256, Assert.Single(result.Contacts).Fields.Count);
    }

    [Fact]
    public void FromText_SeveralAddressesInACell_TakesTheFirstValid()
    {
        var contact = Assert.Single(ContactExtractor.FromText("Nazwa;E-mail\nGmina;brak, sekretariat@gmina.pl, wojt@gmina.pl").Contacts);
        Assert.Equal("sekretariat@gmina.pl", contact.Email);
        Assert.Equal("brak, sekretariat@gmina.pl, wojt@gmina.pl", contact.Fields["E-mail"]);
    }

    [Fact]
    public void FromText_CountsInvalidRowsAndDuplicates_IgnoresBlankRows()
    {
        var result = ContactExtractor.FromText("Nazwa;E-mail\nA;a@x.pl\nB;brak adresu\n;;\n\nC;A@X.PL\nD;\nE;e@x.pl\n");
        Assert.Equal(["a@x.pl", "e@x.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal(2, result.InvalidAddresses);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal("A", result.Contacts[0].Name);
    }

    [Fact]
    public void FromText_ShortRows_GetEmptyFields()
    {
        var contact = Assert.Single(ContactExtractor.FromText("E-mail;Nazwa;Miasto\nj@x.pl").Contacts);
        Assert.Equal(string.Empty, contact.Fields["Miasto"]);
        Assert.Equal(string.Empty, contact.Name);
    }

    [Fact]
    public void FromText_TableWithoutAnyAddress_IsFreeTextWithTheInvalidCount()
    {
        var result = ContactExtractor.FromText("Nazwa;E-mail\nA;brak\nB;nie ma\n");
        Assert.Empty(result.Contacts);
        Assert.Equal(2, result.InvalidAddresses);
    }

    // ---- rows ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void FromRows_SingleColumnHeaderIsEnough()
    {
        var result = ContactExtractor.FromRows(Rows(["E-mail"], ["jan@urzad.pl"], ["anna@urzad.pl"]), ContactSourceFormat.Excel);
        Assert.Equal(ContactSourceFormat.Excel, result.Format);
        Assert.Equal(["E-mail"], result.Columns);
        Assert.Equal(2, result.Contacts.Count);
    }

    [Fact]
    public void FromRows_HeaderAfterBlankRows()
    {
        var result = ContactExtractor.FromRows(
            Rows([], ["", ""], ["Wykaz"], ["Nazwa", "E-mail"], ["Gmina Łomianki", "gmina@lomianki.pl"]), ContactSourceFormat.Word);
        Assert.Equal("Gmina Łomianki", Assert.Single(result.Contacts).Name);
    }

    [Fact]
    public void FromRows_RowsAboveTheHeaderAreFreeText()
    {
        var result = ContactExtractor.FromRows(
            Rows(["Wykaz – pytania: Biuro Rady <rada@gmina.pl>"], ["Nazwa", "E-mail"], ["Sołectwo Łąki", "soltys@laki.pl"]), ContactSourceFormat.Excel);
        Assert.Equal(["rada@gmina.pl", "soltys@laki.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal("Biuro Rady", result.Contacts[0].Name);
        Assert.Equal(["Nazwa", "E-mail"], result.Columns);
        Assert.Equal(ContactSourceFormat.Excel, result.Format);
    }

    [Fact]
    public void FromRows_WithoutHeader_CellsAreFreeText()
    {
        var result = ContactExtractor.FromRows(Rows(["Jan Kowalski", "jan@urzad.pl"], ["Anna", "Anna Nowak <anna@urzad.pl>"]), ContactSourceFormat.Html);
        Assert.Equal(ContactSourceFormat.Html, result.Format);
        Assert.Empty(result.Columns);
        Assert.Equal(["jan@urzad.pl", "anna@urzad.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal("Anna Nowak", result.Contacts[1].Name);
    }

    [Fact]
    public void FromRows_ColumnsWithoutHeaderOrValueAreSkipped()
    {
        var result = ContactExtractor.FromRows(Rows(["Nazwa", "", "E-mail", ""], ["Gmina", "", "g@x.pl", ""]), ContactSourceFormat.Excel);
        Assert.Equal(["Nazwa", "E-mail"], result.Columns);
    }

    [Fact]
    public void FromRows_ExtraCellsBeyondTheHeader_AreUnnamedColumns()
    {
        var result = ContactExtractor.FromRows(Rows(["Nazwa", "E-mail"], ["Gmina", "g@x.pl", "", "notatka"]), ContactSourceFormat.Excel);
        Assert.Equal(["Nazwa", "E-mail", "Kolumna 4"], result.Columns);
        Assert.Equal("notatka", Assert.Single(result.Contacts).Fields["Kolumna 4"]);
    }

    [Fact]
    public void ToString_NeverShowsAddresses()
    {
        var result = ContactExtractor.FromText("Jan Kowalski <jan@urzad.pl>");
        Assert.Equal("1 contacts", result.ToString());
        Assert.Equal(nameof(ImportedContact), result.Contacts[0].ToString());
    }
}
