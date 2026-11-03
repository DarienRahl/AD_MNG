using AdMng.Core.EventLogs;

namespace AdMng.Core.Tests.EventLogs;

public class XPathLiteralAndHostNamesTests
{
    [Theory]
    [InlineData("józef.żak", "'józef.żak'")]
    [InlineData("o'neill", "\"o'neill\"")]
    [InlineData("x\"y", "'x\"y'")]
    [InlineData("0x1' or '1'='1", "\"0x1' or '1'='1\"")]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-1104", "'S-1-5-21-3623811015-3361044348-30300820-1104'")]
    public void Quote(string value, string literal) => Assert.Equal(literal, XPathLiteral.Quote(value));

    [Theory]
    [InlineData("o'ne\"ill")]
    [InlineData("zofia\u0001")]
    [InlineData("a\tb")]
    [InlineData("\u0085")]
    [InlineData("")]
    public void ValuesThatCannotBeOneLiteral(string value)
    {
        Assert.False(XPathLiteral.IsAllowedValue(value));
        var error = Assert.Throws<ArgumentException>(() => XPathLiteral.Quote(value));
        if (value.Length > 1)
        {
            Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ValueLengthLimit()
    {
        Assert.True(XPathLiteral.IsAllowedValue(new string('ł', XPathLiteral.MaxValueLength)));
        Assert.False(XPathLiteral.IsAllowedValue(new string('ł', XPathLiteral.MaxValueLength + 1)));
        Assert.False(XPathLiteral.IsAllowedValue(null));
    }

    [Theory]
    [InlineData("TargetUserName", true)]
    [InlineData("Security", true)]
    [InlineData("{4FE80A66-5F93-4F73-B215-68678058E613}", true)]
    [InlineData("MSSQL$SQLEXPRESS", true)]
    [InlineData("Directory Service", true)]
    [InlineData("Microsoft-Windows-GroupPolicy/Operational", false)]
    [InlineData("Użytkownik", false)]
    [InlineData("a]b", false)]
    [InlineData("a'b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Names(string? name, bool allowed) => Assert.Equal(allowed, XPathLiteral.IsAllowedName(name));

    [Theory]
    [InlineData(" DC01.Contoso.Test. ", "dc01.contoso.test")]
    [InlineData("dc01", "dc01")]
    public void EventHostNames_Normalize(string host, string expected) => Assert.Equal(expected, EventHostNames.Normalize(host));

    [Fact]
    public void EventHostNames_AreEqual()
    {
        Assert.True(EventHostNames.AreEqual("DC01.contoso.test.", "dc01.CONTOSO.test"));
        Assert.False(EventHostNames.AreEqual("dc01.contoso.test", "dc01"));
        Assert.False(EventHostNames.AreEqual(null, "dc01"));
    }

    [Theory]
    [InlineData("dc01.contoso.test", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("::1", false)]
    [InlineData("2130706433", false)]
    [InlineData("dc01.contoso.test\r\n", false)]
    [InlineData("", false)]
    public void EventHostNames_IsHostName(string host, bool expected) => Assert.Equal(expected, EventHostNames.IsHostName(host));

    [Theory]
    [InlineData("WAW-PC001", "waw-pc001")]
    [InlineData("WAW-PC001$", "waw-pc001")]
    [InlineData(@"\\WAW-PC001", "waw-pc001")]
    [InlineData("waw-pc001.contoso.test.", "waw-pc001")]
    [InlineData("::ffff:10.10.1.21", "10.10.1.21")]
    [InlineData("-", null)]
    [InlineData(null, null)]
    public void EventHostNames_ShortName(string? host, string? expected) => Assert.Equal(expected, EventHostNames.ShortName(host));

    [Theory]
    [InlineData("WAW-PC001", "waw-pc001.contoso.test", true)]
    [InlineData(@"\\WAW-PC001", "WAW-PC001$", true)]
    [InlineData("waw-pc001.contoso.test", "WAW-PC001.CONTOSO.TEST.", true)]
    [InlineData("waw-pc001.contoso.test", "waw-pc001.fabrikam.test", false)]
    [InlineData("WAW-PC001", "WAW-PC002", false)]
    [InlineData("10.10.1.21", "10.10.1.21", true)]
    [InlineData("10.10.1.21", "10.10.1.22", false)]
    [InlineData("10", "10.0.0.1", false)]
    [InlineData("WAW-PC001", "-", false)]
    [InlineData(null, "WAW-PC001", false)]
    public void EventHostNames_SameComputer(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, EventHostNames.SameComputer(a, b));
        Assert.Equal(expected, EventHostNames.SameComputer(b, a));
    }
}
