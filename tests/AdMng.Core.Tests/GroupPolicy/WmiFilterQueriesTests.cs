using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Core.Tests.GroupPolicy;

public class WmiFilterQueriesTests
{
    [Fact]
    public void Format_WritesTheGpmcLayout()
    {
        var value = WmiFilterQueries.Format([new WmiFilterQuery(@"root\CIMv2", "SELECT * FROM Win32_OperatingSystem WHERE ProductType = 1")]);

        Assert.Equal(@"1;3;10;57;WQL;root\CIMv2;SELECT * FROM Win32_OperatingSystem WHERE ProductType = 1;", value);
    }

    [Fact]
    public void RoundTrip_KeepsSemicolonsAndPolishTextInQueries()
    {
        WmiFilterQuery[] queries =
        [
            new(@"root\CIMv2", "SELECT * FROM Win32_ComputerSystem WHERE Name LIKE 'WAW-%;KRK-%'"),
            new(@"root\CIMv2", "SELECT * FROM Win32_OperatingSystem WHERE Caption LIKE '%Łódź%'"),
        ];

        var parsed = WmiFilterQueries.Parse(WmiFilterQueries.Format(queries));

        Assert.Equal(queries, parsed);
    }

    [Theory]
    [InlineData("x;")]
    [InlineData("1;3;10;99;WQL;root\\CIMv2;SELECT;")]
    [InlineData("2;3;10;6;WQL;root\\CIMv2;SELECT;")]
    [InlineData("1;3;10;6;WQL;root\\CIMv2;SELECT")]
    public void Parse_RefusesMalformedValues(string value) => Assert.Null(WmiFilterQueries.Parse(value));

    [Fact]
    public void Dates_UseTheWmiForm()
    {
        var time = new DateTimeOffset(2026, 10, 27, 9, 30, 15, TimeSpan.FromHours(1));

        var text = WmiFilterQueries.FormatDate(time);

        Assert.Equal("20261027083015.000000-000", text);
        Assert.Equal(time.ToUniversalTime().AddTicks(-time.Ticks % TimeSpan.TicksPerSecond), WmiFilterQueries.ParseDate(text));
    }

    [Fact]
    public void Links_FormatAndParse()
    {
        var link = WmiFilterLinks.Format("contoso.test", "{8E0A3C12-6B4D-4F0E-9C1A-2B3D4E5F6A7B}");

        Assert.Equal("[contoso.test;{8E0A3C12-6B4D-4F0E-9C1A-2B3D4E5F6A7B};0]", link);
        Assert.Equal("{8E0A3C12-6B4D-4F0E-9C1A-2B3D4E5F6A7B}", WmiFilterLinks.ParseId(link));
        Assert.Null(WmiFilterLinks.ParseId(" "));
    }
}
