using System.Text;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.Tests.GroupPolicy.Content;

/// <summary>
/// A whole GPO folder as GPMC lays it out, with every kind of file the shared parsers read (written by hand from
/// [MS-GPSB], [MS-GPAC], [MS-GPREG], [MS-GPPREF], [MS-GPSCR]; passwords are made up), and the settings the real parsers
/// make of it (<see cref="GroupPolicyReader.Settings"/>).
/// </summary>
internal static class GpoContentSamples
{
    public const string GptTmplPath = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf";
    public const string AuditPath = "Machine/Microsoft/Windows NT/Audit/audit.csv";

    public const string GptTmpl = """
        [Unicode]
        Unicode=yes
        [System Access]
        MinimumPasswordAge = 1
        MaximumPasswordAge = 42
        MinimumPasswordLength = 14
        PasswordComplexity = 1
        PasswordHistorySize = 24
        LockoutBadCount = 5
        ResetLockoutCount = 30
        LockoutDuration = 30
        RequireLogonToChangePassword = 0
        NewAdministratorName = "Administrator"
        EnableGuestAccount = 0
        [Kerberos Policy]
        MaxTicketAge = 10
        MaxRenewAge = 7
        MaxServiceAge = 600
        MaxClockSkew = 5
        TicketValidateClient = 1
        [Event Audit]
        AuditLogonEvents = 3
        AuditPrivilegeUse = 2
        AuditObjectAccess = 0
        [System Log]
        MaximumLogSize = 32768
        [Security Log]
        MaximumLogSize = 196608
        [Application Log]
        RetentionDays = 7
        [Registry Values]
        MACHINE\System\CurrentControlSet\Control\Lsa\NoLMHash=4,1
        MACHINE\System\CurrentControlSet\Control\Lsa\LmCompatibilityLevel=4,5
        MACHINE\System\CurrentControlSet\Services\LanManServer\Parameters\RequireSecuritySignature=4,1
        MACHINE\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\DefaultPassword=1,"Tajne!Hasło2026"
        MACHINE\Software\Microsoft\Windows\CurrentVersion\Policies\System\LegalNoticeCaption=1,"Ostrzeżenie"
        MACHINE\Software\Microsoft\Windows\CurrentVersion\Policies\System\LegalNoticeText=7,Dostęp tylko dla upoważnionych.,Zażółć gęślą jaźń
        MACHINE\Software\Microsoft\Windows\CurrentVersion\Policies\System\InactivityTimeoutSecs=4,900
        MACHINE\System\CurrentControlSet\Services\SNMP\Parameters\ValidCommunities\publiczna=4,4
        MACHINE\Software\Contoso\Łódź\Ustawienie=4,2
        [Privilege Rights]
        SeDebugPrivilege = *S-1-5-32-544
        SeRemoteInteractiveLogonRight = *S-1-5-32-555,*S-1-5-32-544
        SeDenyNetworkLogonRight = *S-1-5-32-546,CONTOSO\Józef.Wąsik
        SeServiceLogonRight =
        [Group Membership]
        *S-1-5-32-544__Memberof =
        *S-1-5-32-544__Members = *S-1-5-21-1004336348-1177238915-682003330-512
        CONTOSO\Pomoc Techniczna__Members = CONTOSO\Łukasz.Żak
        Administratorzy bez separatora = CONTOSO\Ewa
        [Service General Setting]
        "Spooler",4,""
        "RemoteRegistry",2,"D:AR(A;;CCLCSWRPWPDTLOCRRC;;;SY)"
        [Registry Keys]
        "MACHINE\SOFTWARE\Contoso",0,"D:PAR(A;CI;KA;;;BA)"
        [File Security]
        "%SystemRoot%\System32\drivers\etc\hosts",2,"D:PAR(A;;FA;;;BA)"
        [Profil Łódzki]
        Klucz = wartość
        [Version]
        signature="$CHICAGO$"
        Revision=1
        """;

