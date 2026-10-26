using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.Core.Gpo.Parsers;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>Group Policy Preferences XML ([MS-GPPREF]) against the hand-written fixtures and hostile XML.</summary>
public sealed class GppXmlParserTests
{
    private const string Groups = "Computer/Preferences/Groups/";
    private const string AdminUserItem = Groups + "{A1B2C3D4-0000-4000-8000-000000000001}";
    private const string AdminUser = AdminUserItem + "/";
    private const string AdminsGroupItem = Groups + "{A1B2C3D4-0000-4000-8000-000000000002}";
    private const string AdminsGroup = AdminsGroupItem + "/";
    private const string Tasks = "Computer/Preferences/ScheduledTasks/";
    private const string Registry = "Computer/Preferences/Registry/";

    [Fact]
    public void GroupMembersAreOneSortedList()
    {
        var settings = Parse("Groups.xml", "Groups");

        var members = GptTmplParserTests.Setting(settings, AdminsGroup + "Members");
        Assert.True(members.IsList);
        Assert.Equal(GpoSettingCategory.PreferenceGroups, members.Category);
        Assert.Equal(
        [
            "Member action=ADD name=CONTOSO\\Administratorzy stacji sid=S-1-5-21-1004336348-1177238915-682003330-1110",
            "Member action=ADD name=CONTOSO\\Łukasz.Żak sid=S-1-5-21-1004336348-1177238915-682003330-1111",
        ], members.Elements());
        Assert.Equal("S-1-5-32-544", GptTmplParserTests.Setting(settings, AdminsGroup + "groupSid").Value);
        Assert.Equal("Administrators (built-in)", GptTmplParserTests.Setting(settings, AdminsGroupItem + "@name").Value);
        Assert.Equal("Group", GptTmplParserTests.Setting(settings, AdminsGroupItem + "@element").Value);
        Assert.Equal(["FilterOrgUnit bool=AND directMember=0 name=OU=Stacje robocze,DC=contoso,DC=com not=0 userContext=0"],
            GptTmplParserTests.Setting(settings, AdminsGroup + "Filters").Elements());
    }

    [Fact]
    public void CpasswordIsMaskedAndOnlyItsPresenceKept()
    {
        var settings = Parse("Groups.xml", "Groups");

        var password = GptTmplParserTests.Setting(settings, AdminUser + "cpassword");
        Assert.Equal(GpoSettingKind.Masked, password.Kind);
        Assert.Equal(string.Empty, password.Value);
        Assert.True(GpoSettingsDiffer.HasGppPassword(settings));
        // the item's "changed" time makes a new password visible as an edit; items without a secret do not get it
        Assert.Equal("2026-10-01 09:15:00", GptTmplParserTests.Setting(settings, AdminUserItem + "@changed").Value);
        Assert.DoesNotContain(settings, s => s.Key.EndsWith("changed", StringComparison.Ordinal) && s.Key.StartsWith(AdminsGroupItem, StringComparison.Ordinal));
        Assert.Equal("Konto lokalnego administratora – stacje w Łodzi", GptTmplParserTests.Setting(settings, AdminUser + "description").Value);
        AssertNowhere(settings, "Zm9vYmFyLW5vdC1hLXJlYWwtY3Bhc3N3b3JkLTIwMjY=");
    }

