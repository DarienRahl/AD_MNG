using AdMng.Core.Computers.Remote;

namespace AdMng.Core.Tests.Computers;

public class HostNamesTests
{
    [Theory]
    [InlineData("krk-pc101.contoso.test")]
    [InlineData("KRK-PC101.contoso.test.")]
    [InlineData("srv_app01.contoso.test")]
    [InlineData("srv_lodz_01.contoso.test")]
    [InlineData("pc01")]
    public void HostNames_IncludingUnderscoresOfLegacyComputers_AreValid(string host) => Assert.True(HostNames.IsValid(host));

    [Theory]
    [InlineData("10.20.5.17")]
    [InlineData("fe80::1")]
    [InlineData("pc01.contoso.test /s")]
    [InlineData("pc01.contoso.test&calc")]
    [InlineData("pc01;calc.contoso.test")]
    [InlineData("_ldap._tcp.contoso.test")]
    [InlineData("srv_.contoso.test")]
    [InlineData("-pc01.contoso.test")]
    [InlineData("pc01..contoso.test")]
    [InlineData("123.456")]
    [InlineData("")]
    [InlineData(null)]
    public void AddressesAndAnythingButAHostName_AreRefused(string? host) => Assert.False(HostNames.IsValid(host));

    [Fact]
    public void ARemoteTarget_TakesALegacyNameWithAnUnderscore()
    {
        var target = new RemoteTarget("CN=SRV_APP01,OU=Serwery,DC=contoso,DC=test", "SRV_APP01", "SRV_APP01.contoso.test.", Core.Computers.ComputerType.Server);

        Assert.Equal("srv_app01.contoso.test", target.DnsHostName);
    }
}
