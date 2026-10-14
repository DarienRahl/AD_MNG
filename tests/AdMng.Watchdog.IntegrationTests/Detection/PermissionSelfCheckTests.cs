using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

namespace AdMng.Watchdog.IntegrationTests.Detection;

/// <summary>
/// The permission self-check against a hardened object (spec 10.12, 22.4): a user whose binary security descriptor
/// denies the watchdog's account the read of uSNChanged is invisible to change tracking – the self-check must report
/// <c>permission.uSNChanged</c> as an Error and append one HealthPermissionGap.
/// </summary>
public sealed class PermissionSelfCheckTests(ReadOnlyEngineFixture engine, LabFixture lab) : IClassFixture<ReadOnlyEngineFixture>
{
    private const string SystemSid = "S-1-5-18";
    private const string AuthenticatedUsersSid = "S-1-5-11";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EngineHost Host => engine.Host;

    [Fact]
    public async Task UserWithAHiddenUsnChanged_IsAPermissionGapError()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Utwardzone", Ct);
        var user = await lab.Writer.CreateUserAsync(ou, "Zbigniew", "Utwardzony", TestNames.Password(), cancellationToken: Ct);
        try
        {
            await HideUsnChangedFromTheWatchdogAsync(user.Dn);
            var mark = await Host.MarkAsync(Ct);

            await Host.RunScansAsync(Ct);

            var check = Assert.Single(Host.Health.Checks, c => c.Code == PermissionSelfCheck.UsnChanged);
            Assert.Equal(HealthState.Error, check.State);
            var gaps = (await Host.AllEventsAsync(mark, Ct)).Where(e => e.Kind == WatchEventKind.HealthPermissionGap).ToList();
            Assert.Contains(gaps, e => e.Changes.Any(c => c.NewValues.Contains(PermissionSelfCheck.UsnChanged)));
        }
        finally
        {
            // Other engines of the shared lab would see the gap too.
            await lab.Writer.DeleteAsync(user.Dn, subtree: false, CancellationToken.None);
        }
    }

    /// <summary>Protected DACL: SYSTEM and Domain Admins full control, Authenticated Users generic read, the watchdog's
    /// account denied Read Property on uSNChanged (the deny first, as in a canonical DACL).</summary>
    private async Task HideUsnChangedFromTheWatchdogAsync(string dn)
    {
        var schemaNc = lab.Admin.Info.SchemaNamingContext;
        var usnChanged = await lab.Admin.GetAsync(DistinguishedName.Parse(schemaNc).Child("CN", "USN-Changed").ToString(), ["schemaIDGUID"], Ct);
        var propertyGuid = new Guid(Assert.IsType<byte[]>(usnChanged?.GetBytes("schemaIDGUID")));
        var watchdog = await FindSidAsync(WatchdogLab.ReadOnlyUser);
        var domainAdmins = (await lab.Admin.GetAsync(WatchdogLab.DomainDn, [Attr.ObjectSid], Ct))?.ObjectSid + "-512";

        var descriptor = SecurityDescriptorBuilder.Build(domainAdmins, domainAdmins,
            SecurityDescriptorBuilder.DenyProperty(watchdog, SecurityDescriptorBuilder.ReadProperty, propertyGuid),
            SecurityDescriptorBuilder.Allow(SystemSid, SecurityDescriptorBuilder.FullControl),
            SecurityDescriptorBuilder.Allow(domainAdmins, SecurityDescriptorBuilder.FullControl),
            SecurityDescriptorBuilder.Allow(AuthenticatedUsersSid, SecurityDescriptorBuilder.GenericRead));
        await lab.Writer.ReplaceAsync(dn, Attr.NtSecurityDescriptor, descriptor, Ct);

        // The arrangement itself: the watchdog's account still finds the user, but not through uSNChanged.
        await using var asWatchdog = await LabFixture.ConnectAsync(WatchdogLab.Plain(WatchdogLab.ReadOnlyUser), WatchdogLab.ReadOnlyPassword, Ct);
        var seen = await asWatchdog.GetAsync(dn, [Attr.ObjectGuid, Attr.UsnChanged], Ct);
        Assert.NotNull(seen?.ObjectGuid);
        Assert.Null(seen.GetInt64(Attr.UsnChanged));
    }

    private async Task<string> FindSidAsync(string userPrincipalName)
    {
        await foreach (var entry in lab.Admin.SearchAsync(new DirectorySearch
        {
            BaseDn = WatchdogLab.DomainDn,
            Filter = LdapFilter.Equal(Attr.UserPrincipalName, userPrincipalName),
            Attributes = [Attr.ObjectSid],
            SizeLimit = 1,
        }, Ct))
        {
            return entry.ObjectSid ?? throw new InvalidOperationException("The account has no objectSid.");
        }

        throw new InvalidOperationException("The watchdog's account was not found.");
    }
}