    [Fact]
    public void ScheduledTaskDefinitionsAreFlattened()
    {
        var settings = Parse("ScheduledTasks.xml", "ScheduledTasks");
        const string Immediate = Tasks + "{B1B2C3D4-0000-4000-8000-000000000001}/";
        const string Legacy = Tasks + "{B1B2C3D4-0000-4000-8000-000000000002}/";

        var command = GptTmplParserTests.Setting(settings, Immediate + "Task/Actions/Exec/Command");
        Assert.Equal(@"\\contoso.com\NETLOGON\agent\install.cmd", command.Value);
        Assert.Equal(GpoSettingCategory.PreferenceScheduledTasks, command.Category);
        Assert.Equal("/quiet", GptTmplParserTests.Setting(settings, Immediate + "Task/Actions/Exec/Arguments").Value);
        Assert.Equal("HighestAvailable", GptTmplParserTests.Setting(settings, Immediate + "Task/Principals/Principal/RunLevel").Value);
        Assert.Equal("Author", GptTmplParserTests.Setting(settings, Immediate + "Task/Principals/Principal@id").Value);
        Assert.Equal("1.3", GptTmplParserTests.Setting(settings, Immediate + "Task@version").Value);
        Assert.Equal("Instaluje agenta – wersja 2", GptTmplParserTests.Setting(settings, Immediate + "Task/RegistrationInfo/Description").Value);
        Assert.Equal("ImmediateTaskV2", GptTmplParserTests.Setting(settings, Tasks + "{B1B2C3D4-0000-4000-8000-000000000001}@element").Value);
        Assert.Equal(@"C:\Narzędzia\kopia.exe", GptTmplParserTests.Setting(settings, Legacy + "appName").Value);
        Assert.Equal("DAILY", GptTmplParserTests.Setting(settings, Legacy + "Triggers/Trigger@type").Value);
        Assert.Equal(GpoSettingKind.Masked, GptTmplParserTests.Setting(settings, Legacy + "cpassword").Kind);
        AssertNowhere(settings, "UHJ6eWvFgmFkb3dlLWhhc8WCby1uaWUtcHJhd2R6aXdl");
    }

    [Fact]
    public void RegistryPreferences()
    {
        var settings = Parse("Registry.xml", "Registry");
        const string Password = Registry + "{C1B2C3D4-0000-4000-8000-000000000001}/";
        const string Run = Registry + "{C1B2C3D4-0000-4000-8000-000000000002}/";
        const string Snmp = Registry + "{C1B2C3D4-0000-4000-8000-000000000003}/";
        const string Wallpaper = Registry + "{C1B2C3D4-0000-4000-8000-000000000004}/";

        Assert.Equal(GpoSettingKind.Masked, GptTmplParserTests.Setting(settings, Password + "value").Kind);
        Assert.Equal(GpoSettingCategory.SecurityRegistry, GptTmplParserTests.Setting(settings, Password + "value").Category);
        AssertNowhere(settings, "Hasło!Autologon1");
        Assert.Equal(@"C:\Program Files\Contoso\agent.exe", GptTmplParserTests.Setting(settings, Run + "value").Value);
        Assert.Equal(GpoSettingCategory.SecurityRegistry, GptTmplParserTests.Setting(settings, Run + "value").Category);
        Assert.Equal("Uruchamianie", GptTmplParserTests.Setting(settings, Registry + "{C1B2C3D4-0000-4000-8000-000000000009}@name").Value);
        Assert.Equal(GpoSettingKind.Masked, GptTmplParserTests.Setting(settings, Snmp + "name").Kind);
        Assert.Equal(GpoSettingKind.Masked, GptTmplParserTests.Setting(settings, Snmp[..^1] + "@name").Kind);
        AssertNowhere(settings, "public-monitoring");
        Assert.Equal((@"C:\Tapety\Zażółć gęślą jaźń.jpg", GpoSettingCategory.Registry),
            (GptTmplParserTests.Setting(settings, Wallpaper + "value").Value, GptTmplParserTests.Setting(settings, Wallpaper + "value").Category));
        Assert.DoesNotContain(settings, s => s.Key.EndsWith("/status", StringComparison.Ordinal) || s.Key.EndsWith("/image", StringComparison.Ordinal));
    }

    [Fact]
    public void ShortcutsIntoStartupFoldersAreSensitive()
    {
        var settings = Parse("Shortcuts.xml", "Shortcuts", GpoScope.User);

        Assert.All(settings, s => Assert.StartsWith("User/Preferences/Shortcuts/", s.Key, StringComparison.Ordinal));
        Assert.Equal(GpoSettingCategory.PreferenceSystemFiles,
            GptTmplParserTests.Setting(settings, "User/Preferences/Shortcuts/{D1B2C3D4-0000-4000-8000-000000000001}/shortcutPath").Category);
        Assert.Equal(GpoSettingCategory.Preferences,
            GptTmplParserTests.Setting(settings, "User/Preferences/Shortcuts/{D1B2C3D4-0000-4000-8000-000000000002}/shortcutPath").Category);
    }

