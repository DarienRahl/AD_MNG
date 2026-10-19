using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Testing;

namespace AdMng.Core.Tests.Directory;

public class WellKnownObjectsTests
{
    private const string Domain = "DC=contoso,DC=test";

    /// <summary>The values of the Samba lab domain head (wellKnownObjects, then otherWellKnownObjects).</summary>
    private static readonly string[] LabValues =
    [
        $"B:32:6227F0AF1FC2410D8E3BB10615BB5B0F:CN=NTDS Quotas,{Domain}",
        $"B:32:F4BE92A4C777485E878E9421D53087DB:CN=Microsoft,CN=Program Data,{Domain}",
        $"B:32:09460C08AE1E4A4EA0F64AEE7DAA1E5A:CN=Program Data,{Domain}",
        $"B:32:22B70C67D56E4EFB91E9300FCA3DC1AA:CN=ForeignSecurityPrincipals,{Domain}",
        $"B:32:18E2EA80684F11D2B9AA00C04F79F805:CN=Deleted Objects,{Domain}",
        $"B:32:2FBAC1870ADE11D297C400C04FD8D5CD:CN=Infrastructure,{Domain}",
        $"B:32:AB8153B7768811D1ADED00C04FD8D5CD:CN=LostAndFound,{Domain}",
        $"B:32:AB1D30F3768811D1ADED00C04FD8D5CD:CN=System,{Domain}",
        $"B:32:A361B2FFFFD211D1AA4B00C04FD7D83A:OU=Domain Controllers,{Domain}",
        $"B:32:AA312825768811D1ADED00C04FD8D5CD:CN=Computers,{Domain}",
        $"B:32:A9D1CA15768811D1ADED00C04FD8D5CD:CN=Users,{Domain}",
    ];

    private static readonly string[] LabOtherValues =
    [
        $"B:32:1EB93889E40C45DF9F0C64D23BBB6237:CN=Managed Service Accounts,{Domain}",
        $"B:32:683A24E2E8164BD3AF86AC3C2CF3F981:CN=Keys,{Domain}",
    ];

    [Fact]
    public void LabValues_MapEveryStandardContainer()
    {
        var map = WellKnownObjectMap.Parse(LabValues, LabOtherValues);

        Assert.Equal(13, map.Count);
        Assert.Equal(Enum.GetValues<WellKnownContainer>().Length, map.Count);
        Assert.Equal($"CN=Users,{Domain}", map.Find(WellKnownContainer.Users));
        Assert.Equal($"CN=Computers,{Domain}", map.Find(WellKnownContainer.Computers));
        Assert.Equal($"OU=Domain Controllers,{Domain}", map.Find(WellKnownContainer.DomainControllers));
        Assert.Equal($"CN=System,{Domain}", map.Find(WellKnownContainer.System));
        Assert.Equal($"CN=LostAndFound,{Domain}", map.Find(WellKnownContainer.LostAndFound));
        Assert.Equal($"CN=Deleted Objects,{Domain}", map.Find(WellKnownContainer.DeletedObjects));
        Assert.Equal($"CN=Infrastructure,{Domain}", map.Find(WellKnownContainer.Infrastructure));
        Assert.Equal($"CN=ForeignSecurityPrincipals,{Domain}", map.Find(WellKnownContainer.ForeignSecurityPrincipals));
        Assert.Equal($"CN=Program Data,{Domain}", map.Find(WellKnownContainer.ProgramData));
        Assert.Equal($"CN=Microsoft,CN=Program Data,{Domain}", map.Find(WellKnownContainer.Microsoft));
        Assert.Equal($"CN=NTDS Quotas,{Domain}", map.Find(WellKnownContainer.NtdsQuotas));
        Assert.Equal($"CN=Managed Service Accounts,{Domain}", map.Find(WellKnownContainer.ManagedServiceAccounts));
        Assert.Equal($"CN=Keys,{Domain}", map.Find(WellKnownContainer.Keys));
        Assert.False(map.IsEmpty);
    }

