using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;

namespace AdMng.Watchdog.IntegrationTests.Infrastructure;

/// <summary>
/// Administrator changes of the scenarios (spec 22.4) – only below the run's OU. Plain LDAP for everything except
/// passwords, which the lab accepts only over LDAPS. DNs are built with <see cref="DistinguishedName"/>, never
/// concatenated.
/// </summary>
public sealed class LabWriter(IDirectorySession admin, IDirectorySession adminTls)
{
    /// <summary>groupType of a global security group.</summary>
    public const int GlobalSecurityGroup = unchecked((int)(GroupTypeFlags.Global | GroupTypeFlags.Security));

    public IDirectorySession Session => admin;

    public async Task<string> CreateOuAsync(string parentDn, string name, string? description, CancellationToken cancellationToken)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("OU", name).ToString();
        var attributes = new Dictionary<string, IReadOnlyList<object>> { [Attr.ObjectClass] = ["top", ObjectClasses.OrganizationalUnit] };
        if (description is not null)
        {
            attributes[Attr.Description] = [description];
        }

        await admin.AddAsync(dn, attributes, cancellationToken);
        return dn;
    }

    /// <summary>A user; <paramref name="password"/> set (LDAPS) and the account enabled when given, otherwise disabled.</summary>
    public async Task<LabUser> CreateUserAsync(string parentDn, string givenName, string surname, string? password = null,
        IReadOnlyDictionary<string, string>? extra = null, CancellationToken cancellationToken = default)
    {
        var name = $"{givenName} {surname}";
        var dn = DistinguishedName.Parse(parentDn).Child("CN", name).ToString();
        var sam = TestNames.SamAccountName();
        var upn = $"{sam}@{WatchdogLab.DomainDns}";
        var attributes = new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.User],
            [Attr.SamAccountName] = [sam],
            [Attr.UserPrincipalName] = [upn],
            [Attr.GivenName] = [givenName],
            [Attr.Sn] = [surname],
            [Attr.DisplayName] = [name],
            [Attr.UserAccountControl] = [Number(UserAccountControl.NormalAccount | UserAccountControl.AccountDisable)],
        };
        foreach (var (attribute, value) in extra ?? new Dictionary<string, string>())
        {
            attributes[attribute] = [value];
        }

        await admin.AddAsync(dn, attributes, cancellationToken);
        if (password is not null)
        {
            await adminTls.ResetPasswordAsync(dn, password, cancellationToken);
            await SetAccountControlAsync(dn, UserAccountControl.NormalAccount, cancellationToken);
        }

        return new LabUser(dn, sam, upn, password);
    }

    public async Task<string> CreateGroupAsync(string parentDn, string name, CancellationToken cancellationToken, params string[] members)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", name).ToString();
        var attributes = new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", ObjectClasses.Group],
            [Attr.SamAccountName] = [TestNames.SamAccountName()],
            [Attr.GroupType] = [GlobalSecurityGroup.ToString(CultureInfo.InvariantCulture)],
        };
        if (members.Length > 0)
        {
            attributes[Attr.Member] = members;
        }

        await admin.AddAsync(dn, attributes, cancellationToken);
        return dn;
    }

    /// <summary>A workstation account (sAMAccountName with '$', WORKSTATION_TRUST_ACCOUNT).</summary>
    public async Task<string> CreateComputerAsync(string parentDn, string name, CancellationToken cancellationToken)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", name).ToString();
        await admin.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.User, ObjectClasses.Computer],
            [Attr.SamAccountName] = [name.ToUpperInvariant() + "$"],
            [Attr.UserAccountControl] = [Number(UserAccountControl.WorkstationTrustAccount)],
            [Attr.DnsHostName] = [$"{name.ToLowerInvariant()}.{WatchdogLab.DomainDns}"],
        }, cancellationToken);
        return dn;
    }

    /// <summary>A child object below <paramref name="parentDn"/> (a service connection point – allowed below computers).</summary>
    public async Task<string> CreateServiceConnectionPointAsync(string parentDn, string name, CancellationToken cancellationToken)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", name).ToString();
        await admin.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", "connectionPoint", "serviceConnectionPoint"],
            ["keywords"] = ["AdMngWatchdogIntegrationTest"],
        }, cancellationToken);
        return dn;
    }

    public async Task<string> CreateContactAsync(string parentDn, string name, CancellationToken cancellationToken)
    {
        var dn = DistinguishedName.Parse(parentDn).Child("CN", name).ToString();
        await admin.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", "person", "organizationalPerson", ObjectClasses.Contact],
        }, cancellationToken);
        return dn;
    }

    /// <summary>Sets a password (LDAPS – the lab refuses password changes over plain LDAP).</summary>
    public Task ResetPasswordAsync(string dn, string password, CancellationToken cancellationToken) =>
        adminTls.ResetPasswordAsync(dn, password, cancellationToken);

    public Task ReplaceAsync(string dn, string attribute, string value, CancellationToken cancellationToken) =>
        admin.ModifyAsync(dn, [AttributeModification.Replace(attribute, value)], null, cancellationToken);

    public Task ReplaceAsync(string dn, string attribute, byte[] value, CancellationToken cancellationToken) =>
        admin.ModifyAsync(dn, [AttributeModification.Replace(attribute, value)], null, cancellationToken);

    public Task SetAccountControlAsync(string dn, UserAccountControl flags, CancellationToken cancellationToken) =>
        ReplaceAsync(dn, Attr.UserAccountControl, Number(flags), cancellationToken);

    public Task AddMembersAsync(string groupDn, IReadOnlyList<string> memberDns, CancellationToken cancellationToken) =>
        admin.ModifyAsync(groupDn, [AttributeModification.Add(Attr.Member, [.. memberDns])], null, cancellationToken);

    public Task RemoveMembersAsync(string groupDn, IReadOnlyList<string> memberDns, CancellationToken cancellationToken) =>
        admin.ModifyAsync(groupDn, [AttributeModification.Delete(Attr.Member, [.. memberDns])], null, cancellationToken);

    /// <summary>Moves and/or renames; returns the new DN.</summary>
    public Task<string> MoveAsync(string dn, string? newParentDn, string? newRdn, CancellationToken cancellationToken) =>
        admin.MoveAsync(dn, newParentDn, newRdn, cancellationToken);

    public Task DeleteAsync(string dn, bool subtree, CancellationToken cancellationToken) => admin.DeleteAsync(dn, subtree, cancellationToken);

    /// <summary>One simple bind with a wrong password (badPwdCount + 1; the threshold-th sets lockoutTime).</summary>
    public static async Task BadBindAsync(LabUser user, CancellationToken cancellationToken)
    {
        var error = await Assert.ThrowsAnyAsync<DirectoryException>(async () =>
        {
            await using var session = await LabFixture.ConnectAsync(WatchdogLab.Plain(user.UserPrincipalName), "Błędne-Hasło-" + Guid.NewGuid().ToString("N")[..6], cancellationToken);
        });
        Assert.True(error.Kind is DirectoryErrorKind.InvalidCredentials or DirectoryErrorKind.AccountRestriction,
            $"unexpected bind error {error.Kind}");
    }

    /// <summary>A successful simple bind (writes lastLogonTimestamp the first time).</summary>
    public static async Task BindAsync(LabUser user, CancellationToken cancellationToken)
    {
        await using var session = await LabFixture.ConnectAsync(WatchdogLab.Plain(user.UserPrincipalName), user.Password!, cancellationToken);
        Assert.NotNull(session.Info.BoundIdentity);
    }

    private static string Number(UserAccountControl flags) => ((int)flags).ToString(CultureInfo.InvariantCulture);
}
