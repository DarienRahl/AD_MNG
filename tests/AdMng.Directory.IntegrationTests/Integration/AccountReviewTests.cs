using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// The account review (README U2) against the Samba lab, in this run's own OU: the server pre-filters are accepted, the
/// constructed attributes come back in a subtree search, and the client side checks find one account in every state
/// that can be produced in an OU (a fine-grained policy would need a PSO outside the OU and "never logged on" an old
/// whenCreated - both covered by the unit tests with the fake DC).
/// </summary>
public sealed class AccountReviewTests(LabFixture lab)
{
    private const int LockoutThreshold = 5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Review_FindsEveryState_WithTheConstructedValuesOfTheDc()
    {
        LabSettings.RequireLab();
        var now = DateTimeOffset.UtcNow;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Przegląd kont"));
        await using var secure = await LabFixture.ConnectAsync(LabSettings.Tls(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);

        var locked = await CreateEnabledUserAsync(secure, ou, "Łucja Zablokowana");
        var mustChange = await CreateEnabledUserAsync(secure, ou, "Michał Musizmienić");
        await Replace(mustChange.Dn, Attr.PwdLastSet, "0");
        var expired = await CreateEnabledUserAsync(secure, ou, "Anna Wygasła");
        await Replace(expired.Dn, Attr.AccountExpires, FileTime(now.AddDays(-1)));
        var expiring = await CreateEnabledUserAsync(secure, ou, "Bogdan Wygasający");
        var endOfDay = new DateTimeOffset(now.UtcDateTime.Date.AddDays(4), TimeSpan.Zero);
        await Replace(expiring.Dn, Attr.AccountExpires, FileTime(endOfDay));
        var neverExpires = await CreateEnabledUserAsync(secure, ou, "Celina Bezwygasania",
            UserAccountControl.NormalAccount | UserAccountControl.DontExpirePassword);
        var notRequired = await lab.CreateUserAsync(ou, "Dariusz Bezhasła");
        await Replace(notRequired, Attr.UserAccountControl, Uac(UserAccountControl.NormalAccount | UserAccountControl.PasswordNotRequired));
        var disabled = await lab.CreateUserAsync(ou, "Edyta Wyłączona");
        var regular = await CreateEnabledUserAsync(secure, ou, "Grzegorz Zwykły");
        await Replace(regular.Dn, Attr.Mail, "grzegorz.zwykly@contoso.test");
        await Replace(regular.Dn, Attr.Manager, locked.Dn);
        var subordinate = await CreateEnabledUserAsync(secure, ou, "Franciszek Podwładny");
        await Replace(subordinate.Dn, Attr.Manager, disabled);

        // Edyta is a member of A, A of B: still in groups, and a nested member of B.
        var groupA = await CreateGroupAsync(ou, "GG_Przeglad_A", disabled);
        var groupB = await CreateGroupAsync(ou, "GG_Przeglad_B", groupA);

        // Five bad passwords lock Łucja (lab: lockoutThreshold 5); Grzegorz logs on (lastLogon on this DC).
        for (var i = 0; i < LockoutThreshold; i++)
        {
            await Assert.ThrowsAsync<DirectoryException>(() =>
                LabFixture.ConnectAsync(LabSettings.Plain(locked.Upn), "Złe-Hasło-1!", cancellationToken: Ct));
        }

        await using (await LabFixture.ConnectAsync(LabSettings.Plain(regular.Upn), regular.Password, cancellationToken: Ct))
        {
        }

        var admin = lab.Admin;
        var domain = await admin.GetAsync(LabSettings.DomainDn, DomainPolicy.Attributes, Ct);
        var policy = DomainPolicy.FromDomainObject(domain!);
        var parameters = new AccountReviewParameters();
        var checks = Enum.GetValues<AccountCheck>().Where(c => c != AccountCheck.PrivilegedWithProblem).ToHashSet();
        var search = new DirectorySearch
        {
            BaseDn = ou,
            Scope = SearchScope.Subtree,
            Filter = AccountReviewQuery.Filter(checks, null, parameters, now, admin.Schema),
            Attributes = AccountReviewQuery.Attributes(admin.Schema),
        };
        var found = new List<AccountReviewFacts>();
        await foreach (var obj in admin.SearchAsync(search, Ct))
        {
            Assert.True(obj.Has(Attr.UserAccountControlComputed), $"{obj.Name}: msDS-User-Account-Control-Computed in a subtree search");
            found.Add(AccountReviewFacts.From(obj, now, TimeZoneInfo.Utc, policy.MaxPasswordAge,
                lockoutTime => policy.GetLockState(lockoutTime, now) != LockState.NotLocked, privileged: null));
        }

        // The managers' state (the review reads them in batches after the stream).
        for (var i = 0; i < found.Count; i++)
        {
            if (found[i].ManagerDn is { } manager)
            {
                found[i] = found[i] with { IsManagerDisabled = (await Read(admin, manager))?.IsDisabled };
            }
        }

        IReadOnlyList<string> Names(AccountCheck check) =>
            [.. found.Where(f => AccountReviewQuery.Matches(check, f, parameters)).Select(f => f.Object.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(["Łucja Zablokowana"], Names(AccountCheck.Locked));
        // A user created without a password has pwdLastSet = 0 as well (ADUC shows "must change" for both).
        Assert.Equal(["Dariusz Bezhasła", "Michał Musizmienić"], Names(AccountCheck.MustChangePassword));
        Assert.Equal(["Anna Wygasła"], Names(AccountCheck.AccountExpired));
        Assert.Equal(["Bogdan Wygasający"], Names(AccountCheck.AccountExpiring));
        Assert.Equal(["Celina Bezwygasania"], Names(AccountCheck.PasswordNeverExpires));
        Assert.Equal(["Dariusz Bezhasła"], Names(AccountCheck.PasswordNotRequired));
        Assert.Equal(["Edyta Wyłączona"], Names(AccountCheck.Disabled));
        Assert.Equal(["Edyta Wyłączona"], Names(AccountCheck.DisabledInGroups));
        Assert.Equal(["Franciszek Podwładny"], Names(AccountCheck.ManagerDisabled));
        Assert.DoesNotContain("Grzegorz Zwykły", Names(AccountCheck.NoMail));
        Assert.Contains("Franciszek Podwładny", Names(AccountCheck.NoMail));
        Assert.Empty(Names(AccountCheck.PasswordExpired));

        // Bogdan's account ends with the day before the stored midnight: 3 days left.
        var bogdan = AccountStatus.Evaluate(await Read(admin, expiring.Dn) ?? throw new InvalidOperationException(), now, TimeZoneInfo.Utc);
        Assert.Equal(3, bogdan.AccountDaysLeft);

        // Password expiry: the DC's constructed value (no estimate), pwdLastSet + the domain's maxPwdAge without a PSO.
        var grzegorz = AccountStatus.Evaluate(await Read(admin, regular.Dn) ?? throw new InvalidOperationException(), now, TimeZoneInfo.Utc, policy.MaxPasswordAge);
        Assert.False(grzegorz.IsPasswordExpiryEstimated);
        if (policy.MaxPasswordAge is { } maxAge && maxAge > TimeSpan.Zero)
        {
            Assert.Equal(PasswordState.Expires, grzegorz.Password);
            Assert.Equal(grzegorz.PasswordLastSet! + maxAge, grzegorz.PasswordExpiresAt);
        }
        else
        {
            Assert.Equal(PasswordState.NeverExpires, grzegorz.Password);
        }

        // Nested members of B (scope "group members") and the in-chain privileged filter.
        var groupBObject = await admin.GetAsync(groupB, [Attr.ObjectSid], Ct);
        Assert.Equal(["Edyta Wyłączona"], await NamesAsync(admin, LdapFilter.And(
            QuickFilters.Users, AccountReviewQuery.GroupMembersFilter(groupB, AdConvert.GetRid(groupBObject!.ObjectSid)))));
        Assert.Equal(["Edyta Wyłączona"], await NamesAsync(admin, LdapFilter.And(
            QuickFilters.Users, AccountReviewQuery.PrivilegedFilter([groupB]), LdapFilter.Equal(Attr.Name, "Edyta Wyłączona"))));
        var protectedGroups = new List<string>();
        await foreach (var group in admin.SearchAsync(new DirectorySearch
        {
            BaseDn = LabSettings.DomainDn,
            Filter = AccountReviewQuery.ProtectedGroupsFilter(policy.DomainSid),
        }, Ct))
        {
            protectedGroups.Add(group.Name);
        }

        Assert.Contains("Domain Admins", protectedGroups);
        Assert.Contains("Administrators", protectedGroups);

        // "Exact last logon": the reader of the review lists the DCs from the configuration partition (the lab has one, the
        // session's own) and reads lastLogon there. Samba does not record a simple LDAP bind as a logon, so the value
        // may be missing; when it is there, it is the logon above.
        var reader = new DomainControllerReader(new LdapDirectoryConnector(), new ErrorTranslator(new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"))));
        var reads = await reader.ReadAsync(admin, [regular.Dn, locked.Dn], [Attr.LastLogon], Ct);
        var read = Assert.Single(reads);
        Assert.True(read.Succeeded, read.Error);
        Assert.Equal(admin.Info.Server, read.Server);
        Assert.NotNull(read.Find(regular.Dn));
        Assert.NotNull(read.Find(locked.Dn));
        if (read.Find(regular.Dn)?.GetFileTime(Attr.LastLogon) is { } lastLogon)
        {
            Assert.InRange(lastLogon, now.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        }
    }

    private async Task<(string Dn, string Upn, string Password)> CreateEnabledUserAsync(
        IDirectorySession secure,
        string ou,
        string cn,
        UserAccountControl flags = UserAccountControl.NormalAccount)
    {
        var sam = TestNames.SamAccountName();
        var upn = $"{sam}@{LabSettings.DomainDns}";
        var dn = await lab.CreateUserAsync(ou, cn, new Dictionary<string, string>
        {
            [Attr.SamAccountName] = sam,
            [Attr.UserPrincipalName] = upn,
        });
        var password = TestNames.Password();
        await secure.ResetPasswordAsync(dn, password, Ct);
        await secure.ModifyAsync(dn, [AttributeModification.Replace(Attr.UserAccountControl, Uac(flags))], null, Ct);
        return (dn, upn, password);
    }

    private async Task<string> CreateGroupAsync(string ou, string name, string member)
    {
        var dn = DistinguishedName.Parse(ou).Child("CN", name).ToString();
        await lab.Admin.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", ObjectClasses.Group],
            [Attr.SamAccountName] = [TestNames.SamAccountName()],
            [Attr.Member] = [member],
        }, Ct);
        return dn;
    }

    private Task Replace(string dn, string attribute, string value) =>
        lab.Admin.ModifyAsync(dn, [AttributeModification.Replace(attribute, value)], null, Ct);

    private static Task<DirectoryObject?> Read(IDirectorySession session, string dn) =>
        session.GetAsync(dn, [.. AccountStatus.Attributes, Attr.Name], Ct);

    private static async Task<IReadOnlyList<string>> NamesAsync(IDirectorySession session, string filter)
    {
        var names = new List<string>();
        await foreach (var obj in session.SearchAsync(new DirectorySearch { BaseDn = LabSettings.DomainDn, Filter = filter, Attributes = [Attr.Name] }, Ct))
        {
            names.Add(obj.Name);
        }

        return [.. names.Order(StringComparer.Ordinal)];
    }

    private static string Uac(UserAccountControl flags) => ((int)flags).ToString(CultureInfo.InvariantCulture);

    private static string FileTime(DateTimeOffset value) => AdConvert.ToFileTime(value).ToString(CultureInfo.InvariantCulture);
}
