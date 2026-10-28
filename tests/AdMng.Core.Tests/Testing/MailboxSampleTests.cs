using AdMng.Core.ActiveDirectory;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Testing;

namespace AdMng.Core.Tests.Testing;

/// <summary>The mailbox schema extension and the mailbox owner sample of the fake directory (U5, design note section 10).</summary>
public class MailboxSampleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<DirectoryObject>> SearchAsync(FakeDirectorySession session, string baseDn, string filter, params string[] attributes)
    {
        var result = new List<DirectoryObject>();
        await foreach (var item in session.SearchAsync(new DirectorySearch { BaseDn = baseDn, Filter = filter, Attributes = attributes }, Ct))
        {
            result.Add(item);
        }

        return result;
    }

    [Fact]
    public void Schema_HasTheMailboxAttributes_WithTheirSyntaxes_AllowedOnUsers()
    {
        var schema = FakeSchema.Create();

        var type = schema.FindAttribute(MailboxTypes.RecipientTypeDetails);
        var delegates = schema.FindAttribute(MailboxTypes.DelegateListLink);
        var sendOnBehalf = schema.FindAttribute(MailboxTypes.PublicDelegates);

        Assert.Equal((AttributeSyntax.LargeInteger, true), (type!.Syntax, type.IsSingleValued));
        Assert.Equal((AttributeSyntax.DistinguishedName, false), (delegates!.Syntax, delegates.IsSingleValued));
        Assert.Equal((AttributeSyntax.DistinguishedName, false), (sendOnBehalf!.Syntax, sendOnBehalf.IsSingleValued));
        var userAttributes = schema.GetAllowedAttributes(["top", "person", "organizationalPerson", ObjectClasses.User]);
        Assert.Superset(new HashSet<string>(FakeSchema.MailboxAttributes, StringComparer.OrdinalIgnoreCase), new HashSet<string>(userAttributes, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(MailboxTypes.RecipientTypeDetails, schema.GetAllowedAttributes(["top", ObjectClasses.Group]), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchemaWithoutMailboxes_LacksOnlyTheMailboxAttributes()
    {
        var full = FakeSchema.Create();
        var without = FakeSchema.CreateWithoutMailboxes();

        Assert.All(FakeSchema.MailboxAttributes, a => Assert.Null(without.FindAttribute(a)));
        Assert.Equal(full.Attributes.Count - FakeSchema.MailboxAttributes.Count, without.Attributes.Count);
        Assert.NotNull(without.FindAttribute(Attr.HideFromAddressLists));
        Assert.DoesNotContain(MailboxTypes.RecipientTypeDetails,
            without.GetAllowedAttributes(["top", "person", "organizationalPerson", ObjectClasses.User]), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sample_CreatesDisabledMailboxes_WithTypesAddressesAndTheExchangeFlag()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        Assert.False(builder.Session.Info.HasExchangeSchema);

        var sample = builder.AddMailboxOwnerSample(company);

        Assert.True(builder.Session.Info.HasExchangeSchema);
        Assert.Equal($"OU=Skrzynki,{company.CompanyOu}", sample.Ou);
        var mailboxes = await SearchAsync(builder.Session, sample.Ou, ObjectKind.User.ToLdapFilter(),
            Attr.UserAccountControl, Attr.Mail, Attr.DisplayName, MailboxTypes.RecipientTypeDetails, Attr.SamAccountName);
        Assert.Equal(14, mailboxes.Count);
        Assert.Equal(sample.Mailboxes.Order(StringComparer.OrdinalIgnoreCase), mailboxes.Select(m => m.DistinguishedName).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(mailboxes, m => Assert.True(m.IsDisabled));
        Assert.All(mailboxes, m => Assert.EndsWith("@contoso.test", m.GetString(Attr.Mail), StringComparison.Ordinal));
        Assert.Equal(13, mailboxes.Count(m => MailboxTypes.Of(m) == MailboxType.Shared));
        Assert.Equal(sample.Sala101, Assert.Single(mailboxes, m => MailboxTypes.Of(m) == MailboxType.Room).DistinguishedName);
        Assert.Equal("faktury@contoso.test", builder.Session.GetValues(sample.Faktury, Attr.Mail).Single());
        Assert.Equal("Księgowość – faktury", builder.Session.GetValues(sample.Faktury, Attr.DisplayName).Single());
        Assert.Equal("Skrzynka współdzielona: Księgowość – faktury", builder.Session.GetValues(sample.Faktury, Attr.Description).Single());
        Assert.Equal("Sala konferencyjna: Sala 101", builder.Session.GetValues(sample.Sala101, Attr.Description).Single());
        Assert.Empty(builder.Session.GetValues(sample.PomocTechniczna, Attr.GivenName));
        Assert.Empty(builder.Session.GetValues(sample.PomocTechniczna, Attr.Sn));
        Assert.Equal(mailboxes.Count, mailboxes.Select(m => m.GetString(Attr.SamAccountName)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Sample_TheLargeIntegerTypeIsSearchable_AndTheDelegateLinkIsADn()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var salutations = builder.AddSalutationSamples(company);
        var sample = builder.AddMailboxOwnerSample(company);

        var rooms = await SearchAsync(builder.Session, builder.DomainDn, LdapFilter.Equal(MailboxTypes.RecipientTypeDetails, 16));
        var remoteOrShared = await SearchAsync(builder.Session, builder.DomainDn, MailboxTypes.Filter(MailboxSelection.Shared)!);
        var delegates = await SearchAsync(builder.Session, builder.DomainDn, LdapFilter.Equal(MailboxTypes.DelegateListLink, salutations.Priest));

        Assert.Equal(sample.Sala101, Assert.Single(rooms).DistinguishedName);
        Assert.Equal(13, remoteOrShared.Count);
        Assert.Equal(sample.Wydarzenia, Assert.Single(delegates).DistinguishedName);
    }

    [Fact]
    public async Task Sample_ThePersons_TwoJanNowak_AndAgnieszkaWithAProxyAddressOnly()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var sample = builder.AddMailboxOwnerSample(company);

        var persons = await SearchAsync(builder.Session, builder.DomainDn, LdapFilter.And(ObjectKind.User.ToLdapFilter(), LdapFilter.Equal(Attr.Sn, "Nowak")),
            Attr.Department, Attr.SamAccountName, Attr.Mail);
        var agnieszka = await builder.Session.GetAsync(sample.AgnieszkaZolc, [Attr.Mail, Attr.ProxyAddresses, Attr.UserAccountControl], Ct);

        // The two "Jan Nowak" accounts and Anna Nowak of the salutation sample (added on the way); the mailboxes are disabled users too.
        var enabledNowak = persons.Where(p => p.Has(Attr.Department)).ToList();
        Assert.Equal(3, enabledNowak.Count);
        Assert.Equal([sample.JanNowakIt, sample.JanNowakSales], sample.Persons.Take(2));
        Assert.Contains(enabledNowak, p => p.DistinguishedName == sample.JanNowakIt && p.GetString(Attr.Department) == "IT");
        Assert.Contains(enabledNowak, p => p.DistinguishedName == sample.JanNowakSales && p.GetString(Attr.Department) == "Sprzedaż");
        Assert.Equal(2, persons.Where(p => p.DistinguishedName.StartsWith("CN=Jan Nowak,", StringComparison.Ordinal)).Select(p => p.GetString(Attr.SamAccountName)).Distinct().Count());
        Assert.Null(agnieszka!.GetString(Attr.Mail));
        Assert.Contains("SMTP:agnieszka.zolc@contoso.test", agnieszka.GetStrings(Attr.ProxyAddresses));
        Assert.False(agnieszka.IsDisabled);
    }

    [Fact]
    public void Sample_AddsTheSalutationSamplesWhenMissing_AndReusesThem()
    {
        var alone = new FakeDirectoryBuilder();
        var aloneCompany = alone.AddSampleCompany();
        var aloneSample = alone.AddMailboxOwnerSample(aloneCompany);

        var withSalutations = new FakeDirectoryBuilder();
        var company = withSalutations.AddSampleCompany();
        var salutations = withSalutations.AddSalutationSamples(company);
        var sample = withSalutations.AddMailboxOwnerSample(company);

        Assert.True(alone.Session.Exists($"OU=Zwroty grzecznościowe,{aloneCompany.CompanyOu}"));
        Assert.Equal(salutations.Priest, sample.ExpectedFor(sample.Wydarzenia).Owner);
        Assert.Equal(salutations.Doctor, sample.ExpectedFor(sample.BiuroProjektu).Owner);
        Assert.Equal(aloneSample.ExpectedFor(aloneSample.Wydarzenia).Owner, sample.ExpectedFor(sample.Wydarzenia).Owner);
        Assert.Equal([salutations.Priest], withSalutations.Session.GetValues(sample.Wydarzenia, MailboxTypes.DelegateListLink));
        Assert.Equal(14, sample.Expected.Count);
        Assert.Equal(sample.Mailboxes, sample.Expected.Select(e => e.Mailbox));
        Assert.Throws<ArgumentNullException>(() => withSalutations.AddMailboxOwnerSample(null!));
    }

    [Fact]
    public async Task Fake_AcceptsWritesOfTheMailboxAttributesOnUsers()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        var session = builder.Session;

        await session.ModifyAsync(company.JanKowalski,
        [
            AttributeModification.Replace(MailboxTypes.RecipientTypeDetails, "1"),
            AttributeModification.Add(MailboxTypes.PublicDelegates, company.LukaszWisniewski),
            AttributeModification.Add(MailboxTypes.DelegateListLink, company.LukaszWisniewski),
        ], null, Ct);
        var jan = await session.GetAsync(company.JanKowalski, [.. FakeSchema.MailboxAttributes], Ct);

        Assert.Equal(MailboxType.User, MailboxTypes.Of(jan!));
        Assert.Equal([company.LukaszWisniewski], jan!.GetStrings(MailboxTypes.PublicDelegates));
        Assert.Equal([company.LukaszWisniewski], MailboxEntry.From(jan).DelegateDns);
        await Assert.ThrowsAsync<DirectoryException>(() => session.ModifyAsync(company.JanKowalski,
            [AttributeModification.Replace(MailboxTypes.RecipientTypeDetails, "współdzielona")], null, Ct));
    }

    [Fact]
    public void Expectation_ToStringHidesTheDns()
    {
        var expectation = new MailboxOwnerExpectation("CN=Wnioski,OU=Skrzynki,DC=contoso,DC=test", OwnerMatchStatus.Matched, "CN=Agnieszka Żółć,DC=contoso,DC=test", OwnerMatchLevel.Exact);

        Assert.Equal("MailboxOwnerExpectation Matched", expectation.ToString());
    }
}
