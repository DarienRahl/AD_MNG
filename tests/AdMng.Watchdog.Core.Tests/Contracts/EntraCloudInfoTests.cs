using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Tests.Contracts;

public class EntraCloudInfoTests
{
    private const string Tenant = "72f988bf-86f1-41af-91ab-2d7cd011db47";

    [Theory]
    [InlineData(EntraCloud.Public, "https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0", "https://outlook.office365.com/.default")]
    [InlineData(EntraCloud.UsGovernment, "https://login.microsoftonline.us/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0", "https://outlook.office365.us/.default")]
    [InlineData(EntraCloud.China, "https://login.partner.microsoftonline.cn/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0", "https://partner.outlook.cn/.default")]
    public void AuthorityAndSmtpScope_AreFixedPerCloud(EntraCloud cloud, string authority, string scope)
    {
        Assert.Equal(authority, EntraCloudInfo.Authority(cloud, Tenant));
        Assert.Equal(authority, EntraCloudInfo.Authority(cloud, "{" + Tenant.ToUpperInvariant() + "}"));
        Assert.Equal(scope, EntraCloudInfo.SmtpScope(cloud));
    }

    [Theory]
    [InlineData("contoso.onmicrosoft.com")]
    [InlineData("72f988bf-86f1-41af-91ab-2d7cd011db47/../evil")]
    [InlineData("")]
    public void Authority_AcceptsOnlyTenantGuids(string tenant)
    {
        Assert.Throws<ArgumentException>(() => EntraCloudInfo.Authority(EntraCloud.Public, tenant));
    }

    [Fact]
    public void TeamsScopes_ContainTheDefaultScopeOfTheChannelOptions()
    {
        Assert.Contains(new TeamsChannelOptions().Scope, EntraCloudInfo.TeamsScopes(EntraCloud.Public));
        Assert.Equal(2, EntraCloudInfo.TeamsScopes(EntraCloud.Public).Count);
        Assert.All(EntraCloudInfo.TeamsScopes(EntraCloud.UsGovernment), s => Assert.Matches(@"^https://(gov|high)\.service\.flow\.microsoft\.us//?\.default$", s));
        Assert.All(EntraCloudInfo.TeamsScopes(EntraCloud.China), s => Assert.StartsWith("https://service.powerautomate.cn/", s));
    }
}
