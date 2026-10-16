using AdMng.Watchdog.Core.Rules.Validation;

namespace AdMng.Watchdog.Core.Tests.Rules;

public class EmailAddressRulesTests
{
    [Theory]
    [InlineData("łukasz.wiśniewski@contoso.test")]
    [InlineData("Nadzór AD <nadzor@contoso.test>")]
    [InlineData("\"Kowalski, Jan\" <jan.kowalski@contoso.test>")]
    [InlineData("  helpdesk.łódź@contoso.test  ")]
    public void Valid(string address) => Assert.True(EmailAddressRules.IsValid(address));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("jan.kowalski")]
    [InlineData("a@b.test, c@d.test")]
    [InlineData("Kowalski, Jan <jan@contoso.test>")]
    [InlineData("jan@contoso.test\nBcc: x@atakujacy.example")]
    [InlineData("jan\t@contoso.test")]
    public void Invalid(string? address) => Assert.False(EmailAddressRules.IsValid(address));
}