    [Fact]
    public void ItemWithoutUidGetsAPositionalId()
    {
        var settings = Parse(GpoFixtures.Utf8Bom("<EnvironmentVariables><EnvironmentVariable name=\"PATH\"><Properties name=\"PATH\" value=\"C:\\x\"/></EnvironmentVariable></EnvironmentVariables>"),
            "EnvironmentVariables");

        Assert.Equal("C:\\x", GptTmplParserTests.Setting(settings, "Computer/Preferences/EnvironmentVariables/EnvironmentVariable#1/value").Value);
    }

    [Fact]
    public void InvalidCharacterReferencesBecomeReplacementCharacters()
    {
        var settings = Parse(GpoFixtures.Utf8Bom("<Groups><User uid=\"{1}\"><Properties description=\"a&#1;b\"/></User></Groups>"), "Groups");

        Assert.Equal("a\uFFFDb", GptTmplParserTests.Setting(settings, "Computer/Preferences/Groups/{1}/description").Value);
    }

    [Theory]
    [InlineData("billion-laughs.xml")]
    [InlineData("xxe.xml")]
    [InlineData("not-well-formed.xml")]
    public void HostileFixturesFail(string fixture)
    {
        Assert.Throws<GpoParseException>(() => Parse(fixture, "Groups"));
    }

    [Fact]
    public void DeepNestingFails()
    {
        var xml = "<Groups>" + string.Concat(Enumerable.Repeat("<a>", 200)) + string.Concat(Enumerable.Repeat("</a>", 200)) + "</Groups>";

        Assert.Throws<GpoParseException>(() => Parse(GpoFixtures.Utf8Bom(xml), "Groups"));
    }

    [Fact]
    public void WrongEncodingsFail()
    {
        var xml = GpoFixtures.Text("Groups.xml");

        // declared UTF-16, bytes UTF-8 without BOM
        Assert.Throws<GpoParseException>(() => Parse(Encoding.UTF8.GetBytes(xml.Replace("utf-8", "utf-16", StringComparison.Ordinal)), "Groups"));
        // UTF-8 declared, Latin-1 bytes for the diacritics: invalid UTF-8 fails the file
        Assert.Throws<GpoParseException>(() => Parse(
            Encoding.Latin1.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><Groups><User uid=\"{1}\"><Properties description=\"Józef\"/></User></Groups>"), "Groups"));
        // empty and binary
        Assert.Throws<GpoParseException>(() => Parse([], "Groups"));
        Assert.Throws<GpoParseException>(() => Parse([0x00, 0x01, 0x02, 0xFF], "Groups"));
    }

    [Fact]
    public void Utf16WithBomIsAccepted()
    {
        var xml = GpoFixtures.Text("Groups.xml").Replace("utf-8", "utf-16", StringComparison.Ordinal);

        Assert.Equal(Parse("Groups.xml", "Groups"), Parse(GpoFixtures.Utf16(xml), "Groups"));
    }

    private static IReadOnlyList<GpoSetting> Parse(string fixture, string type, GpoScope scope = GpoScope.Computer) =>
        Parse(GpoFixtures.Sysvol(fixture), type, scope);

    private static IReadOnlyList<GpoSetting> Parse(byte[] content, string type, GpoScope scope = GpoScope.Computer)
    {
        var sink = new GpoSettingSink("Machine/Preferences/" + type + "/" + type + ".xml");
        GppXmlParser.Parse(content, scope, type, sink);
        return sink.Settings;
    }

    private static void AssertNowhere(IReadOnlyList<GpoSetting> settings, string secret) =>
        Assert.DoesNotContain(settings, s => s.Key.Contains(secret, StringComparison.Ordinal) || s.Value.Contains(secret, StringComparison.Ordinal));
}
