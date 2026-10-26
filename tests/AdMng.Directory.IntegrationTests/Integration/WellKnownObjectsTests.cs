using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>The domain head's wellKnownObjects / otherWellKnownObjects as the real DC returns them.</summary>
public sealed class WellKnownObjectsTests(LabFixture lab)
{
    [Fact]
    public async Task DomainHead_ListsEveryStandardContainer_AndTheyExist()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var domainDn = lab.Admin.Info.DefaultNamingContext;

        var head = await lab.Admin.GetAsync(domainDn, WellKnownObjects.Attributes, ct);
        var map = WellKnownObjectMap.FromDomainObject(head!);

        Assert.Equal(Enum.GetValues<WellKnownContainer>().Length, map.Count);
        Assert.Equal($"CN=Users,{domainDn}", map.Find(WellKnownContainer.Users), ignoreCase: true);
        Assert.Equal($"CN=Computers,{domainDn}", map.Find(WellKnownContainer.Computers), ignoreCase: true);
        Assert.Equal($"OU=Domain Controllers,{domainDn}", map.Find(WellKnownContainer.DomainControllers), ignoreCase: true);
        Assert.Equal($"CN=Managed Service Accounts,{domainDn}", map.Find(WellKnownContainer.ManagedServiceAccounts), ignoreCase: true);
        foreach (var (container, dn) in map.Containers)
        {
            if (container == WellKnownContainer.DeletedObjects)
            {
                // Visible only with the Show Deleted control.
                continue;
            }

            var target = await lab.Admin.GetAsync(dn, [Attr.ObjectClass], ct);
            Assert.True(target is not null, $"{container}: {dn} does not exist");
            Assert.Equal(container, map.Identify(target!.DistinguishedName));
        }
    }
}