    [Fact]
    public void Identify_ComparesParsedDns_NotStrings()
    {
        var map = WellKnownObjectMap.Parse(LabValues, LabOtherValues);

        Assert.Equal(WellKnownContainer.Users, map.Identify("cn=users,dc=CONTOSO,dc=test"));
        Assert.Equal(WellKnownContainer.DomainControllers, map.Identify("OU=Domain Controllers, DC=contoso, DC=test"));
        Assert.Equal(WellKnownContainer.Microsoft, map.Identify($"CN=Microsoft,CN=Program Data,{Domain}"));
        Assert.Null(map.Identify($"OU=Firma,{Domain}"));
        Assert.Null(map.Identify(Domain));
        Assert.Null(map.Identify("not a dn"));
        Assert.Null(map.Identify(null));
    }

    [Fact]
    public void RedirectedContainers_AreFollowed_AndTheOldNameIsNotGuessed()
    {
        // redirusr / redircmp: the GUID points to an OU, CN=Users is an ordinary container now.
        var map = WellKnownObjectMap.Parse(
        [
            $"B:32:{WellKnownGuids.Users}:OU=Użytkownicy,OU=Firma,{Domain}",
            $"B:32:{WellKnownGuids.Computers}:<GUID=a0d10000-0000-4000-8000-000000000001>;OU=Nowe komputery,OU=Firma,{Domain}",
        ]);

        Assert.Equal($"OU=Użytkownicy,OU=Firma,{Domain}", map.Find(WellKnownContainer.Users));
        Assert.Equal($"OU=Nowe komputery,OU=Firma,{Domain}", map.Find(WellKnownContainer.Computers));
        Assert.Equal(WellKnownContainer.Users, map.Identify($"OU=Użytkownicy,OU=Firma,{Domain}"));
        Assert.Null(map.Identify($"CN=Users,{Domain}"));
        Assert.Null(map.Identify($"CN=Computers,{Domain}"));
        Assert.Null(map.Find(WellKnownContainer.System));
    }

