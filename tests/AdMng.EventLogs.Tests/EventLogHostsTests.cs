using AdMng.Core.EventLogs;

namespace AdMng.EventLogs.Tests;

public class EventLogHostsTests
{
    [Theory]
    [InlineData("dc01.contoso.test", "dc01.contoso.test")]
    [InlineData("DC01.Contoso.Test.", "dc01.contoso.test")]
    [InlineData("krk-pc101.oddzial.contoso.test", "krk-pc101.oddzial.contoso.test")]
    [InlineData("wro_srv01.contoso.test", "wro_srv01.contoso.test")]
    public void Require_AnFqdn_IsNormalized(string host, string expected)
    {
        Assert.True(EventLogHosts.IsValid(host));
        Assert.Equal(expected, EventLogHosts.Require(host));
    }

    [Theory]
    [InlineData("dc01")]
    [InlineData("10.0.0.5")]
    [InlineData("10.0.0.5.")]
    [InlineData("::1")]
    [InlineData("[fe80::1]")]
    [InlineData("0x7f000001")]
    [InlineData("2130706433")]
    [InlineData("10.1")]
    [InlineData("dc01.contoso.test/Security")]
    [InlineData("dc 01.contoso.test")]
    [InlineData("-dc01.contoso.test")]
    [InlineData("dc01..contoso.test")]
    [InlineData("żółw.contoso.test")]
    [InlineData(@"\\dc01.contoso.test")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Require_RefusesEverythingButAnFqdn(string? host)
    {
        Assert.False(EventLogHosts.IsValid(host));
        var error = Assert.Throws<EventLogAccessException>(() => EventLogHosts.Require(host));
        Assert.Equal(EventLogAccessError.NotSupported, error.Error);
        Assert.Equal(EventLogHosts.InvalidHostNameError, error.NativeErrorCode);
    }
}
