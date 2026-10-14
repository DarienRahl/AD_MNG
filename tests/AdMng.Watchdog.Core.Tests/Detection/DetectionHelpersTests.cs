using AdMng.Core.ActiveDirectory;
using AdMng.Testing;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Core.Tests.Detection;

/// <summary>Watched/noise attribute sets, flag names, gPLink parsing and wellKnownObjects.</summary>
public class DetectionHelpersTests
{
    [Fact]
    public void WatchedAttributes_AddConfiguredAttributes_NeverSecretsOrNoise()
    {
        var settings = new DirectorySettings
        {
            AdditionalWatchedAttributes = ["extensionAttribute1", "unicodePwd", "lastLogonTimestamp", "ms-Mcs-AdmPwd", "bad*name"],
            IgnoredAttributes = [Attr.Department],
        };

        var user = WatchedAttributes.For(WatchObjectKind.User, settings, FakeSchema.Create());
        var group = WatchedAttributes.For(WatchObjectKind.Group, settings);

        Assert.Contains("extensionAttribute1", user);
        Assert.DoesNotContain(Attr.UnicodePwd, user);
        Assert.DoesNotContain("ms-Mcs-AdmPwd", user);
        Assert.DoesNotContain(Attr.LastLogonTimestamp, user);
        Assert.DoesNotContain("bad*name", user);
        Assert.DoesNotContain(Attr.Department, user);
        Assert.DoesNotContain("extensionAttribute1", group);
        Assert.DoesNotContain(Attr.Cn, user);
        Assert.True(WatchedAttributes.IsWatched("extensionAttribute1", settings));
        Assert.False(WatchedAttributes.IsWatched(Attr.Department, settings));
    }

    [Fact]
    public void Computers_DoNotWatchPersonalFields()
    {
        var computer = WatchedAttributes.For(WatchObjectKind.Computer, new DirectorySettings());

        Assert.DoesNotContain(Attr.Department, computer);
        Assert.Contains(Attr.DnsHostName, computer);
        Assert.Contains(WatchdogAttributes.CreatorSid, computer);
        Assert.Contains(WatchdogAttributes.KeyCredentialLink, computer);
    }

    [Fact]
    public void RequestSet_HasIdentityAndMemberButNoDescriptor()
    {
        var request = WatchedAttributes.RequestSet(new DirectorySettings());

        Assert.Contains(Attr.ObjectGuid, request);
        Assert.Contains(Attr.Member, request);
        Assert.Contains(Attr.GpLink, request);
        Assert.DoesNotContain(Attr.NtSecurityDescriptor, request);
        Assert.Equal(request.Count, request.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("lastLogonTimestamp", true)]
    [InlineData("msTPM-OwnerInformation", true)]
    [InlineData("memberOf", true)]
    [InlineData("member;range=0-1499", false)]
    [InlineData("department", false)]
    public void NoiseAttributes_AreRecognised(string attribute, bool noise) => Assert.Equal(noise, NoiseAttributes.IsNoise(attribute));

    [Fact]
    public void ComputerNoise_DependsOnTheSwitches()
    {
        Assert.True(NoiseAttributes.IsDroppedComputerChange(Attr.PwdLastSet, new DirectorySettings()));
        Assert.False(NoiseAttributes.IsDroppedComputerChange(Attr.PwdLastSet, new DirectorySettings { TrackComputerPasswordChanges = true }));
        Assert.True(NoiseAttributes.IsDroppedComputerChange(Attr.OperatingSystem, new DirectorySettings()));
        Assert.False(NoiseAttributes.IsDroppedComputerChange(Attr.OperatingSystem, new DirectorySettings { TrackComputerOsChanges = true }));
        Assert.False(NoiseAttributes.IsDroppedComputerChange(Attr.Description, new DirectorySettings()));
    }

    [Theory]
    [InlineData("HOST/waw-pc001.contoso.test", true)]
    [InlineData("HOST/WAW-PC001", true)]
    [InlineData("TERMSRV/waw-pc001.contoso.test:3389", true)]
    [InlineData("MSSQLSvc/sql01.contoso.test:1433", false)]
    [InlineData("cifs/dc01.contoso.test", false)]
    public void OwnHostSpn_IsComputerNoiseOnly(string spn, bool own) =>
        Assert.Equal(own, NoiseAttributes.IsOwnHostSpn(spn, "waw-pc001.contoso.test", "WAW-PC001$"));

    [Fact]
    public void FlagNames_DecodeAndDiff()
    {
        Assert.Equal(["ACCOUNTDISABLE", "NORMAL_ACCOUNT"], FlagNames.Decode(Attr.UserAccountControl, 0x202));
        Assert.Equal(["GLOBAL_GROUP", "SECURITY_ENABLED"], FlagNames.Decode(Attr.GroupType, -2147483646));
        Assert.Equal(["0x4000"], FlagNames.Decode(Attr.UserAccountControl, 0x4000));
        var (added, removed) = FlagNames.Diff(Attr.UserAccountControl, 0x202, 0x10200);
        Assert.Equal(["DONT_EXPIRE_PASSWORD"], added);
        Assert.Equal(["ACCOUNTDISABLE"], removed);
        Assert.False(FlagNames.IsFlagAttribute(Attr.Department));
    }

    [Fact]
    public void GpLinks_ParseLinksAndOptions()
    {
        var links = GpLinks.Parse("[LDAP://cn={31B2F340-016D-11D2-945F-00C04FB984F9},cn=policies,cn=system,DC=contoso,DC=test;0]"
            + "[LDAP://CN={6AC1786C-016F-11D2-945F-00C04fB984F9},CN=Policies,CN=System,DC=contoso,DC=test;2][garbage]");

        Assert.Equal(2, links.Count);
        Assert.False(links[0].Enforced);
        Assert.True(links[1].Enforced);
        Assert.Empty(GpLinks.Parse(null));
    }

    [Fact]
    public void WellKnownContainers_FollowRedirection_AndAcceptExtendedDns()
    {
        var domain = "DC=contoso,DC=test";
        var redirected = WellKnownContainers.Parse(
        [
            $"B:32:{WellKnownContainers.UsersGuid}:CN=Users,{domain}",
            $"B:32:{WellKnownContainers.ComputersGuid}:<GUID=a0d10000-0000-4000-8000-000000000001>;OU=Nowe komputery,OU=Firma,{domain}",
        ], domain);
        var standard = WellKnownContainers.Parse([], domain);

        Assert.True(redirected.ComputersRedirected);
        Assert.False(redirected.UsersRedirected);
        Assert.True(redirected.IsDefaultContainer($"OU=Nowe komputery,OU=Firma,{domain}"));
        Assert.True(redirected.IsUnexpectedContainer($"CN=Computers,{domain}"));
        Assert.False(redirected.IsUnexpectedContainer($"CN=Users,{domain}"));
        Assert.False(standard.ComputersRedirected);
        Assert.True(standard.IsDefaultContainer($"CN=Computers,{domain}"));
        Assert.False(standard.IsUnexpectedContainer($"CN=Computers,{domain}"));
    }
}
