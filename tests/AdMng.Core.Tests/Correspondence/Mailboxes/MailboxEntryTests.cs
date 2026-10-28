using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Testing;
using static AdMng.Core.Tests.Correspondence.Mailboxes.MailboxTestData;

namespace AdMng.Core.Tests.Correspondence.Mailboxes;

/// <summary>Mailboxes and persons from directory objects, and the attributes they need.</summary>
public class MailboxEntryTests
{
    private const string Tomasz = "CN=Tomasz Wójcik,OU=Zwroty grzecznościowe,OU=Firma,DC=contoso,DC=test";
    private const string Grazyna = "CN=Grażyna Dąbrowska,OU=Księgowość,OU=Pracownicy,OU=Firma,DC=contoso,DC=test";

    private static string Uac(UserAccountControl flags) => ((int)flags).ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void Mailbox_From_ReadsNameAddressTypeDelegatesAndState()
    {
        var mailbox = MailboxEntry.From(Account("Wydarzenia",
            (Attr.DisplayName, " Wydarzenia "),
            (Attr.Mail, "wydarzenia@contoso.test"),
            (MailboxTypes.RecipientTypeDetails, "4"),
            (MailboxTypes.DelegateListLink, new[] { Tomasz, Grazyna }),
            (MailboxTypes.PublicDelegates, new[] { Tomasz.ToUpperInvariant(), " " }),
            (Attr.UserAccountControl, Uac(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable))));

        Assert.Equal($"CN=Wydarzenia,{MailboxOu}", mailbox.DistinguishedName);
        Assert.Equal("Wydarzenia", mailbox.Name);
        Assert.Equal("wydarzenia@contoso.test", mailbox.Address);
        Assert.Equal(MailboxType.Shared, mailbox.Type);
        Assert.Equal([Tomasz, Grazyna], mailbox.DelegateDns);
        Assert.True(mailbox.IsDisabled);
        Assert.Equal(nameof(MailboxEntry), mailbox.ToString());
    }

    [Fact]
    public void Mailbox_From_FallsBackToTheRdnAndThePrimaryProxyAddress()
    {
        var mailbox = MailboxEntry.From(Account("Księgowość – faktury",
            (Attr.Mail, "nie adres"),
            (Attr.ProxyAddresses, new[] { "smtp:faktury.stare@contoso.test", "SMTP:faktury@contoso.test", "X500:/o=Contoso" })));

        Assert.Equal("Księgowość – faktury", mailbox.Name);
        Assert.Equal("faktury@contoso.test", mailbox.Address);
        Assert.Equal(MailboxType.None, mailbox.Type);
        Assert.Empty(mailbox.DelegateDns);
        Assert.False(mailbox.IsDisabled);
        Assert.Null(MailboxEntry.From(Account("Bez adresu", (Attr.ProxyAddresses, new[] { "smtp:tylko.dodatkowy@contoso.test" }))).Address);
        Assert.Throws<ArgumentNullException>(() => MailboxEntry.From(null!));
    }

    [Fact]
    public void Person_From_ReadsEverythingTheMatcherAndTheReviewNeed()
    {
        var person = PersonCandidate.From(Object($"CN=Agnieszka Żółć,{PeopleOu}",
            (Attr.GivenName, " Agnieszka "),
            (Attr.Sn, "Żółć"),
            (Attr.DisplayName, "Agnieszka Żółć"),
            (Attr.ProxyAddresses, new[] { "SMTP:agnieszka.zolc@contoso.test" }),
            (Attr.Department, "Sprzedaż"),
            (Attr.Title, "Asystentka"),
            (Attr.SamAccountName, "agnieszka.zolc"),
            (Attr.UserAccountControl, Uac(UserAccountControl.NormalAccount)),
            (MailboxTypes.RecipientTypeDetails, "1")));

        Assert.Equal("Agnieszka", person.GivenName);
        Assert.Equal("Żółć", person.Surname);
        Assert.Equal("Agnieszka Żółć", person.DisplayName);
        Assert.Equal("agnieszka.zolc@contoso.test", person.Address);
        Assert.False(person.IsDisabled);
        Assert.Equal(MailboxType.User, person.Type);
        Assert.Equal("Sprzedaż", person.Department);
        Assert.Equal("Asystentka", person.Title);
        Assert.Equal("agnieszka.zolc", person.Login);
        Assert.Equal(nameof(PersonCandidate), person.ToString());
    }

    [Fact]
    public void Person_From_MissingValues()
    {
        var person = PersonCandidate.From(Object($"CN=Zbigniew Kaźmierczak,{PeopleOu}",
            (Attr.Mail, "zbigniew.kazmierczak@contoso.test"),
            (Attr.Department, "  "),
            (Attr.UserAccountControl, Uac(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable))));

        Assert.Equal(string.Empty, person.GivenName);
        Assert.Equal(string.Empty, person.Surname);
        Assert.Equal("Zbigniew Kaźmierczak", person.DisplayName);
        Assert.Equal("zbigniew.kazmierczak@contoso.test", person.Address);
        Assert.True(person.IsDisabled);
        Assert.Equal(MailboxType.None, person.Type);
        Assert.Null(person.Department);
        Assert.Null(person.Title);
        Assert.Null(person.Login);
        Assert.Throws<ArgumentNullException>(() => PersonCandidate.From(null!));
    }

    [Fact]
    public void AttributesFor_AskForExchangeAttributesOnlyWhenTheSchemaHasThem()
    {
        var withExchange = MailboxEntry.AttributesFor(FakeSchema.Create());
        var without = MailboxEntry.AttributesFor(FakeSchema.CreateWithoutMailboxes());
        var noSchema = MailboxEntry.AttributesFor(null);

        Assert.Contains(MailboxTypes.RecipientTypeDetails, withExchange);
        Assert.Contains(MailboxTypes.DelegateListLink, withExchange);
        Assert.Contains(MailboxTypes.PublicDelegates, withExchange);
        Assert.Contains(Attr.Mail, without);
        Assert.Contains(Attr.ProxyAddresses, without);
        Assert.Contains(Attr.UserAccountControl, without);
        Assert.DoesNotContain(MailboxTypes.RecipientTypeDetails, without);
        Assert.DoesNotContain(MailboxTypes.DelegateListLink, without);
        Assert.Equal(withExchange, noSchema);

        Assert.Contains(MailboxTypes.RecipientTypeDetails, PersonCandidate.AttributesFor(FakeSchema.Create()));
        Assert.DoesNotContain(MailboxTypes.RecipientTypeDetails, PersonCandidate.AttributesFor(FakeSchema.CreateWithoutMailboxes()));
        Assert.Superset(
            new HashSet<string>([Attr.GivenName, Attr.Sn, Attr.DisplayName, Attr.Mail, Attr.ProxyAddresses, Attr.UserAccountControl, Attr.Department, Attr.Title, Attr.SamAccountName]),
            new HashSet<string>(PersonCandidate.AttributesFor(null)));
    }
}
