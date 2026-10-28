using AdMng.Core.ActiveDirectory;
using AdMng.Core.Correspondence.Mailboxes;
using static AdMng.Core.Tests.Correspondence.Mailboxes.MailboxTestData;

namespace AdMng.Core.Tests.Correspondence.Mailboxes;

/// <summary>The owner's name on a mailbox account: givenName/sn, display name, description or another attribute.</summary>
public class OwnerNameParserTests
{
    private static OwnerName? FromNames(string? givenName, string? surname)
    {
        var values = new List<(string, object)>();
        if (givenName is not null)
        {
            values.Add((Attr.GivenName, givenName));
        }

        if (surname is not null)
        {
            values.Add((Attr.Sn, surname));
        }

        return OwnerNameParser.Parse(Account("Skrzynka", [.. values]), OwnerNameSource.GivenNameAndSurname, null);
    }

    private static OwnerName? FromDisplayName(string displayName) =>
        OwnerNameParser.Parse(Account("Skrzynka", (Attr.DisplayName, displayName)), OwnerNameSource.DisplayName, null);

    private static void AssertName(OwnerName? name, string givenName, string surname, string text)
    {
        Assert.NotNull(name);
        Assert.Equal(givenName, name.GivenName);
        Assert.Equal(surname, name.Surname);
        Assert.Equal(text, name.Text);
        Assert.False(name.IsEmpty);
    }

    [Fact]
    public void GivenNameAndSurname_TakesBothAttributes()
    {
        AssertName(FromNames("Jan", "Kowalski"), "Jan", "Kowalski", "Jan Kowalski");
        AssertName(FromNames("  Małgorzata  ", " Wójcik "), "Małgorzata", "Wójcik", "Małgorzata Wójcik");
        AssertName(FromNames("Anna Maria", "Kowalska-Nowak"), "Anna Maria", "Kowalska-Nowak", "Anna Maria Kowalska-Nowak");
        AssertName(FromNames("Wójcik", "Małgorzata"), "Wójcik", "Małgorzata", "Wójcik Małgorzata");
    }

    [Fact]
    public void GivenNameAndSurname_OneAttributeWithBothWords_IsSplit()
    {
        AssertName(FromNames("Jan Kowalski", null), "Jan", "Kowalski", "Jan Kowalski");
        AssertName(FromNames(null, "Jan Kowalski"), "Jan", "Kowalski", "Jan Kowalski");
        AssertName(FromNames("", "Anna Maria Kowalska-Nowak"), "Anna Maria", "Kowalska-Nowak", "Anna Maria Kowalska-Nowak");
        AssertName(FromNames(null, "dr Anna Nowak"), "Anna", "Nowak", "Anna Nowak");
    }

