using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>Effective permissions and access denied mapping - read-only on seeded data.</summary>
public sealed class PermissionTests(LabFixture lab)
{
    private static readonly string[] HelpdeskWritable = [Attr.TelephoneNumber, Attr.Mobile, Attr.PwdLastSet, Attr.LockoutTime];

    [Fact]
    public async Task Administrator_CanWriteEverythingRelevant()
    {
        LabSettings.RequireLab();
        var user = await lab.FindSeededUserAsync($"OU=IT,{LabSettings.EmployeesOu}");

        var permissions = await lab.Admin.GetEffectivePermissionsAsync(user.DistinguishedName, TestContext.Current.CancellationToken);

        Assert.True(permissions.IsKnown);
        Assert.True(permissions.CanWrite(Attr.Department));
        Assert.True(permissions.CanWrite(Attr.TelephoneNumber));
        Assert.True(permissions.CanWrite(Attr.Description));
        Assert.True(permissions.SecurityDescriptorRights.HasFlag(SecurityDescriptorRights.Dacl));

        var ou = await lab.Admin.GetEffectivePermissionsAsync(LabSettings.EmployeesOu, TestContext.Current.CancellationToken);
        Assert.True(ou.CanCreate(ObjectClasses.User));
        Assert.True(ou.CanCreate(ObjectClasses.OrganizationalUnit));
    }

    [Fact]
    public async Task Helpdesk_CanWriteDelegatedAttributesOnly()
    {
        LabSettings.RequireLab();
        var user = await lab.FindSeededUserAsync($"OU=Sprzedaż,{LabSettings.EmployeesOu}");
        await using var helpdesk = await LabFixture.ConnectAsync(
            LabSettings.Plain(LabSettings.HelpdeskUser),
            LabSettings.HelpdeskPassword,
            cancellationToken: TestContext.Current.CancellationToken);

        var permissions = await helpdesk.GetEffectivePermissionsAsync(user.DistinguishedName, TestContext.Current.CancellationToken);

        Assert.True(permissions.IsKnown);
        Assert.All(HelpdeskWritable, attribute => Assert.True(permissions.CanWrite(attribute), attribute));
        Assert.False(permissions.CanWrite(Attr.Department));
        Assert.False(permissions.CanWrite(Attr.DisplayName));
        Assert.False(permissions.CanCreate(ObjectClasses.User));
        Assert.False(permissions.SecurityDescriptorRights.HasFlag(SecurityDescriptorRights.Dacl));
    }

    [Fact]
    public async Task ReadOnlyUser_CanWriteNothing()
    {
        LabSettings.RequireLab();
        var user = await lab.FindSeededUserAsync($"OU=Kadry,{LabSettings.EmployeesOu}");
        await using var reader = await LabFixture.ConnectAsync(
            LabSettings.Plain(LabSettings.ReaderUser),
            LabSettings.ReaderPassword,
            cancellationToken: TestContext.Current.CancellationToken);

        var permissions = await reader.GetEffectivePermissionsAsync(user.DistinguishedName, TestContext.Current.CancellationToken);

        Assert.True(permissions.IsKnown);
        Assert.Empty(permissions.WritableAttributes);
        Assert.Empty(permissions.CreatableChildClasses);
        Assert.False(permissions.CanWrite(Attr.TelephoneNumber));
        Assert.Equal($"{LabSettings.NetBiosName}\\odczyt.test", reader.Info.BoundIdentity, ignoreCase: true);
    }

    [Fact]
    public async Task Helpdesk_ModifyingDepartment_IsAccessDenied()
    {
        LabSettings.RequireLab();
        var user = await lab.FindSeededUserAsync($"OU=Logistyka,{LabSettings.EmployeesOu}", Attr.Department);
        var department = user.GetString(Attr.Department)!;
        await using var helpdesk = await LabFixture.ConnectAsync(
            LabSettings.Plain(LabSettings.HelpdeskUser),
            LabSettings.HelpdeskPassword,
            cancellationToken: TestContext.Current.CancellationToken);

        // Writing the current value: even a (wrongly) permitted write would not change seeded data.
        var error = await Assert.ThrowsAsync<DirectoryException>(() => helpdesk.ModifyAsync(
            user.DistinguishedName,
            [AttributeModification.Replace(Attr.Department, department)],
            null,
            TestContext.Current.CancellationToken));

        Assert.Equal(DirectoryErrorKind.AccessDenied, error.Kind);
        Assert.Equal(50, error.LdapResultCode);
        Assert.Equal(user.DistinguishedName, error.DistinguishedName);
        Assert.NotNull(error.ServerMessage);
        Assert.DoesNotContain(user.DistinguishedName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EffectivePermissions_OfAMissingObject_IsNoSuchObject()
    {
        LabSettings.RequireLab();
        var error = await Assert.ThrowsAsync<DirectoryException>(() => lab.Admin.GetEffectivePermissionsAsync(
            $"CN=Nie istnieje,{LabSettings.TestOu}",
            TestContext.Current.CancellationToken));

        Assert.Equal(DirectoryErrorKind.NoSuchObject, error.Kind);
    }
}
