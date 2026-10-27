using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Watchdog.Core.Gpo;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>GPT.INI ([MS-GPOL] 2.2.4), scripts.ini/psscripts.ini ([MS-GPSCR] 2.2.2), audit.csv ([MS-GPAC] 2.2.1.2) and the
/// policy comments.</summary>
public sealed class SmallParserTests
{
    [Fact]
    public void GptIniVersionHalves()
    {
        var ini = GptIniParser.Parse(GpoFixtures.Sysvol("GPT.INI"));

        Assert.Equal(131075, ini.Version);
        Assert.Equal("Zasady kontrolerów domeny – Łódź", ini.DisplayName);
        var version = new GpoVersion(ini.Version!.Value);
        Assert.Equal((3, 2), (version.Computer, version.User));
    }

    [Fact]
    public void GptIniInAnsiStillYieldsTheVersion()
    {
        byte[] ansi = [.. "[General]\r\nVersion=65537\r\ndisplayName=Zasada "u8, 0xA3, 0xF3, 0x64, 0x9F, (byte)'\r', (byte)'\n'];

        Assert.Equal(65537, GptIniParser.Parse(ansi).Version);
    }

    [Theory]
    [InlineData("[General]\r\nVersion=abc\r\n")]
    [InlineData("[General]\r\nVersion=99999999999\r\n")]
    public void GptIniWithAnInvalidVersionFails(string text)
    {
        Assert.Throws<GpoParseException>(() => GptIniParser.Parse(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void GptIniWithoutVersion()
    {
        Assert.Null(GptIniParser.Parse("[General]\r\n"u8).Version);
    }

    [Fact]
    public void VersionLag()
    {
        var gpc = new GpoVersion((2 << 16) | 5);

        Assert.True(new GpoVersion((2 << 16) | 4).IsBehind(gpc));
        Assert.True(new GpoVersion((1 << 16) | 9).IsBehind(gpc));
        Assert.False(new GpoVersion((2 << 16) | 5).IsBehind(gpc));
        Assert.False(new GpoVersion((3 << 16) | 6).IsBehind(gpc));
    }

    [Fact]
    public void ScriptsIni()
    {
        var settings = Parse(s => ScriptsIniParser.Parse(GpoFixtures.Sysvol("scripts.ini"), GpoScope.Computer, powerShell: false, s));

        Assert.Equal("mapuj-dyski.cmd", Value(settings, "Computer/Scripts/Startup/0"));
        Assert.Equal("/ciche", Value(settings, "Computer/Scripts/Startup/0/Parameters"));
        Assert.Equal(@"\\contoso.com\NETLOGON\inwentaryzacja.vbs", Value(settings, "Computer/Scripts/Startup/1"));
        Assert.DoesNotContain(settings, s => s.Key == "Computer/Scripts/Startup/1/Parameters");
        Assert.Equal("sprzątanie.cmd", Value(settings, "Computer/Scripts/Shutdown/0"));
        Assert.All(settings, s => Assert.Equal(GpoSettingCategory.Scripts, s.Category));
    }

    [Fact]
    public void PowerShellScriptsIni()
    {
        var settings = Parse(s => ScriptsIniParser.Parse(GpoFixtures.Sysvol("psscripts.ini"), GpoScope.Computer, powerShell: true, s));

        Assert.Equal("true", Value(settings, "Computer/Scripts/PowerShell/Config/StartExecutePSFirst"));
        Assert.Equal("Konfiguracja-Żółć.ps1", Value(settings, "Computer/Scripts/PowerShell/Startup/0"));
        Assert.Equal("-Tryb Pełny", Value(settings, "Computer/Scripts/PowerShell/Startup/0/Parameters"));
    }

    [Fact]
    public void UserLogonScripts()
    {
        var settings = Parse(s => ScriptsIniParser.Parse(GpoFixtures.Utf16("[Logon]\n00CmdLine=logon.cmd\n0Parameters=\n"), GpoScope.User, powerShell: false, s));

        Assert.Equal("logon.cmd", Value(settings, "User/Scripts/Logon/0"));
    }

    [Fact]
    public void ScriptsIniInBigEndianFails()
    {
        byte[] content = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("[Startup]\r\n0CmdLine=x.cmd\r\n")];

        Assert.Throws<GpoParseException>(() => Parse(s => ScriptsIniParser.Parse(content, GpoScope.Computer, powerShell: false, s)));
    }

    [Fact]
    public void AuditCsv()
    {
        var settings = Parse(s => AuditCsvParser.Parse(GpoFixtures.Sysvol("audit.csv"), s));

        Assert.Equal("Success and Failure", Value(settings, "Computer/Audit/{0cce923f-69ae-11d9-bed3-505054503030}"));
        Assert.Equal("Success", Value(settings, "Computer/Audit/{0cce9237-69ae-11d9-bed3-505054503030}"));
        Assert.Equal("Success and Failure", Value(settings, "Computer/Audit/{0cce922f-69ae-11d9-bed3-505054503030}")); // upper-case GUID in the file
        Assert.Equal("No Auditing", Value(settings, "Computer/Audit/{0cce9227-69ae-11d9-bed3-505054503030}"));
        Assert.Equal("0", Value(settings, "Computer/Audit/Option/CrashOnAuditFail"));
        Assert.Equal("1", Value(settings, "Computer/Audit/Option/AuditBaseObjects"));
        Assert.All(settings, s => Assert.Equal(GpoSettingCategory.AuditPolicy, s.Category));
    }

    [Fact]
    public void AuditCsvWithoutBomAndWithExclusion()
    {
        var csv = "Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value\n"
                  + ",System,Audit Logon,{0cce9215-69ae-11d9-bed3-505054503030},Success,Failure,1\n";
        var settings = Parse(s => AuditCsvParser.Parse(Encoding.UTF8.GetBytes(csv), s));

        Assert.Equal("Success; exclusion: Failure", Value(settings, "Computer/Audit/{0cce9215-69ae-11d9-bed3-505054503030}"));
    }

    [Theory]
    [InlineData("Subcategory,Value\n,x\n")]
    [InlineData("Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value\n,System,\"Audit Logon,{0cce9215-69ae-11d9-bed3-505054503030},Success,,1\n")]
    public void MalformedAuditCsvFails(string csv)
    {
        Assert.Throws<GpoParseException>(() => Parse(s => AuditCsvParser.Parse(Encoding.UTF8.GetBytes(csv), s)));
    }

    [Fact]
    public void PolicyComments()
    {
        var settings = Parse(s => PolicyCommentParser.ParseCmtx(GpoFixtures.Sysvol("comment.cmtx"), GpoScope.Computer, s));

        var comment = Assert.Single(settings);
        Assert.Equal("Computer/Comment/Microsoft.Policies.WindowsUpdate:AutoUpdateCfg", comment.Key);
        Assert.Equal("Aktualizacje instalowane automatycznie – zgłoszenie ZMIANA-1234 (Łódź).", comment.Value);
        Assert.Equal(GpoSettingCategory.Comments, comment.Category);
    }

    [Fact]
    public void CommentsAreCutTo200Characters()
    {
        var text = string.Concat(Enumerable.Repeat("Zażółć gęślą jaźń. ", 20));
        var xml = "<policyComments xmlns=\"http://www.microsoft.com/GroupPolicy/CommentDefinitions\"><comments><admTemplate>"
                  + "<comment policyRef=\"Lokalna:Zasada\" commentText=\"" + text + "\"/></admTemplate></comments></policyComments>";
        var cmtx = Assert.Single(Parse(s => PolicyCommentParser.ParseCmtx(GpoFixtures.Utf8Bom(xml), GpoScope.User, s)));
        var gpo = Assert.Single(Parse(s => PolicyCommentParser.ParseGpoComment(GpoFixtures.Utf16(text), s)));

        Assert.Equal("User/Comment/Lokalna:Zasada", cmtx.Key);
        Assert.Equal(GpoSettingKind.Text, cmtx.Kind);
        Assert.True(cmtx.Value.Length <= 200);
        Assert.StartsWith("Zażółć gęślą jaźń.", cmtx.Value, StringComparison.Ordinal);
        Assert.Equal(("Gpo/Comment", cmtx.Value), (gpo.Key, gpo.Value));
    }

    private static IReadOnlyList<GpoSetting> Parse(Action<GpoSettingSink> parse)
    {
        var sink = new GpoSettingSink("test");
        parse(sink);
        return sink.Settings;
    }

    private static string Value(IReadOnlyList<GpoSetting> settings, string key) => GptTmplParserTests.Setting(settings, key).Value;
}