    [Fact]
    public void GivenNameAndSurname_OneWord_KeepsTheMeaningOfItsAttribute()
    {
        AssertName(FromNames("Jan", null), "Jan", string.Empty, "Jan");
        AssertName(FromNames(" ", "Kowalski"), string.Empty, "Kowalski", "Kowalski");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "\t")]
    [InlineData("(zastępstwo)", null)]
    public void GivenNameAndSurname_WithoutAName_IsNull(string? givenName, string? surname) => Assert.Null(FromNames(givenName, surname));

    [Fact]
    public void GivenNameAndSurname_RemovesTitlesAndRemarks()
    {
        AssertName(FromNames("dr Anna", "Nowak"), "Anna", "Nowak", "Anna Nowak");
        AssertName(FromNames("Ewa", "Zielińska, prof. UW"), "Ewa", "Zielińska", "Ewa Zielińska");
        AssertName(FromNames("Jan", "Kowalski (zastępstwo)"), "Jan", "Kowalski", "Jan Kowalski");
        AssertName(FromNames("Jan (IT)", "Kowalski"), "Jan", "Kowalski", "Jan Kowalski");

        // A title alone is no title to remove.
        AssertName(FromNames("Jan", "Ks"), "Jan", "Ks", "Jan Ks");
    }

    [Theory]
    [InlineData("Jan Kowalski", "Jan", "Kowalski")]
    [InlineData("Skrzynka: Jan Kowalski", "Jan", "Kowalski")]
    [InlineData("Skrzynka: dr Jan Kowalski", "Jan", "Kowalski")]
    [InlineData("Opis: skrzynka: Jan Kowalski", "Jan", "Kowalski")]
    [InlineData("Projekt UE – Karolina Malinowska", "Karolina", "Malinowska")]
    [InlineData("Projekt UE - Karolina Malinowska", "Karolina", "Malinowska")]
    [InlineData("Projekt UE — Karolina Malinowska", "Karolina", "Malinowska")]
    [InlineData("dr hab. Ewa Zielińska, prof. UW", "Ewa", "Zielińska")]
    [InlineData("prof. dr hab. Stanisław Kowalczyk", "Stanisław", "Kowalczyk")]
    [InlineData("mgr inż. Piotr Wiśniewski", "Piotr", "Wiśniewski")]
    [InlineData("Jan Kowalski (zastępstwo)", "Jan", "Kowalski")]
    [InlineData("Jan Kowalski (IT: helpdesk)", "Jan", "Kowalski")]
    [InlineData("Skrzynka (stara): Jan Kowalski", "Jan", "Kowalski")]
    [InlineData("Jan Kowalski (zastępstwo", "Jan", "Kowalski")]
    [InlineData("Jan Kowalski ((Kraków) IT)", "Jan", "Kowalski")]
    [InlineData("„Jan Kowalski”", "Jan", "Kowalski")]
    [InlineData("\"Jan Kowalski\"", "Jan", "Kowalski")]
    [InlineData("  Jan \t Kowalski \r\n", "Jan", "Kowalski")]
    [InlineData("Anna Maria Kowalska-Nowak", "Anna Maria", "Kowalska-Nowak")]
    [InlineData("Pomoc techniczna", "Pomoc", "techniczna")]
    [InlineData("Jan Kowalski:", "Jan", "Kowalski")]
    public void Text_TitlesRemarksAndPrefixesRemoved_LastWordIsTheSurname(string text, string givenName, string surname) =>
        AssertName(FromDisplayName(text), givenName, surname, $"{givenName} {surname}");

    [Theory]
    [InlineData("Kowalski", "Kowalski")]
    [InlineData("Księgowość – faktury", "faktury")]
    [InlineData("Skrzynka:", "Skrzynka")]
    [InlineData("dr Kowalski", "Kowalski")]
    public void Text_OneWord_IsTheSurnameOnly(string text, string surname) =>
        AssertName(FromDisplayName(text), string.Empty, surname, surname);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(tylko uwaga)")]
    [InlineData(" - ")]
    [InlineData("„ ”")]
    public void Text_WithoutAName_IsNull(string text) => Assert.Null(FromDisplayName(text));

    [Fact]
    public void Text_IsCutAtTheMaximumLength()
    {
        var text = string.Join(' ', Enumerable.Repeat("Słowo", 500)) + " Kowalski";

        var name = OwnerNameParser.ParseText(text);

        Assert.NotNull(name);
        Assert.True(name.Text.Length <= OwnerNameParser.MaxTextLength);
        Assert.Equal("Słowo", name.Surname);
    }

    [Fact]
    public void DescriptionAndAttributeSources()
    {
        var mailbox = Account("Archiwum",
            (Attr.Description, new[] { "Właściciel: Piotr Wiśniewski", "druga wartość" }),
            ("extensionAttribute1", "dr Anna Nowak"),
            (Attr.DisplayName, "Archiwum"));

        AssertName(OwnerNameParser.Parse(mailbox, OwnerNameSource.Description, null), "Piotr", "Wiśniewski", "Piotr Wiśniewski");
        AssertName(OwnerNameParser.Parse(mailbox, OwnerNameSource.Attribute, " extensionAttribute1 "), "Anna", "Nowak", "Anna Nowak");
        AssertName(OwnerNameParser.Parse(mailbox, OwnerNameSource.DisplayName, "extensionAttribute1"), string.Empty, "Archiwum", "Archiwum");
        Assert.Null(OwnerNameParser.Parse(mailbox, OwnerNameSource.Attribute, "extensionAttribute2"));
        Assert.Null(OwnerNameParser.Parse(mailbox, OwnerNameSource.Attribute, null));
        Assert.Null(OwnerNameParser.Parse(mailbox, OwnerNameSource.Attribute, "  "));
        Assert.Null(OwnerNameParser.Parse(mailbox, OwnerNameSource.Attribute, "1extensionAttribute1"));
        Assert.Null(OwnerNameParser.Parse(Account("Tajna", ("userPassword", "Jan Kowalski")), OwnerNameSource.Attribute, "userPassword"));
        Assert.Null(OwnerNameParser.Parse(Account("Pusta"), OwnerNameSource.Description, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnerNameParser.Parse(mailbox, (OwnerNameSource)9, null));
        Assert.Throws<ArgumentNullException>(() => OwnerNameParser.Parse(null!, OwnerNameSource.DisplayName, null));
    }

    [Fact]
    public void AttributesFor_EachSource()
    {
        Assert.Equal([Attr.GivenName, Attr.Sn], OwnerNameParser.AttributesFor(OwnerNameSource.GivenNameAndSurname, "info"));
        Assert.Equal([Attr.DisplayName], OwnerNameParser.AttributesFor(OwnerNameSource.DisplayName, null));
        Assert.Equal([Attr.Description], OwnerNameParser.AttributesFor(OwnerNameSource.Description, null));
        Assert.Equal(["extensionAttribute1"], OwnerNameParser.AttributesFor(OwnerNameSource.Attribute, " extensionAttribute1 "));
        Assert.Empty(OwnerNameParser.AttributesFor(OwnerNameSource.Attribute, "mail;binary"));
        Assert.Empty(OwnerNameParser.AttributesFor(OwnerNameSource.Attribute, "(mail=*)"));
        Assert.Empty(OwnerNameParser.AttributesFor(OwnerNameSource.Attribute, null));
        Assert.Empty(OwnerNameParser.AttributesFor(OwnerNameSource.Attribute, "userPassword"));
    }

    [Theory]
    [InlineData("info", true)]
    [InlineData("extensionAttribute15", true)]
    [InlineData("msDS-cloudExtensionAttribute1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("1info", false)]
    [InlineData("in fo", false)]
    [InlineData("info;binary", false)]
    [InlineData("opis_skrzynki", false)]
    [InlineData("imię", false)]
    [InlineData("unicodePwd", false)]
    [InlineData("userPassword", false)]
    [InlineData("msDS-ManagedPassword", false)]
    [InlineData("ms-Mcs-AdmPwd", false)]
    public void IsUsableAttribute(string? attribute, bool expected) => Assert.Equal(expected, OwnerNameParser.IsUsableAttribute(attribute));

    [Fact]
    public void OwnerName_IsEmpty_AndToStringHidesTheName()
    {
        Assert.True(new OwnerName("", " ", "").IsEmpty);
        Assert.False(new OwnerName("", "Kowalski", "Kowalski").IsEmpty);
        Assert.False(new OwnerName("Jan", "", "Jan").IsEmpty);
        var name = Name("Łukasz", "Wiśniewski");
        Assert.Equal(nameof(OwnerName), name.ToString());
        Assert.DoesNotContain("Łukasz", name.ToString(), StringComparison.Ordinal);
    }
}
