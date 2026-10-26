using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.PropertySheets;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.AccountReview;

/// <summary>Server pre-filters of the review and the Account tab summary.</summary>
public class AccountReviewQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PreFilters_ReuseTheChips_AndBoundTheDatesOnTheServer()
    {
        var parameters = new AccountReviewParameters();
        string Filter(AccountCheck check) => AccountReviewQuery.PreFilter(check, parameters, Now);
        var enabled = "(!(userAccountControl:1.2.840.113556.1.4.803:=2))";

        Assert.Contains("(lockoutTime>=1)", Filter(AccountCheck.Locked), StringComparison.Ordinal);
        Assert.Contains(enabled, Filter(AccountCheck.Locked), StringComparison.Ordinal);
        Assert.Equal("(userAccountControl:1.2.840.113556.1.4.803:=2)", Filter(AccountCheck.Disabled));
        Assert.Contains("(pwdLastSet=0)", Filter(AccountCheck.MustChangePassword), StringComparison.Ordinal);
        Assert.Contains($"(accountExpires<={AdConvert.ToFileTime(Now)})", Filter(AccountCheck.AccountExpired), StringComparison.Ordinal);
        Assert.Contains($"(accountExpires<={AdConvert.ToFileTime(Now.AddDays(31))})", Filter(AccountCheck.AccountExpiring), StringComparison.Ordinal);
        Assert.Contains($"(lastLogonTimestamp<={AdConvert.ToFileTime(Now.AddDays(-90))})", Filter(AccountCheck.Inactive), StringComparison.Ordinal);
        Assert.Contains("(!(lastLogonTimestamp=*))", Filter(AccountCheck.NeverLoggedOn), StringComparison.Ordinal);
        Assert.Contains($"(whenCreated<={AdConvert.ToGeneralizedTime(Now.AddDays(-90))})", Filter(AccountCheck.NeverLoggedOn), StringComparison.Ordinal);
        Assert.Contains($"(pwdLastSet<={AdConvert.ToFileTime(Now.AddDays(-365))})", Filter(AccountCheck.PasswordOld), StringComparison.Ordinal);
        Assert.Contains(":=32)", Filter(AccountCheck.PasswordNotRequired), StringComparison.Ordinal);
        Assert.Contains(":=128)", Filter(AccountCheck.ReversibleEncryption), StringComparison.Ordinal);
        Assert.Contains("(memberOf=*)", Filter(AccountCheck.DisabledInGroups), StringComparison.Ordinal);

        // Constructed attributes never reach a filter; the privileged check ORs the pre-filters of its problems.
        foreach (var check in Enum.GetValues<AccountCheck>())
        {
            Assert.Null(LdapFilter.Validate(Filter(check)));
            Assert.DoesNotContain("Computed", Filter(check), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(Filter(AccountCheck.PasswordNotRequired), Filter(AccountCheck.PrivilegedWithProblem), StringComparison.Ordinal);
        Assert.DoesNotContain("(manager=*)", Filter(AccountCheck.PrivilegedWithProblem), StringComparison.Ordinal);
    }

    [Fact]
    public void Gal_UsesTheExchangeAttributeOnlyWhenTheSchemaHasIt_AndGroupFiltersAreEscaped()
    {
        var withExchange = AccountReviewQuery.PreFilter(AccountCheck.DisabledInGal, new(), Now, FakeSchema.Create());
        Assert.Contains("(!(msExchHideFromAddressLists=TRUE))", withExchange, StringComparison.Ordinal);
        var withoutSchema = AccountReviewQuery.PreFilter(AccountCheck.DisabledInGal, new(), Now);
        Assert.DoesNotContain("msExchHideFromAddressLists", withoutSchema, StringComparison.Ordinal);
        Assert.Contains("(showInAddressBook=*)", withoutSchema, StringComparison.Ordinal);

        var group = @"CN=Księgowość (Kraków)\, *,OU=Grupy,DC=contoso,DC=test";
        var members = AccountReviewQuery.GroupMembersFilter(group, 1105);
        Assert.Equal(@"(|(memberOf:1.2.840.113556.1.4.1941:=CN=Księgowość \28Kraków\29\5c, \2a,OU=Grupy,DC=contoso,DC=test)(primaryGroupID=1105))", members);
        Assert.Null(LdapFilter.Validate(members));

        var protectedGroups = AccountReviewQuery.ProtectedGroupsFilter("S-1-5-21-1-2-3");
        Assert.Contains("(objectSid=S-1-5-32-544)", protectedGroups, StringComparison.Ordinal);
        Assert.Contains("(objectSid=S-1-5-21-1-2-3-512)", protectedGroups, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21-", AccountReviewQuery.ProtectedGroupsFilter(null), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => AccountReviewQuery.Filter([], null, new(), Now));
    }

    [Fact]
    public void Parameters_AreClampedAndNeverPersistedUnderAPasswordName()
    {
        var normalized = new AccountReviewParameters { ExpiryWarningDays = -5, InactiveDays = 0, RotationDays = 99_999 }.Normalized();
        Assert.Equal((0, 1, AccountReviewParameters.MaxDays), (normalized.ExpiryWarningDays, normalized.InactiveDays, normalized.RotationDays));
        Assert.DoesNotContain(typeof(AccountReviewParameters).GetProperties(), p => p.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AccountTab_SummarizesTheState_AndChecksEveryDomainController()
    {
        var scenario = await ReviewScenario.CreateAsync();
        var harness = scenario.Harness;
        var sheet = harness.Factory.CreatePropertySheet(scenario.Company.ZanetaSzymanska);
        await sheet.InitializeAsync();
        var page = Assert.IsType<FieldsPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == Presentation.Localization.L.Pages.Account));
        var status = Assert.Single(page.Fields.OfType<AccountStatusFieldViewModel>());

        Assert.True(status.IsInformational);
        Assert.False(status.CanSendMessages);
        Assert.True(status.HasProblem);
        var en = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal($"Locked out since {new DateTime(2026, 10, 13, 7, 30, 0).ToString("g", en)}", status.StateText);
        Assert.Equal($"Expires {new DateTime(2026, 11, 30, 8, 0, 0).ToString("g", en)} (in 48 days)", status.PasswordText);
        Assert.Equal("Never expires", status.AccountText);
        Assert.Equal("Never", status.LastLogonText);
        Assert.False(sheet.HasChanges);

        await status.CheckDomainControllersCommand.ExecuteAsync(null);
        Assert.Equal(
            $"Locked out on: dc01.contoso.test ({new DateTime(2026, 10, 13, 7, 30, 0).ToString("g", en)}){Environment.NewLine}Exact last logon: never on any DC",
            status.DomainControllersText);

        var pawel = harness.Factory.CreatePropertySheet(scenario.Company.PawelLewandowski);
        await pawel.InitializeAsync();
        var pawelStatus = Assert.Single(Assert.IsType<FieldsPageViewModel>(Assert.Single(pawel.Pages, p => p.Key == Presentation.Localization.L.Pages.Account))
            .Fields.OfType<AccountStatusFieldViewModel>());
        Assert.Equal("Enabled, not locked out", pawelStatus.StateText);
        Assert.Equal($"Valid until the end of {new DateTime(2026, 10, 16).ToString("d", en)} (in 3 days)", pawelStatus.AccountText);
        Assert.StartsWith($"{new DateTime(2026, 6, 15, 12, 0, 0).ToString("g", en)} (120 days ago)", pawelStatus.LastLogonText, StringComparison.Ordinal);

        var bartlomiej = harness.Factory.CreatePropertySheet(scenario.Company.BartlomiejKozlowski);
        await bartlomiej.InitializeAsync();
        Assert.Equal("Must be changed at the next logon", Assert.Single(Assert.IsType<FieldsPageViewModel>(
            Assert.Single(bartlomiej.Pages, p => p.Key == Presentation.Localization.L.Pages.Account)).Fields.OfType<AccountStatusFieldViewModel>()).PasswordText);
    }
}