    public const string AuditCsv = """
        Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value
        ,System,Audit Credential Validation,{0cce923f-69ae-11d9-bed3-505054503030},Success and Failure,,3
        ,System,Audit Security Group Management,{0CCE9237-69AE-11D9-BED3-505054503030},Success,,1
        ,System,Audit Logon,,Success,,1
        ,System,Audit Nieznana Podkategoria,,Success,,1
        ,,Option:CrashOnAuditFail,,Disabled,,0
        ,File System,Global SACL,,,,"S:(AU;FA;FA;;;WD)"
        ,Registry,Global SACL,,,,"S:(AU;FA;KA;;;WD)"
        """;

    public const string GroupsXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Groups clsid="{3125E937-EB16-4b4c-9934-544FC6D24D26}">
          <User clsid="{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}" name="Administrator (built-in)" image="2" changed="2026-10-01 09:15:00" uid="{A1B2C3D4-0000-4000-8000-000000000001}">
            <Properties action="U" newName="" fullName="" description="Konto administratora – stacje w Łodzi" cpassword="Zm9vYmFyLW5vdC1hLXJlYWwtY3Bhc3N3b3JkLTIwMjY=" neverExpires="1" acctDisabled="0" userName="Administrator (built-in)"/>
          </User>
          <Group clsid="{6D4A79E4-529C-4481-ABD0-F5BD7EA93BA7}" name="Administrators (built-in)" image="2" uid="{A1B2C3D4-0000-4000-8000-000000000002}" userContext="0" removePolicy="0">
            <Properties action="U" newName="" description="" deleteAllUsers="0" deleteAllGroups="0" removeAccounts="0" groupSid="S-1-5-32-544" groupName="Administrators (built-in)">
              <Members>
                <Member name="CONTOSO\Administratorzy stacji" action="ADD" sid="S-1-5-21-1004336348-1177238915-682003330-1110"/>
                <Member name="CONTOSO\Łukasz.Żak" action="ADD" sid="S-1-5-21-1004336348-1177238915-682003330-1111"/>
              </Members>
            </Properties>
            <Filters>
              <FilterOrgUnit bool="AND" not="0" name="OU=Stacje robocze,DC=contoso,DC=com" userContext="0" directMember="0"/>
            </Filters>
          </Group>
        </Groups>
        """;

    public const string ScheduledTasksXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <ScheduledTasks clsid="{CC63F200-7309-4ba0-B154-A71CD118DBCC}">
          <ImmediateTaskV2 clsid="{9756B581-76EC-4169-9AFC-0CA8D43ADB5F}" name="Aktualizacja agenta" uid="{B1B2C3D4-0000-4000-8000-000000000001}">
            <Properties action="C" name="Aktualizacja agenta" runAs="NT AUTHORITY\System" logonType="S4U">
              <Task version="1.3">
                <Settings><Enabled>true</Enabled><Hidden>false</Hidden></Settings>
                <Actions Context="Author"><Exec><Command>\\contoso.com\NETLOGON\agent\instaluj.cmd</Command></Exec></Actions>
              </Task>
            </Properties>
          </ImmediateTaskV2>
          <Task clsid="{2DEECB1C-261F-4e13-9B21-16FB83BC03BD}" name="Kopia zapasowa" uid="{B1B2C3D4-0000-4000-8000-000000000002}">
            <Properties action="U" name="Kopia zapasowa" appName="C:\Narzędzia\kopia.exe" runAs="CONTOSO\svc-kopia" cpassword="UHJ6eWvFgmFkb3dl" enabled="1">
              <Triggers>
                <Trigger type="DAILY" startHour="2" startMinutes="0"/>
                <Trigger type="WEEKLY" startHour="3" startMinutes="0"/>
              </Triggers>
            </Properties>
          </Task>
        </ScheduledTasks>
        """;

