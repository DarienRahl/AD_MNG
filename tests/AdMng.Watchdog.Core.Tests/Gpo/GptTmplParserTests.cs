using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.Core.Gpo.Parsers;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>The security template parser against the hand-written <c>GptTmpl.inf</c> ([MS-GPSB] 2.2) and hostile variants.</summary>
public sealed class GptTmplParserTests
{
    private const string Sa = "Computer/Security/System Access/";
    private const string Rv = "Computer/Security/Registry Values/";

    [Fact]
    public void AccountAndKerberosPolicy()
    {
        var settings = Parse(GpoFixtures.Sysvol("GptTmpl.inf"));

        Assert.Equal(("14", GpoSettingCategory.AccountPolicy), Value(settings, Sa + "MinimumPasswordLength"));
        Assert.Equal("Administrator", Value(settings, Sa + "NewAdministratorName").Value);
        Assert.Equal(("10", GpoSettingCategory.AccountPolicy), Value(settings, "Computer/Security/Kerberos Policy/MaxTicketAge"));
    }

    [Fact]
    public void EventAuditValuesAreNamed()
    {
        var settings = Parse(GpoFixtures.Sysvol("GptTmpl.inf"));

        Assert.Equal(("Success and Failure", GpoSettingCategory.AuditPolicy), Value(settings, "Computer/Security/Event Audit/AuditLogonEvents"));
        Assert.Equal("Failure", Value(settings, "Computer/Security/Event Audit/AuditPrivilegeUse").Value);
        Assert.Equal("No Auditing", Value(settings, "Computer/Security/Event Audit/AuditObjectAccess").Value);
    }

    [Fact]
    public void PrivilegeRightsAreSortedSidLists()
    {
        var settings = Parse(GpoFixtures.Sysvol("GptTmpl.inf"));

        var rdp = Setting(settings, "Computer/Security/Privilege Rights/SeRemoteInteractiveLogonRight");
        Assert.True(rdp.IsList);
        Assert.Equal(GpoSettingCategory.PrivilegeRights, rdp.Category);
        Assert.Equal(["S-1-5-32-544", "S-1-5-32-555"], rdp.Elements());
        Assert.Equal(["CONTOSO\\Józef.Wąsik", "S-1-5-32-546"], Setting(settings, "Computer/Security/Privilege Rights/SeDenyNetworkLogonRight").Elements());
        var empty = Setting(settings, "Computer/Security/Privilege Rights/SeServiceLogonRight");
        Assert.True(empty.IsList);
        Assert.Empty(empty.Elements());
    }

    [Fact]
    public void RestrictedGroups()
    {
        var settings = Parse(GpoFixtures.Sysvol("GptTmpl.inf"));

        var members = Setting(settings, "Computer/Security/Group Membership/S-1-5-32-544/Members");
        Assert.Equal(GpoSettingCategory.GroupMembership, members.Category);
        Assert.Equal(
            ["S-1-5-21-1004336348-1177238915-682003330-1105", "S-1-5-21-1004336348-1177238915-682003330-512"],
            members.Elements());
        Assert.Empty(Setting(settings, "Computer/Security/Group Membership/S-1-5-32-544/MemberOf").Elements());
        Assert.Equal(["CONTOSO\\Pomoc Techniczna"], Setting(settings, "Computer/Security/Group Membership/S-1-5-32-555/Members").Elements());
    }

    [Fact]
    public void RegistryValuesAreTypedAndCategorizedByPath()
    {
        var settings = Parse(GpoFixtures.Sysvol("GptTmpl.inf"));

        Assert.Equal(("REG_DWORD:1", GpoSettingCategory.SecurityRegistry), Value(settings, Rv + @"HKLM\System\CurrentControlSet\Control\Lsa!NoLMHash"));
        Assert.Equal(GpoSettingCategory.SecurityRegistry,
            Setting(settings, Rv + @"HKLM\System\CurrentControlSet\Services\LanManServer\Parameters!RequireSecuritySignature").Category);
        Assert.Equal("REG_SZ:Ostrzeżenie", Value(settings, Rv + @"HKLM\Software\Microsoft\Windows NT\CurrentVersion\Winlogon!LegalNoticeCaption").Value);
        Assert.Equal(("REG_MULTI_SZ:\"Dostęp tylko dla upoważnionych pracowników.\",\"Zażółć gęślą jaźń\"", GpoSettingCategory.SecurityRegistry),
            Value(settings, Rv + @"HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\System!LegalNoticeText"));
        Assert.Equal(GpoSettingCategory.Registry,
            Setting(settings, Rv + @"HKLM\System\CurrentControlSet\Control\Print\Providers\LanMan Print Services\Servers!AddPrinterDrivers").Category);
    }

    [Fact]
    public void WinlogonDefaultPasswordIsMaskedAndNeverKept()
    {
        var settings = Parse(GpoFixtures.Sysvol("GptTmpl.inf"));

        var password = Setting(settings, Rv + @"HKLM\Software\Microsoft\Windows NT\CurrentVersion\Winlogon!DefaultPassword");
        Assert.Equal(GpoSettingKind.Masked, password.Kind);
        Assert.Equal(string.Empty, password.Value);
        Assert.DoesNotContain(settings, s => s.Value.Contains("Tajne", StringComparison.Ordinal) || s.Key.Contains("Tajne", StringComparison.Ordinal));
    }

