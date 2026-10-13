using AdMng.Core.ActiveDirectory;
using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

public sealed class PasswordTests(LabFixture lab)
{
    [Fact]
    public async Task ResetPassword_OverLdaps_LetsTheUserBind()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Hasla"));
        var sam = TestNames.SamAccountName();
        var dn = await lab.CreateUserAsync(ou, "Hasło Testowe", new Dictionary<string, string>
        {
            [Attr.SamAccountName] = sam,
            [Attr.UserPrincipalName] = $"{sam}@{LabSettings.DomainDns}",
        });
        var password = TestNames.Password();

        await using (var secure = await LabFixture.ConnectAsync(LabSettings.Tls(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: ct))
        {
            await secure.ResetPasswordAsync(dn, password, ct);
            await secure.ModifyAsync(
                dn,
                [AttributeModification.Replace(Attr.UserAccountControl, ((int)UserAccountControl.NormalAccount).ToString(CultureInfo.InvariantCulture))],
                null,
                ct);
        }

        var user = await lab.Admin.GetAsync(dn, [Attr.PwdLastSet, Attr.UserAccountControl], ct);
        Assert.NotNull(user!.GetFileTime(Attr.PwdLastSet));
        Assert.False(user.IsDisabled);

        await using var asUser = await LabFixture.ConnectAsync(LabSettings.Plain($"{sam}@{LabSettings.DomainDns}"), password, cancellationToken: ct);
        Assert.Equal($"{LabSettings.NetBiosName}\\{sam}", asUser.Info.BoundIdentity, ignoreCase: true);
        Assert.True(DistinguishedName.AreEqual(dn, asUser.Info.BoundUserDn));
    }

    [Fact]
    public async Task ResetPassword_TooSimple_IsPasswordPolicy()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Polityka"));
        var dn = await lab.CreateUserAsync(ou, "Słabe Hasło");
        await using var secure = await LabFixture.ConnectAsync(LabSettings.Tls(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: ct);

        var error = await Assert.ThrowsAsync<DirectoryException>(() => secure.ResetPasswordAsync(dn, "abc", ct));

        Assert.Equal(DirectoryErrorKind.PasswordPolicy, error.Kind);
        Assert.Equal(0x52D, error.Win32Error);
        Assert.DoesNotContain("abc", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetPassword_OverAnUnprotectedChannel_IsRefusedLocally()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Jawne"));
        var dn = await lab.CreateUserAsync(ou, "Bez Szyfrowania");
        var logs = new CapturingLoggerFactory();
        await using var plain = await LabFixture.ConnectAsync(LabSettings.Plain(LabSettings.AdminUser), LabSettings.AdminPassword, logs, cancellationToken: ct);
        Assert.False(((LdapDirectorySession)plain).IsChannelEncrypted);
        var password = TestNames.Password();

        var reset = await Assert.ThrowsAsync<DirectoryException>(() => plain.ResetPasswordAsync(dn, password, ct));
        Assert.Equal(DirectoryErrorKind.StrongAuthRequired, reset.Kind);
        Assert.Null(reset.LdapResultCode); // refused before anything was sent

        var modify = await Assert.ThrowsAsync<DirectoryException>(() => plain.ModifyAsync(
            dn,
            [AttributeModification.Replace(Attr.UnicodePwd, AdConvert.EncodeUnicodePwd(password))],
            null,
            ct));
        Assert.Equal(DirectoryErrorKind.StrongAuthRequired, modify.Kind);

        var user = await lab.Admin.GetAsync(dn, [Attr.PwdLastSet], ct);
        Assert.Null(user!.GetFileTime(Attr.PwdLastSet));
        Assert.All(logs.Records, r => Assert.DoesNotContain(password, r.Message, StringComparison.Ordinal));
    }
}