    public const string RegistryXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <RegistrySettings clsid="{A3CCFC41-DFDB-43a5-8D26-0FE8B954DA51}">
          <Collection clsid="{53B533F5-224C-47e3-B01B-CA3B3F3FF4BF}" name="Uruchamianie" uid="{C1B2C3D4-0000-4000-8000-000000000009}">
            <Registry clsid="{9CD4B2F4-923D-47f5-A062-E897DD1DAD50}" name="Agent" uid="{C1B2C3D4-0000-4000-8000-000000000002}">
              <Properties action="U" hive="HKEY_LOCAL_MACHINE" key="SOFTWARE\Microsoft\Windows\CurrentVersion\Run" name="Agent" type="REG_SZ" value="C:\Program Files\Contoso\agent.exe"/>
            </Registry>
          </Collection>
          <Registry clsid="{9CD4B2F4-923D-47f5-A062-E897DD1DAD50}" name="public-monitoring" uid="{C1B2C3D4-0000-4000-8000-000000000003}">
            <Properties action="U" hive="HKEY_LOCAL_MACHINE" key="SOFTWARE\Policies\SNMP\Parameters\ValidCommunities" name="public-monitoring" type="REG_DWORD" value="00000004"/>
          </Registry>
        </RegistrySettings>
        """;

    /// <summary>Items without a uid (keys <c>&lt;Element&gt;#n</c>).</summary>
    public const string ServicesXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <NTServices clsid="{2CFB484A-4E96-4b5d-A0B6-093D2F91E6AE}">
          <NTService clsid="{AB6F0B67-341F-4e51-92F9-005FBFBA1A43}" name="Spooler" disabled="1">
            <Properties startupType="DISABLED" serviceName="Spooler" serviceAction="STOP" timeout="30"/>
          </NTService>
        </NTServices>
        """;