    [Fact]
    public void ServicesAndObjectSecurity()
    {
        var settings = Parse(GpoFixtures.Sysvol("GptTmpl.inf"));

        Assert.Equal(("Disabled", GpoSettingCategory.Services), Value(settings, "Computer/Security/Services/Spooler/StartupMode"));
        Assert.Equal(string.Empty, Value(settings, "Computer/Security/Services/Spooler/Security").Value);
        Assert.StartsWith("D:AR(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)", Value(settings, "Computer/Security/Services/RemoteRegistry/Security").Value, StringComparison.Ordinal);
        Assert.Equal(("Propagate", GpoSettingCategory.FileRegistrySecurity), Value(settings, @"Computer/Security/Registry Keys/HKLM\SOFTWARE\Contoso/Mode"));
        Assert.Equal(("Replace", GpoSettingCategory.FileRegistrySecurity), Value(settings, @"Computer/Security/File Security/%SystemRoot%\System32\drivers\etc\hosts/Mode"));
    }

    [Fact]
    public void EventLogSectionsAndNoVersionSettings()
    {
        var settings = Parse(GpoFixtures.Sysvol("GptTmpl.inf"));

        Assert.Equal(("196608", GpoSettingCategory.EventLog), Value(settings, "Computer/Security/Security Log/MaximumLogSize"));
        Assert.DoesNotContain(settings, s => s.Key.Contains("/Unicode/", StringComparison.Ordinal) || s.Key.Contains("/Version/", StringComparison.Ordinal));
        Assert.All(settings, s => Assert.Equal(GpoFixtures.GptTmplPath, s.Source));
    }

    [Fact]
    public void EncodingsGiveTheSameSettings()
    {
        var text = GpoFixtures.Text("GptTmpl.inf");
        var expected = Parse(GpoFixtures.Utf16(text));

        Assert.Equal(expected, Parse(Encoding.Unicode.GetBytes(GpoFixtures.Crlf(text))));
        Assert.Equal(expected, Parse(GpoFixtures.Utf8Bom(text)));
        Assert.Equal(expected, Parse([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)])); // LF only
    }

    [Fact]
    public void WrongEncodingsFailTheFile()
    {
        var text = GpoFixtures.Crlf(GpoFixtures.Text("GptTmpl.inf"));

        Assert.Throws<GpoParseException>(() => Parse([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)]));
        Assert.Throws<GpoParseException>(() => Parse(Encoding.Latin1.GetBytes(text)));        // ANSI with diacritics
        Assert.Throws<GpoParseException>(() => Parse(Enumerable.Range(0, 4096).Select(i => (byte)(i * 7919 % 251)).ToArray())); // binary
        Assert.Throws<GpoParseException>(() => Parse("garbage without any section"u8));
    }

    [Fact]
    public void LongValuesAreHashedAndHugeListsCollapse()
    {
        var longText = new string('x', 300);
        var sids = string.Join(",", Enumerable.Range(1000, 600).Select(i => "*S-1-5-21-1-2-3-" + GpoFixtures.Invariant(i)));
        var settings = Parse(GpoFixtures.Utf16($"[System Access]\nNewGuestName = \"{longText}\"\n[Privilege Rights]\nSeBatchLogonRight = {sids}\n"));

        var guest = Setting(settings, Sa + "NewGuestName");
        Assert.Equal(GpoSettingKind.Hash, guest.Kind);
        Assert.Equal(GpoValues.Hash(longText), guest.Value);
        var batch = Setting(settings, "Computer/Security/Privilege Rights/SeBatchLogonRight");
        Assert.Equal(GpoSettingKind.Hash, batch.Kind);
        Assert.StartsWith(GpoValues.HashPrefix, batch.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlCharactersBecomeReplacementCharacters()
    {
        var settings = Parse(GpoFixtures.Utf16("[System Access]\nNewGuestName = \"Gość\u0001\u001b[31m\"\n"));

        Assert.Equal("Gość��[31m", Value(settings, Sa + "NewGuestName").Value);
    }

    [Fact]
    public void MalformedServiceLineFailsTheFile()
    {
        Assert.Throws<GpoParseException>(() => Parse(GpoFixtures.Utf16("[Service General Setting]\n\"\"\n")));
    }

    internal static IReadOnlyList<GpoSetting> Parse(ReadOnlySpan<byte> content)
    {
        var sink = new GpoSettingSink(GpoFixtures.GptTmplPath);
        GptTmplParser.Parse(content, sink);
        return sink.Settings;
    }

    internal static GpoSetting Setting(IReadOnlyList<GpoSetting> settings, string key) =>
        Assert.Single(settings, s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static (string Value, GpoSettingCategory Category) Value(IReadOnlyList<GpoSetting> settings, string key)
    {
        var setting = Setting(settings, key);
        return (setting.Value, setting.Category);
    }
}
