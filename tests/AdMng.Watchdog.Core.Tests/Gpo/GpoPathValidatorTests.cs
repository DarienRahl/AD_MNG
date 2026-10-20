using AdMng.Watchdog.Core.Gpo;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>Stage B design 8.2 (GPO path hijack) and the paths the service builds and accepts from a listing.</summary>
public sealed class GpoPathValidatorTests
{
    private const string Cn = "{6AC1786C-016F-11D2-945F-00C04FB984F9}";

    [Theory]
    [InlineData(@"\\contoso.com\SysVol\contoso.com\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}")]
    [InlineData(@"\\CONTOSO.COM\sysvol\Contoso.Com\policies\{6ac1786c-016f-11d2-945f-00c04fb984f9}")]
    public void ExpectedPathIsValid(string path)
    {
        Assert.Equal(GpoPathCheck.Valid, GpoPathValidator.Validate(path, "contoso.com", Cn));
    }

    [Theory]
    [InlineData(@"\\evil.example\SysVol\contoso.com\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}")]
    [InlineData(@"\\dc01.contoso.com\SysVol\contoso.com\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}")]
    [InlineData(@"\\contoso.com\NETLOGON\contoso.com\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}")]
    [InlineData(@"\\contoso.com\SysVol\contoso.com\Policies\{31B2F340-016D-11D2-945F-00C04FB984F9}")]
    [InlineData(@"\\contoso.com\SysVol\contoso.com\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}\")]
    [InlineData(@"\\contoso.com\SysVol\contoso.com\Policies\..\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}")]
    [InlineData(@"\\contoso.com\SysVol\contoso.com\Scripts\{6AC1786C-016F-11D2-945F-00C04FB984F9}")]
    [InlineData("//contoso.com/SysVol/contoso.com/Policies/{6AC1786C-016F-11D2-945F-00C04FB984F9}")]
    [InlineData(@"\\?\UNC\contoso.com\SysVol\contoso.com\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}")]
    [InlineData(@"\\contoso.com.\SysVol\contoso.com\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}")]
    [InlineData(@" \\contoso.com\SysVol\contoso.com\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}")]
    [InlineData(@"\\10.0.0.66\SysVol\contoso.com\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}")]
    [InlineData(@"C:\Windows\SYSVOL\domain\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}")]
    [InlineData(@"https://evil.example/gpo")]
    public void AnythingElseIsARedirection(string path)
    {
        Assert.Equal(GpoPathCheck.Redirected, GpoPathValidator.Validate(path, "contoso.com", Cn));
    }

    [Fact]
    public void MissingPathAndInvalidCn()
    {
        Assert.Equal(GpoPathCheck.Missing, GpoPathValidator.Validate(null, "contoso.com", Cn));
        Assert.Equal(GpoPathCheck.Missing, GpoPathValidator.Validate(string.Empty, "contoso.com", Cn));
        Assert.Equal(GpoPathCheck.Redirected, GpoPathValidator.Validate(GpoPathValidator.ExpectedPath("contoso.com", GpoFixtures.Gpo), "contoso.com", "Default Domain Policy"));
        Assert.False(GpoPathValidator.TryParseGpcCn("6AC1786C-016F-11D2-945F-00C04FB984F9", out _));
        Assert.False(GpoPathValidator.TryParseGpcCn("{00000000-0000-0000-0000-000000000000}", out _));
    }

    [Fact]
    public void TheServiceReadsFromThePinnedDcOnly()
    {
        var folder = SysvolGpoFolder.Create("DC01.Contoso.COM.", "contoso.com", GpoFixtures.Gpo);

        Assert.Equal("//dc01.contoso.com/SYSVOL", folder.Service);
        Assert.Equal("contoso.com/Policies/{6AC1786C-016F-11D2-945F-00C04FB984F9}", folder.Directory);
        Assert.Equal(@"\\dc01.contoso.com\SYSVOL\contoso.com\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}", folder.UncPath);
    }

    [Theory]
    [InlineData("dc01.contoso.com;rm -rf /")]
    [InlineData("dc01 -c ls")]
    [InlineData("'dc01'")]
    [InlineData("dc01/../evil")]
    [InlineData("-dc01.contoso.com")]
    [InlineData("dc01..contoso.com")]
    [InlineData("dć01.contoso.com")]
    [InlineData("10.0.0.66")]
    [InlineData("")]
    [InlineData(" ")]
    public void FolderRefusesNamesOutsideTheDnsAlphabet(string name)
    {
        Assert.Throws<ArgumentException>(() => SysvolGpoFolder.Create(name, "contoso.com", GpoFixtures.Gpo));
        Assert.Throws<ArgumentException>(() => SysvolGpoFolder.Create("dc01.contoso.com", name, GpoFixtures.Gpo));
    }

    [Fact]
    public void FolderRefusesTheEmptyGuid()
    {
        Assert.Throws<ArgumentException>(() => SysvolGpoFolder.Create("dc01.contoso.com", "contoso.com", Guid.Empty));
    }

    [Theory]
    [InlineData("GPT.INI", "GPT.INI")]
    [InlineData(@"Machine\Registry.pol", "Machine/Registry.pol")]
    [InlineData("Machine/Scripts/", "Machine/Scripts")]
    [InlineData("User/Preferences/Groups/Zażółć.xml", "User/Preferences/Groups/Zażółć.xml")]
    public void RelativePathsAreNormalized(string path, string expected)
    {
        Assert.True(GpoRelativePath.TryNormalize(path, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("../GPT.INI")]
    [InlineData("Machine/../../Policies/{31B2F340-016D-11D2-945F-00C04FB984F9}/GPT.INI")]
    [InlineData(@"Machine\..\..\x")]
    [InlineData("./GPT.INI")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\\evil\share\x")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("Machine//Registry.pol")]
    [InlineData("Machine/Registry.pol:stream")]
    [InlineData("Machine/Registry.pol\u0000.txt")]
    [InlineData("Machine/Reg\nistry.pol")]
    [InlineData("Machine/x*")]
    [InlineData("Machine/trailing.")]
    [InlineData("Machine/trailing ")]
    [InlineData("")]
    public void HostileRelativePathsAreRefused(string path)
    {
        Assert.False(GpoRelativePath.TryNormalize(path, out _));
    }

    [Fact]
    public void LoneSurrogateIsRefused()
    {
        Assert.False(GpoRelativePath.TryNormalize("Machine/" + '\uD800' + "x", out _));
        Assert.True(GpoRelativePath.TryNormalize("Machine/" + char.ConvertFromUtf32(0x1F600), out _));
    }

    [Fact]
    public void OverLongRelativePathIsRefused()
    {
        Assert.False(GpoRelativePath.TryNormalize(string.Join('/', Enumerable.Repeat("abcdefghij", 120)), out _));
    }
}