    public const string ShortcutsXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Shortcuts clsid="{872ECB34-B2EC-401b-A585-D32574AA90EE}">
          <Shortcut clsid="{4F2F7C55-2790-433e-8127-0739D1CFA327}" name="Intranet" uid="{D1B2C3D4-0000-4000-8000-000000000002}" desc="Strona główna – Kraków">
            <Properties targetType="URL" action="U" targetPath="https://intranet.contoso.com/" shortcutPath="%DesktopDir%\Intranet"/>
          </Shortcut>
        </Shortcuts>
        """;

    public const string ScriptsIni = """
        [Startup]
        0CmdLine=mapuj-dyski.cmd
        0Parameters=/ciche
        1CmdLine=\\contoso.com\NETLOGON\inwentaryzacja.vbs
        [Shutdown]
        0CmdLine=sprzątanie.cmd
        CosInnego=1
        """;

    public const string PsScriptsIni = """
        [ScriptsConfig]
        StartExecutePSFirst=true
        [Startup]
        0CmdLine=Konfiguracja-Żółć.ps1
        0Parameters=-Tryb Pełny
        """;

    public const string UserScriptsIni = """
        [Logon]
        0CmdLine=logowanie.cmd
        """;

    public const string CommentCmtx = """
        <?xml version='1.0' encoding='utf-8'?>
        <policyComments xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" revision="1.0" schemaVersion="1.0" xmlns="http://www.microsoft.com/GroupPolicy/CommentDefinitions">
          <policyNamespaces>
            <using prefix="ns0" namespace="Microsoft.Policies.WindowsUpdate"></using>
          </policyNamespaces>
          <comments>
            <admTemplate>
              <comment policyRef="ns0:AutoUpdateCfg" commentText="$(resource.ns0_AutoUpdateCfg)"></comment>
            </admTemplate>
          </comments>
          <resources minRequiredRevision="1.0">
            <stringTable>
              <string id="ns0_AutoUpdateCfg">Aktualizacje automatyczne – zgłoszenie ZMIANA-1234 (Łódź).</string>
            </stringTable>
          </resources>
        </policyComments>
        """;

    /// <summary>A value name holding '!' (key paths end at the first one).</summary>
    public const string BangValueName = "https://intranet.contoso.test/#!/start";

    /// <summary>The computer part's Registry.pol: every entry kind of [MS-GPREG] 2.2.1.</summary>
    public static byte[] MachineRegistryPol() => RegistryPolFile.Write(
    [
        new(@"Software\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", RegistryFormat.RegDword, RegistryPolFile.DwordData(0)),
        new(@"Software\Policies\Contoso\Pulpit", "Tapeta", RegistryFormat.RegSz, RegistryPolFile.StringData(@"C:\Tapety\Zażółć gęślą jaźń.jpg")),
        new(@"Software\Policies\Contoso\Pulpit", "Folder", RegistryFormat.RegExpandSz, RegistryPolFile.StringData(@"%ProgramData%\Contoso")),
        new(@"Software\Policies\Contoso\Pulpit", "Serwery", RegistryFormat.RegMultiSz, RegistryPolFile.MultiStringData(["srv-łódź-01", "srv \"główny\""])),
        new(@"Software\Policies\Contoso\Pulpit", "Klucz", RegistryFormat.RegBinary, [0x0A, 0x1B]),
        new(@"Software\Policies\Contoso\Pulpit", "Rozmiar", RegistryFormat.RegQword, RegistryPolFile.QwordData(10_000_000_000)),
        new(@"Software\Policies\Contoso\Pulpit", string.Empty, RegistryFormat.RegSz, RegistryPolFile.StringData("domyślna")),
        new(@"Software\Policies\Contoso\Stare", "**del.Usunięta", RegistryFormat.RegSz, RegistryPolEntry.DeletionData),
        new(@"Software\Policies\Contoso\Lista", "**delvals.", RegistryFormat.RegSz, RegistryPolEntry.DeletionData),
        new(@"Software\Policies\Contoso\Stare", "**DeleteValues", RegistryFormat.RegSz, RegistryPolFile.StringData("Pierwsza;Druga")),
        new(@"Software\Policies\Contoso", "**DeleteKeys", RegistryFormat.RegSz, RegistryPolFile.StringData("Przestarzałe;Inne")),
        new(@"Software\Policies\Contoso\Miękkie", "**soft.Opóźnienie", RegistryFormat.RegSz, RegistryPolFile.StringData("30")),
        new(@"Software\Policies\Contoso\Pusty", string.Empty, RegistryFormat.RegNone, []),
        new(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "DefaultPassword", RegistryFormat.RegSz, RegistryPolFile.StringData("Hasło!Autologon1")),
        new(@"Software\Policies\SNMP\Parameters\ValidCommunities", "tajna-społeczność", RegistryFormat.RegDword, RegistryPolFile.DwordData(4)),
        new(@"Software\Policies\Contoso\Przegladarka\ZaufaneWitryny", BangValueName, RegistryFormat.RegSz, RegistryPolFile.StringData("1")),
    ]);

    /// <summary>The user part's Registry.pol.</summary>
    public static byte[] UserRegistryPol() => RegistryPolFile.Write(
    [
        new(@"Software\Policies\Microsoft\Windows\Control Panel\Desktop", "ScreenSaveTimeOut", RegistryFormat.RegSz, RegistryPolFile.StringData("900")),
    ]);

    /// <summary>UTF-16LE with BOM and CRLF (GptTmpl.inf, scripts.ini, psscripts.ini, GPO.cmt).</summary>
    public static byte[] Utf16(string text) =>
        [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal))];

    /// <summary>UTF-8 with BOM and CRLF (GPP XML, comment.cmtx, audit.csv).</summary>
    public static byte[] Utf8Bom(string text) =>
        [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal))];

    /// <summary>The sample folder: every parsed file kind, plus an unreadable preferences file of the user part.</summary>
    public static Dictionary<string, byte[]> Files() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["GPT.INI"] = Encoding.ASCII.GetBytes("[General]\r\nVersion=65539\r\ndisplayName=Stacje robocze – Łódź\r\n"),
        ["GPO.cmt"] = Utf16("Zasada dla stacji roboczych – właściciel: zespół Tier 1."),
        [GptTmplPath] = Utf16(GptTmpl),
        [AuditPath] = Utf8Bom(AuditCsv),
        ["Machine/Registry.pol"] = MachineRegistryPol(),
        ["User/Registry.pol"] = UserRegistryPol(),
        ["Machine/Preferences/Groups/Groups.xml"] = Utf8Bom(GroupsXml),
        ["Machine/Preferences/ScheduledTasks/ScheduledTasks.xml"] = Utf8Bom(ScheduledTasksXml),
        ["Machine/Preferences/Registry/Registry.xml"] = Utf8Bom(RegistryXml),
        ["Machine/Preferences/Services/Services.xml"] = Utf8Bom(ServicesXml),
        ["User/Preferences/Shortcuts/Shortcuts.xml"] = Utf8Bom(ShortcutsXml),
        ["User/Preferences/Drives/Drives.xml"] = Utf8Bom("<?xml version=\"1.0\"?><Drives><Drive uid=\"{1}\"><Properties letter=\"Z\""),
        ["Machine/Scripts/scripts.ini"] = Utf16(ScriptsIni),
        ["Machine/Scripts/psscripts.ini"] = Utf16(PsScriptsIni),
        ["User/Scripts/scripts.ini"] = Utf16(UserScriptsIni),
        ["Machine/Scripts/Startup/mapuj-dyski.cmd"] = Encoding.ASCII.GetBytes("@echo off\r\nnet use Z: \\\\fs01\\dane\r\n"),
        ["Machine/comment.cmtx"] = Utf8Bom(CommentCmtx),
    };

    /// <summary>The settings the real parsers make of <see cref="Files"/>.</summary>
    public static IReadOnlyList<GpoSetting> Settings() => GroupPolicyReader.Settings(new GpoFolderContent([], Files()));

    /// <summary>A security template with every option of the K4 catalog (a value inside its range).</summary>
    public static byte[] CatalogTemplate()
    {
        var text = new StringBuilder("[Unicode]\nUnicode=yes\n");
        foreach (var section in new[] { SecurityTemplateDocument.SystemAccess, SecurityTemplateDocument.KerberosPolicy, SecurityTemplateDocument.RegistryValues })
        {
            text.Append('[').Append(section).Append("]\n");
            foreach (var option in SecuritySettingsCatalog.Options.Where(o => o.Section == section))
            {
                var value = option.Kind switch
                {
                    SecurityValueKind.Text => "\"Tekst Żółć\"",
                    SecurityValueKind.MultiText => "Linia pierwsza,Linia druga",
                    SecurityValueKind.Choice => option.Choices![0].ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => option.Minimum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                text.Append(option.Key)
                    .Append(option.Location == SecurityOptionLocation.RegistryValue ? "=" + option.RegistryType + "," : " = ")
                    .Append(value).Append('\n');
            }
        }

        return Utf16(text.ToString());
    }

    /// <summary>An audit.csv with every subcategory of the K4 catalog (GUIDs upper case as some tools write them).</summary>
    public static byte[] CatalogAuditCsv()
    {
        var text = new StringBuilder(string.Join(",", AuditPolicyDocument.StandardColumns)).Append('\n');
        foreach (var subcategory in SecuritySettingsCatalog.AuditSubcategories)
        {
            text.Append(",System,").Append(subcategory.CsvName).Append(',').Append(subcategory.Guid.ToString("B").ToUpperInvariant()).Append(",Success,,1\n");
        }

        return Utf8Bom(text.ToString());
    }
}