    [Theory]
    [InlineData("B:32:a9d1ca15768811d1aded00c04fd8d5cd:CN=Users,DC=contoso,DC=test", "A9D1CA15768811D1ADED00C04FD8D5CD", "CN=Users,DC=contoso,DC=test")]
    [InlineData("B:32:A9D1CA15768811D1ADED00C04FD8D5CD:<GUID=a0d10000-0000-4000-8000-000000000001>;CN=Users,DC=contoso,DC=test", "A9D1CA15768811D1ADED00C04FD8D5CD", "CN=Users,DC=contoso,DC=test")]
    [InlineData("B:4:00FF:CN=x,DC=contoso,DC=test", "00FF", "CN=x,DC=contoso,DC=test")]
    [InlineData("B:32:AB1D30F3768811D1ADED00C04FD8D5CD:CN=a\\,b,DC=contoso,DC=test", "AB1D30F3768811D1ADED00C04FD8D5CD", "CN=a\\,b,DC=contoso,DC=test")]
    public void TryParseValue_SplitsTheBinaryAndTheDn(string value, string expectedHex, string expectedDn)
    {
        Assert.True(WellKnownObjects.TryParseValue(value, out var hex, out var dn));
        Assert.Equal(expectedHex, hex);
        Assert.Equal(expectedDn, dn);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CN=Users,DC=contoso,DC=test")]
    [InlineData("S:5:hello:CN=Users,DC=contoso,DC=test")]
    [InlineData("B:31:A9D1CA15768811D1ADED00C04FD8D5CD:CN=Users,DC=contoso,DC=test")]
    [InlineData("B:33:A9D1CA15768811D1ADED00C04FD8D5CD:CN=Users,DC=contoso,DC=test")]
    [InlineData("B:32:A9D1CA15768811D1ADED00C04FD8D5CZ:CN=Users,DC=contoso,DC=test")]
    [InlineData("B:3:ABC:CN=Users,DC=contoso,DC=test")]
    [InlineData("B:-32:A9D1CA15768811D1ADED00C04FD8D5CD:CN=Users,DC=contoso,DC=test")]
    [InlineData("B:x:A9D1CA15768811D1ADED00C04FD8D5CD:CN=Users,DC=contoso,DC=test")]
    [InlineData("B:32:A9D1CA15768811D1ADED00C04FD8D5CD:")]
    [InlineData("B:32:A9D1CA15768811D1ADED00C04FD8D5CD:not a dn")]
    [InlineData("B:32:A9D1CA15768811D1ADED00C04FD8D5CD")]
    public void TryParseValue_RejectsMalformedValues(string value)
    {
        Assert.False(WellKnownObjects.TryParseValue(value, out _, out _));
    }

    [Fact]
    public void Parse_IgnoresMalformedAndUnknownValues_AndKeepsTheFirstOfADuplicate()
    {
        var map = WellKnownObjectMap.Parse(
        [
            "garbage",
            $"B:32:00000000000000000000000000000001:CN=Unknown,{Domain}",
            $"B:32:{WellKnownGuids.Users}:CN=Users,{Domain}",
            $"B:32:{WellKnownGuids.Users}:OU=Second,{Domain}",
        ]);

        Assert.Equal(1, map.Count);
        Assert.Equal($"CN=Users,{Domain}", map.Find(WellKnownContainer.Users));
    }

    [Fact]
    public void Empty_KnowsNothing()
    {
        var map = WellKnownObjectMap.Parse(null, null);

        Assert.True(map.IsEmpty);
        Assert.Same(WellKnownObjectMap.Empty, WellKnownObjectMap.Empty);
        Assert.True(WellKnownObjectMap.Empty.IsEmpty);
        Assert.Null(map.Find(WellKnownContainer.Users));
        Assert.Null(map.Identify($"CN=Users,{Domain}"));
    }

    [Fact]
    public void FromDomainObject_ReadsBothAttributes()
    {
        var domain = new DirectoryObject(Domain,
        [
            new AttributeValues(WellKnownObjects.WellKnownObjectsAttribute, LabValues),
            new AttributeValues(WellKnownObjects.OtherWellKnownObjectsAttribute, LabOtherValues),
        ]);

        var map = WellKnownObjectMap.FromDomainObject(domain);

        Assert.Equal(13, map.Count);
        Assert.Equal([WellKnownObjects.WellKnownObjectsAttribute, WellKnownObjects.OtherWellKnownObjectsAttribute], WellKnownObjects.Attributes);
    }

    [Fact]
    public async Task FakeBuilder_SeedsTheDomainHead_AndRedirects()
    {
        var builder = new FakeDirectoryBuilder();
        var ou = builder.AddOu("Użytkownicy");
        var ct = TestContext.Current.CancellationToken;

        var seeded = WellKnownObjectMap.FromDomainObject((await builder.Session.GetAsync(builder.DomainDn, WellKnownObjects.Attributes, ct))!);
        builder.SetWellKnownContainer(WellKnownContainer.Users, ou);
        builder.SetWellKnownContainer(WellKnownContainer.ManagedServiceAccounts, builder.AddContainer("Managed Service Accounts"));
        builder.SetWellKnownContainer(WellKnownContainer.DomainControllers, null);
        var redirected = WellKnownObjectMap.FromDomainObject((await builder.Session.GetAsync(builder.DomainDn, WellKnownObjects.Attributes, ct))!);

        Assert.Equal(builder.UsersDn, seeded.Find(WellKnownContainer.Users));
        Assert.Equal(builder.ComputersDn, seeded.Find(WellKnownContainer.Computers));
        Assert.Equal(builder.DomainControllersDn, seeded.Find(WellKnownContainer.DomainControllers));
        Assert.Equal(ou, redirected.Find(WellKnownContainer.Users));
        Assert.Equal(builder.ComputersDn, redirected.Find(WellKnownContainer.Computers));
        Assert.Equal(builder.ManagedServiceAccountsDn, redirected.Find(WellKnownContainer.ManagedServiceAccounts));
        Assert.Null(redirected.Find(WellKnownContainer.DomainControllers));
    }

    [Fact]
    public void Guids_MatchTheContainers()
    {
        foreach (var container in Enum.GetValues<WellKnownContainer>())
        {
            var guid = WellKnownGuids.Of(container);
            Assert.Equal(32, guid.Length);
            Assert.True(WellKnownGuids.TryGetContainer(guid.ToLowerInvariant(), out var back));
            Assert.Equal(container, back);
        }

        Assert.False(WellKnownGuids.TryGetContainer("00000000000000000000000000000001", out _));
        Assert.Equal("A9D1CA15768811D1ADED00C04FD8D5CD", WellKnownGuids.Users);
        Assert.Equal("AA312825768811D1ADED00C04FD8D5CD", WellKnownGuids.Computers);
    }
}
