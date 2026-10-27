using AdMng.Core.GroupPolicy;

namespace AdMng.Core.Tests.GroupPolicy;

/// <summary>What a SYSVOL reader may put into a UNC path or a command line: validated host names and relative paths.</summary>
public sealed class SysvolPathsTests
{
    [Theory]
    [InlineData("dc01.contoso.test", "dc01.contoso.test")]
    [InlineData("DC01.Contoso.Test.", "DC01.Contoso.Test")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("vm", "vm")]
    public void Hosts_DnsNamesAndAddresses(string value, string expected)
    {
        Assert.True(SysvolPaths.TryNormalizeHost(value, out var host));
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(@"dc01\share")]
    [InlineData("dc01/SYSVOL")]
    [InlineData("dc01..contoso")]
    [InlineData("-dc01.contoso.test")]
    [InlineData("dc01.contoso.test\" -c ls")]
    [InlineData("łódź.contoso.test")]
    [InlineData("dc01:445")]
    public void Hosts_EverythingElseIsRefused(string value) => Assert.False(SysvolPaths.TryNormalizeHost(value, out _));

    [Fact]
    public void RelativePaths_NormalizedOrRefused()
    {
        Assert.True(SysvolPaths.TryNormalize(@"Machine\Microsoft\Windows NT\SecEdit\GptTmpl.inf", out var path));
        Assert.Equal(SecurityTemplate.RelativePath, path);
        Assert.True(SysvolPaths.TryNormalize("Machine/Preferences/Groups/Grupy – Łódź.xml", out _));
        foreach (var bad in new[] { "", "/Machine/x", @"\Machine", "Machine/../../x", "Machine//x", "./x", "x:stream", "a/b\"c", "a/b|c", "a/\u0001" })
        {
            Assert.False(SysvolPaths.TryNormalize(bad, out _), bad);
        }

        Assert.Equal("{31B2F340-016D-11D2-945F-00C04FB984F9}", SysvolPaths.FolderName(Guid.Parse("31b2f340-016d-11d2-945f-00c04fb984f9")));
    }
}
