using System.Net;
using AdMng.Core.Computers;

namespace AdMng.Core.Tests.Computers;

public class PlacementAndSubnetTests
{
    private const string Domain = "DC=contoso,DC=test";
    private static readonly string Computers = $"CN=Computers,{Domain}";
    private static readonly string DomainControllers = $"OU=Domain Controllers,{Domain}";

    [Fact]
    public void BuiltInRules_NeedNoConfiguration()
    {
        var rules = new PlacementRules(Computers, DomainControllers);

        Assert.Equal([PlacementFinding.NotMovedAfterJoin], rules.Evaluate($"CN=PC1,{Computers}", ComputerType.Workstation));
        Assert.Equal([PlacementFinding.DomainControllerOutsideOu], rules.Evaluate($"CN=DC2,OU=Serwery,{Domain}", ComputerType.DomainController));
        Assert.Equal([PlacementFinding.DomainControllerOutsideOu], rules.Evaluate($"CN=RODC1,OU=Oddziały,{Domain}", ComputerType.ReadOnlyDomainController));
        Assert.Equal([PlacementFinding.MemberInDomainControllersOu], rules.Evaluate($"CN=APP1,{DomainControllers}", ComputerType.Server));
        Assert.Empty(rules.Evaluate($"CN=DC1,{DomainControllers}", ComputerType.DomainController));
        Assert.Empty(rules.Evaluate($"CN=PREST1,{DomainControllers}", ComputerType.Unknown));
        Assert.False(rules.HasConfiguredRules);
    }

    [Fact]
    public void ConfiguredRules_AllowSubtrees()
    {
        var stations = PlacementRules.Resolve("OU=Stacje robocze,OU=Komputery,{domain}", Domain)!;
        var servers = PlacementRules.Resolve("OU=Serwery,{DOMAIN}", Domain)!;
        var rules = new PlacementRules(Computers, DomainControllers, new Dictionary<ComputerType, IReadOnlyList<string>>
        {
            [ComputerType.Workstation] = [stations],
            [ComputerType.Server] = [servers],
        });

        Assert.Empty(rules.Evaluate($"CN=PC1,OU=Łódź,{stations}", ComputerType.Workstation));
        Assert.Equal([PlacementFinding.UnexpectedContainer], rules.Evaluate($"CN=PC2,{servers}", ComputerType.Workstation));
        Assert.Equal([PlacementFinding.NotMovedAfterJoin, PlacementFinding.UnexpectedContainer], rules.Evaluate($"CN=SRV1,{Computers}", ComputerType.Server));
        Assert.Empty(rules.Evaluate($"CN=X,OU=Gdzieś,{Domain}", ComputerType.Unknown));
        Assert.True(rules.HasConfiguredRules);
        Assert.Null(PlacementRules.Resolve("not a dn", Domain));
    }

    [Fact]
    public void SubnetMap_MatchesTheLongestPrefix_IPv4AndIPv6()
    {
        var map = SubnetMap.Build(
        [
            ("10.0.0.0/8", $"CN=Centrala,CN=Sites,CN=Configuration,{Domain}"),
            ("10.1.0.0/16", $"CN=Kraków,CN=Sites,CN=Configuration,{Domain}"),
            ("10.1.2.0/24", $"CN=Kraków-Serwerownia,CN=Sites,CN=Configuration,{Domain}"),
            ("2001:db8::/32", $"CN=Centrala,CN=Sites,CN=Configuration,{Domain}"),
            ("2001:db8:1::/48", $"CN=Gdańsk,CN=Sites,CN=Configuration,{Domain}"),
            ("10.9.9.9/33", null),
            ("nie-podsieć", null),
            ("192.168.1.77/24", null),
        ]);

        Assert.Equal("Kraków-Serwerownia", map.Match(IPAddress.Parse("10.1.2.3")).SiteName);
        Assert.Equal("Kraków", map.Match(IPAddress.Parse("10.1.9.3")).SiteName);
        Assert.Equal("Centrala", map.Match(IPAddress.Parse("10.200.0.1")).SiteName);
        Assert.Equal("Gdańsk", map.Match(IPAddress.Parse("2001:db8:1::15")).SiteName);
        Assert.Equal("Centrala", map.Match(IPAddress.Parse("2001:db8:2::15")).SiteName);
        Assert.Equal("Kraków", map.Match(IPAddress.Parse("::ffff:10.1.9.3")).SiteName);
        Assert.Null(map.Match(IPAddress.Parse("172.16.0.1")).Subnet);
        Assert.Equal(["10.9.9.9/33", "nie-podsieć"], map.Invalid);

        // Host bits are cleared; a subnet without a site matches without one.
        var host = map.Match(IPAddress.Parse("192.168.1.200"));
        Assert.Equal("192.168.1.0", host.Subnet!.Network.ToString());
        Assert.Null(host.SiteName);
    }
}
