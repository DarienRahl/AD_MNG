using AdMng.Core.Correspondence.Import;

namespace AdMng.Core.Tests.Correspondence.Import;

public class EmailAddressTextTests
{
    [Theory]
    [InlineData("jan.kowalski@urzad.gov.pl")]
    [InlineData("JAN.KOWALSKI@URZAD.GOV.PL")]
    [InlineData("biuro+zamowienia@firma-łódź.pl", false)]   // the pattern is ASCII (as MailerWPiA)
    [InlineData("a_b%c-d@sub.domena.com.pl")]
    [InlineData("x@a.museum")]
    [InlineData("  sekretariat@gmina.pl  ")]
    public void IsValid_AcceptsUsualAddresses(string address, bool expected = true) =>
        Assert.Equal(expected, EmailAddressText.IsValid(address));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("jan")]
    [InlineData("jan@")]
    [InlineData("@urzad.pl")]
    [InlineData("jan@urzad")]
    [InlineData("jan@urzad.p")]
    [InlineData("jan@urzad.p1")]
    [InlineData("jan kowalski@urzad.pl")]
    [InlineData("jan@@urzad.pl")]
    [InlineData("jan@urzad..pl")]
    [InlineData("jan..kowalski@urzad.pl")]
    [InlineData(".jan@urzad.pl")]
    [InlineData("jan.@urzad.pl")]
    [InlineData("jan@-urzad.pl")]
    [InlineData("jan@urzad-.pl")]
    [InlineData("jan@.urzad.pl")]
    public void IsValid_RefusesMalformedAddresses(string? address) => Assert.False(EmailAddressText.IsValid(address));

    [Fact]
    public void IsValid_TopLevelDomainOf2To63Letters()
    {
        Assert.True(EmailAddressText.IsValid("a@b." + new string('x', 63)));
        Assert.False(EmailAddressText.IsValid("a@b." + new string('x', 64)));
        Assert.False(EmailAddressText.IsValid(new string('a', 65) + "@urzad.pl"));
    }

    [Theory]
    [InlineData("kontakt@example.com")]
    [InlineData("kontakt@example.org")]
    [InlineData("kontakt@example.pl")]
    [InlineData("kontakt@poczta.example.com")]
    [InlineData("abc123@sentry.io")]
    [InlineData("3f2a9c@o450123.ingest.sentry.io")]
    [InlineData("x@schemas.microsoft.com")]
    [InlineData("x@w3.org")]
    [InlineData("x@www.w3.org")]
    public void IsValid_RefusesJunk(string address) => Assert.False(EmailAddressText.IsValid(address));

    [Theory]
    [InlineData("  MAILTO:Jan.Kowalski@Urzad.PL?subject=Pismo ", "jan.kowalski@urzad.pl")]
    [InlineData("<jan@urzad.pl>,", "jan@urzad.pl")]
    [InlineData("\"jan@urzad.pl\";", "jan@urzad.pl")]
    [InlineData("'jan@urzad.pl'", "jan@urzad.pl")]
    [InlineData("(jan@urzad.pl).", "jan@urzad.pl")]
    [InlineData("[jan@urzad.pl]", "jan@urzad.pl")]
    [InlineData("\u00A0jan@urzad.pl\u00A0", "jan@urzad.pl")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Normalize_TrimsDecorationsAndLowerCases(string? raw, string expected) =>
        Assert.Equal(expected, EmailAddressText.Normalize(raw));

    [Fact]
    public void Extract_FindsEveryAddressInOrder_NormalizedAndDistinct()
    {
        var text = "Proszę pisać na SEKRETARIAT@gmina.pl lub sekretariat@gmina.pl, a w sprawach pilnych: "
            + "mailto:wojt@gmina.pl?subject=Pilne. Śmieci: kontakt@example.com, zly@adres, x@sentry.io; ostatni: Łucja.Żak@gmina.pl";
        Assert.Equal(["sekretariat@gmina.pl", "wojt@gmina.pl"], EmailAddressText.Extract(text));
    }

    [Fact]
    public void Extract_NothingFromEmptyText()
    {
        Assert.Empty(EmailAddressText.Extract(null));
        Assert.Empty(EmailAddressText.Extract("bez adresów"));
    }

    [Theory]
    [InlineData("  Kontakt:   Jan    Kowalski  ", "Jan Kowalski")]
    [InlineData("Urząd Gminy: Wydział Kultury", "Wydział Kultury")]
    [InlineData("• Urząd Gminy Łódź –", "Urząd Gminy Łódź")]
    [InlineData("„Zakład Usług Wodnych”", "Zakład Usług Wodnych")]
    [InlineData("\"Gmina Żółkiewka\"", "Gmina Żółkiewka")]
    [InlineData("- * Starostwo · ", "Starostwo")]
    [InlineData("jan@urzad.pl", "")]
    [InlineData("Jan jan@urzad.pl", "")]
    [InlineData(null, "")]
    [InlineData(" \t ", "")]
    [InlineData("Urząd:", "Urząd:")]
    public void CleanName_FollowsMailerWPiA(string? raw, string expected) => Assert.Equal(expected, EmailAddressText.CleanName(raw));

    [Fact]
    public void CleanName_CutsAt120Characters()
    {
        var name = EmailAddressText.CleanName(new string('ż', 150));
        Assert.Equal(EmailAddressText.MaxNameLength, name.Length);
    }

    [Theory]
    [InlineData("a@x.pl; b@y.pl", "a@x.pl")]
    [InlineData("brak; B@Y.PL", "b@y.pl")]
    [InlineData("kontakt@example.com, b@y.pl", "b@y.pl")]
    [InlineData("Jan Kowalski <JAN@URZAD.PL>", "jan@urzad.pl")]
    [InlineData("mailto:jan@urzad.pl", "jan@urzad.pl")]
    [InlineData("jan@urzad\nb@y.pl", "b@y.pl")]
    public void FirstValid_TakesTheFirstValidAddressOfACell(string cell, string expected) =>
        Assert.Equal(expected, EmailAddressText.FirstValid(cell));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("brak adresu")]
    [InlineData("jan@urzad")]
    public void FirstValid_NullWithoutAValidAddress(string? cell) => Assert.Null(EmailAddressText.FirstValid(cell));
}
