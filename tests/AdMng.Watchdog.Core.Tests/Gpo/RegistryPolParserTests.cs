using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.Core.Gpo.Parsers;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>Registry.pol ([MS-GPREG] 2.2.1) against the hand-annotated hex fixture, every special value name and malformed files.</summary>
public sealed class RegistryPolParserTests
{
    private const string Au = @"Software\Policies\Microsoft\Windows\WindowsUpdate\AU";
    private const string Hklm = "Computer/Registry/HKLM\\";

    [Fact]
    public void HexFixture()
    {
        var settings = Parse(GpoFixtures.Sysvol("Registry.pol.hex"));

        Assert.Equal(4, settings.Count);
        Assert.Equal(("REG_DWORD:1", GpoSettingCategory.Registry), Value(settings, Hklm + Au + "!NoAutoUpdate")); // design 8.5 example
        Assert.Equal("DELETE", Value(settings, Hklm + Au + "!AUOptions").Value);
        Assert.Equal("REG_SZ:Łódź – centrala", Value(settings, Hklm + @"Software\Policies\Contoso\Biuro!Nazwa").Value);
        Assert.Equal(("REG_DWORD:0", GpoSettingCategory.SecurityRegistry),
            Value(settings, Hklm + @"Software\Microsoft\Windows\CurrentVersion\Policies\System!EnableLUA"));
    }

    [Fact]
    public void BuilderProducesTheAnnotatedLayout()
    {
        var built = GpoFixtures.Pol(
            (Au, "NoAutoUpdate", RegistryFormat.RegDword, GpoFixtures.Dword(1)),
            (Au, "**del.AUOptions", RegistryFormat.RegSz, GpoFixtures.Sz(" ")),
            (@"Software\Policies\Contoso\Biuro", "Nazwa", RegistryFormat.RegSz, GpoFixtures.Sz("Łódź – centrala")),
            (@"Software\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", RegistryFormat.RegDword, GpoFixtures.Dword(0)));

        Assert.Equal(GpoFixtures.Sysvol("Registry.pol.hex"), built);
    }

    [Fact]
    public void UserPartUsesHkcu()
    {
        var settings = Parse(GpoFixtures.Pol((@"Software\Policies\Microsoft\Windows\Control Panel\Desktop", "ScreenSaveTimeOut", 1, GpoFixtures.Sz("900"))), GpoScope.User);

        Assert.Equal("REG_SZ:900", Value(settings, @"User/Registry/HKCU\Software\Policies\Microsoft\Windows\Control Panel\Desktop!ScreenSaveTimeOut").Value);
    }

    [Fact]
    public void SpecialValueNames()
    {
        var settings = Parse(GpoFixtures.Pol(
            (@"Software\Policies\A", "**delvals.", RegistryFormat.RegSz, GpoFixtures.Sz(" ")),
            (@"Software\Policies\B", "**DeleteValues", RegistryFormat.RegSz, GpoFixtures.Sz("Jeden;Dwa; ")),
            (@"Software\Policies\C", "**DeleteKeys", RegistryFormat.RegSz, GpoFixtures.Sz("Stary;Żółty")),
            (@"Software\Policies\D", "**soft.Tryb", RegistryFormat.RegDword, GpoFixtures.Dword(2)),
            (@"Software\Policies\E", "", RegistryFormat.RegNone, []),
            (@"Software\Policies\F", "**SecureKey", RegistryFormat.RegDword, GpoFixtures.Dword(1)),
            (@"Software\Policies\G", "", RegistryFormat.RegSz, GpoFixtures.Sz("domyślna"))));

        Assert.Equal("DELETE_ALL_VALUES", Value(settings, Hklm + @"Software\Policies\A!**delvals.").Value);
        Assert.Equal("DELETE", Value(settings, Hklm + @"Software\Policies\B!Jeden").Value);
        Assert.Equal("DELETE", Value(settings, Hklm + @"Software\Policies\B!Dwa").Value);
        Assert.Equal("DELETE_KEY", Value(settings, Hklm + @"Software\Policies\C\Stary").Value);
        Assert.Equal("DELETE_KEY", Value(settings, Hklm + @"Software\Policies\C\Żółty").Value);
        Assert.Equal("SOFT:REG_DWORD:2", Value(settings, Hklm + @"Software\Policies\D!Tryb").Value);
        Assert.Equal("CREATE_KEY", Value(settings, Hklm + @"Software\Policies\E").Value);
        Assert.Equal("REG_DWORD:1", Value(settings, Hklm + @"Software\Policies\F!**SecureKey").Value);
        Assert.Equal("REG_SZ:domyślna", Value(settings, Hklm + @"Software\Policies\G!(Default)").Value);
    }

    [Theory]
    [InlineData(RegistryFormat.RegExpandSz, "250053004500520000000000", "REG_EXPAND_SZ:%SER")]
    [InlineData(RegistryFormat.RegBinary, "00ff10", "REG_BINARY:00ff10")]
    [InlineData(RegistryFormat.RegDwordBigEndian, "00000102", "REG_DWORD_BIG_ENDIAN:258")]
    [InlineData(RegistryFormat.RegQword, "0000000001000000", "REG_QWORD:4294967296")]
    [InlineData(RegistryFormat.RegDword, "010203", "REG_DWORD:010203")]
    [InlineData(99u, "ab", "REG_TYPE_99:ab")]
    public void Types(uint type, string dataHex, string expected)
    {
        var settings = Parse(GpoFixtures.Pol((@"Software\Policies\T", "V", type, Convert.FromHexString(dataHex))));

        Assert.Equal(expected, Assert.Single(settings).Value);
    }

    [Fact]
    public void MultiStringIsQuoted()
    {
        var settings = Parse(GpoFixtures.Pol((@"Software\Policies\T", "Lista", RegistryFormat.RegMultiSz, GpoFixtures.MultiSz("a\"b", "Kraków", "c\\d"))));

        Assert.Equal("REG_MULTI_SZ:\"a\\\"b\",\"Kraków\",\"c\\\\d\"", Assert.Single(settings).Value);
    }

    [Fact]
    public void PasswordLikeValuesAreMasked()
    {
        var settings = Parse(GpoFixtures.Pol(
            (@"Software\Policies\Contoso\Proxy", "ProxyPassword", RegistryFormat.RegSz, GpoFixtures.Sz("Sekret-Proxy-1")),
            (@"Software\Policies\Contoso\Proxy", "ApiSecretKey", RegistryFormat.RegSz, GpoFixtures.Sz("Sekret-Api-2")),
            (@"Software\Policies\Contoso\Proxy", "**soft.AdminPwd", RegistryFormat.RegSz, GpoFixtures.Sz("Sekret-Soft-3")),
            (@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "DefaultPassword", RegistryFormat.RegSz, GpoFixtures.Sz("Sekret-Winlogon-4"))));

        Assert.Equal(4, settings.Count);
        Assert.All(settings, s => Assert.Equal(GpoSettingKind.Masked, s.Kind));
        Assert.All(settings, s => Assert.Equal(string.Empty, s.Value));
        Assert.Equal(GpoSettingCategory.SecurityRegistry, Setting(settings, Hklm + @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon!DefaultPassword").Category);
    }

    [Fact]
    public void SnmpCommunityNamesAreHiddenInTheKeyToo()
    {
        var settings = Parse(GpoFixtures.Pol(
            (@"Software\Policies\SNMP\Parameters\ValidCommunities", "tajna-spolecznosc", RegistryFormat.RegDword, GpoFixtures.Dword(4)),
            (@"Software\Policies\SNMP\Parameters\ValidCommunities", "druga-spolecznosc", RegistryFormat.RegDword, GpoFixtures.Dword(8))));

        Assert.All(settings, s => Assert.Equal(Hklm + @"Software\Policies\SNMP\Parameters\ValidCommunities!*", s.Key));
        Assert.All(settings, s => Assert.Equal(GpoSettingKind.Masked, s.Kind));
        Assert.DoesNotContain(settings, s => s.Key.Contains("spolecznosc", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyFileAndHeaderOnly()
    {
        Assert.Empty(Parse([]));
        Assert.Empty(Parse(GpoFixtures.Pol()));
    }

    public static TheoryData<string, byte[]> Malformed()
    {
        var valid = GpoFixtures.Pol((@"Software\Policies\T", "V", RegistryFormat.RegDword, GpoFixtures.Dword(1)));
        var hugeSize = valid.ToArray();
        // size field: header 8 + '[' 2 + key ("Software\Policies\T" 19 chars + NUL) 40 + ';' 2 + "V" + NUL 4 + ';' 2 + type 4 + ';' 2
        var sizeOffset = 8 + 2 + 40 + 2 + 4 + 2 + 4 + 2;
        hugeSize[sizeOffset] = 0xFF;
        hugeSize[sizeOffset + 1] = 0xFF;
        hugeSize[sizeOffset + 2] = 0xFF;
        hugeSize[sizeOffset + 3] = 0xFF;
        return new TheoryData<string, byte[]>
        {
            { "too short", "PReg"u8.ToArray() },
            { "wrong signature", [.. "GerP"u8, 1, 0, 0, 0] },
            { "version 2", [.. "PReg"u8, 2, 0, 0, 0] },
            { "truncated entry", valid[..^5] },
            { "missing closing bracket", valid[..^2] },
            { "size beyond the file", hugeSize },
            { "unterminated key", [.. "PReg"u8, 1, 0, 0, 0, .. Encoding.Unicode.GetBytes("[Software\\Policies")] },
            { "garbage after the entries", [.. valid, 0x41, 0x00] },
            { "text file", Encoding.UTF8.GetBytes("Windows Registry Editor Version 5.00\r\n") },
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedFilesFail(string reason, byte[] content)
    {
        Assert.NotNull(reason);
        Assert.Throws<GpoParseException>(() => Parse(content));
    }

    [Fact]
    public void OverLongStringFails()
    {
        byte[] content = [.. "PReg"u8, 1, 0, 0, 0, .. Encoding.Unicode.GetBytes("["), .. Encoding.Unicode.GetBytes(new string('k', 40_000))];

        Assert.Throws<GpoParseException>(() => Parse(content));
    }

    private static IReadOnlyList<GpoSetting> Parse(byte[] content, GpoScope scope = GpoScope.Computer)
    {
        var sink = new GpoSettingSink("Machine/Registry.pol");
        RegistryPolParser.Parse(content, scope, sink);
        return sink.Settings;
    }

    private static GpoSetting Setting(IReadOnlyList<GpoSetting> settings, string key) =>
        Assert.Single(settings, s => s.Key.Equals(key, StringComparison.Ordinal));

    private static (string Value, GpoSettingCategory Category) Value(IReadOnlyList<GpoSetting> settings, string key)
    {
        var setting = Setting(settings, key);
        return (setting.Value, setting.Category);
    }
}
